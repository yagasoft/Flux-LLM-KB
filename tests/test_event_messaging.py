from __future__ import annotations

import asyncio
import json
from types import SimpleNamespace
from datetime import UTC, datetime

import pytest

from flux_llm_kb import callback_dispatcher, callbacks, event_scheduler, event_subscriber, messaging, outbox_relay


def test_flux_message_envelope_rejects_raw_content_payload():
    with pytest.raises(ValueError, match="raw content"):
        messaging.build_message(
            message_type="flux.corpus.process",
            routing_key="corpus.process",
            job_id="job-1",
            payload={"root_name": "docs", "raw_content": "private body"},
        )


def test_flux_message_envelope_bounds_serialised_payload():
    with pytest.raises(ValueError, match="exceeds"):
        messaging.build_message(
            message_type="flux.corpus.process",
            routing_key="corpus.process",
            job_id="job-1",
            payload={"paths": ["a" * 70_000]},
            max_payload_bytes=1024,
        )


def test_flux_message_envelope_is_traceable_and_json_serialisable():
    message = messaging.build_message(
        message_type="flux.corpus.process",
        routing_key="corpus.process",
        job_id="job-1",
        correlation_id="corr-1",
        causation_id="cause-1",
        payload={"root_name": "docs", "path": "safe.md"},
        created_at=datetime(2026, 1, 1, tzinfo=UTC),
    )

    payload = message.to_broker_payload()

    assert payload["message_id"]
    assert payload["correlation_id"] == "corr-1"
    assert payload["causation_id"] == "cause-1"
    assert payload["schema_version"] == 1
    assert payload["job_id"] == "job-1"
    assert payload["payload"] == {"root_name": "docs", "path": "safe.md"}
    assert json.loads(json.dumps(payload))["routing_key"] == "corpus.process"


def test_rabbitmq_topology_uses_quorum_retry_and_dead_lettering():
    topology = messaging.default_topology()
    queues = {queue.name: queue for queue in topology.queues}

    assert topology.exchanges["commands"].name == "flux.commands"
    assert topology.exchanges["events"].name == "flux.events"
    assert topology.exchanges["callbacks"].name == "flux.callbacks"
    assert topology.exchanges["dead"].name == "flux.dead"
    assert queues["flux.commands.corpus"].arguments["x-queue-type"] == "quorum"
    assert queues["flux.commands.corpus"].arguments["x-dead-letter-exchange"] == "flux.retry"
    assert queues["flux.commands.corpus_host_agent"].routing_key == "corpus.host_agent.process"
    assert queues["flux.commands.corpus_host_agent"].arguments["x-dead-letter-exchange"] == "flux.retry"
    assert queues["flux.retry.corpus_host_agent"].arguments["x-dead-letter-exchange"] == "flux.commands"
    assert queues["flux.retry.corpus_host_agent"].arguments["x-dead-letter-routing-key"] == "corpus.host_agent.process"
    assert queues["flux.retry.corpus"].arguments["x-message-ttl"] >= 1000
    assert queues["flux.retry.corpus"].arguments["x-dead-letter-exchange"] == "flux.commands"
    assert queues["flux.callbacks.dispatch"].arguments["x-dead-letter-exchange"] == "flux.retry"
    assert queues["flux.retry.callback"].arguments["x-dead-letter-exchange"] == "flux.callbacks"
    assert queues["flux.dead.letters"].arguments["x-queue-type"] == "quorum"


def test_rabbitmq_topology_declares_durable_event_subscriber_queues():
    topology = messaging.default_topology()
    queues = {queue.name: queue for queue in topology.queues}

    for queue_name in ("flux.events.audit", "flux.events.dashboard", "flux.events.diagnostics"):
        queue = queues[queue_name]
        retry_queue = queues[f"flux.retry.events.{queue_name.rsplit('.', 1)[-1]}"]
        assert queue.exchange == "flux.events"
        assert queue.routing_key == "#"
        assert queue.arguments["x-queue-type"] == "quorum"
        assert queue.arguments["x-dead-letter-exchange"] == "flux.retry"
        assert retry_queue.arguments["x-message-ttl"] >= 1000
        assert retry_queue.arguments["x-dead-letter-exchange"] == ""
        assert retry_queue.arguments["x-dead-letter-routing-key"] == queue_name


def test_management_queue_status_reports_missing_required_event_queues(monkeypatch):
    class FakeResponse:
        def __enter__(self):
            return self

        def __exit__(self, *_exc_info):
            return False

        def read(self):
            return json.dumps(
                [
                    {"name": "flux.commands.corpus", "messages_ready": 0, "messages_unacknowledged": 0, "messages": 0, "consumers": 1},
                ]
            ).encode("utf-8")

    monkeypatch.setattr(messaging, "urlopen", lambda *_args, **_kwargs: FakeResponse())

    status = messaging.management_queue_status()

    assert status["available"] is True
    assert "flux.events.audit" in status["topology"]["missing_required_queues"]
    assert "flux.events.dashboard" in status["topology"]["missing_event_subscribers"]


def test_event_subscriber_records_journal_before_ack():
    events: list[tuple[str, dict]] = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return True

        def record_event_journal(self, **kwargs):
            events.append(("journal", kwargs))
            return {"message_id": kwargs["message_id"], "subscriber_name": kwargs["subscriber_name"]}

        def complete_message_inbox(self, **kwargs):
            events.append(("complete", kwargs))

    message = messaging.build_message(
        message_type="corpus.job.completed",
        routing_key="corpus.job.completed",
        job_id="job-1",
        payload={"job_id": "job-1", "status": "completed"},
        correlation_id="corr-1",
        causation_id="cmd-1",
    )
    subscriber = event_subscriber.EventSubscriber(subscriber_name="audit", database_module=FakeDatabase())

    result = subscriber.handle(message)

    assert result["status"] == "handled"
    assert [event[0] for event in events] == ["begin", "journal", "complete"]
    assert events[1][1]["exchange"] == "flux.events"
    assert events[1][1]["routing_key"] == "corpus.job.completed"
    assert events[1][1]["correlation_id"] == "corr-1"
    assert events[2][1]["status"] == "handled"


def test_event_subscriber_suppresses_duplicate_events():
    events: list[tuple[str, dict]] = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return False

        def record_event_journal(self, **kwargs):  # pragma: no cover - should not run
            events.append(("journal", kwargs))

        def complete_message_inbox(self, **kwargs):  # pragma: no cover - should not run
            events.append(("complete", kwargs))

    message = messaging.build_message(
        message_type="corpus.job.completed",
        routing_key="corpus.job.completed",
        payload={"job_id": "job-1"},
    )
    subscriber = event_subscriber.EventSubscriber(subscriber_name="audit", database_module=FakeDatabase())

    result = subscriber.handle(message)

    assert result == {"status": "duplicate", "message_id": message.message_id, "acked": True}
    assert [event[0] for event in events] == ["begin"]


def test_callback_policy_allows_loopback_private_and_allowlisted_hosts():
    policy = callbacks.CallbackPolicy(allowlist=("https://hooks.example.local/flux",))

    assert callbacks.validate_callback_url("http://127.0.0.1:8765/hook", policy).allowed
    assert callbacks.validate_callback_url("http://192.168.1.20/hook", policy).allowed
    assert callbacks.validate_callback_url("https://hooks.example.local/flux/job", policy).allowed


def test_callback_policy_blocks_public_hosts_without_allowlist():
    decision = callbacks.validate_callback_url("https://example.com/hook", callbacks.CallbackPolicy())

    assert not decision.allowed
    assert "not loopback, private-network, or allowlisted" in decision.reason


def test_callback_signature_is_stable_and_carries_idempotency_key():
    body = b'{"message_id":"msg-1"}'

    headers = callbacks.sign_callback(
        body=body,
        secret="local-secret",
        message_id="msg-1",
        timestamp="2026-01-01T00:00:00Z",
    )

    assert headers["Idempotency-Key"] == "msg-1"
    assert headers["X-Flux-KB-Signature"].startswith("v1=")
    assert headers == callbacks.sign_callback(
        body=body,
        secret="local-secret",
        message_id="msg-1",
        timestamp="2026-01-01T00:00:00Z",
    )


def test_outbox_relay_marks_published_only_after_publisher_accepts(monkeypatch):
    events: list[tuple[str, str]] = []

    rows = [
        {
            "id": "outbox-1",
            "exchange": "flux.commands",
            "routing_key": "corpus.process",
            "message_type": "flux.corpus.process",
            "payload": {"message_id": "msg-1", "routing_key": "corpus.process", "payload": {"job_id": "job-1"}},
            "headers": {"h": "v"},
        }
    ]

    class FakeDatabase:
        def claim_pending_outbox_messages(self, **_kwargs):
            return list(rows)

        def mark_outbox_message_published(self, *, outbox_id, broker_message_id, **_kwargs):
            events.append(("published", f"{outbox_id}:{broker_message_id}"))

        def mark_outbox_message_failed(self, *, outbox_id, error, **_kwargs):
            events.append(("failed", f"{outbox_id}:{error}"))

    class FakePublisher:
        async def publish(self, *, exchange, routing_key, message, headers=None):
            assert exchange == "flux.commands"
            assert routing_key == "corpus.process"
            assert headers == {"h": "v"}
            return {"message_id": message["message_id"]}

    relay = outbox_relay.OutboxRelay(database_module=FakeDatabase(), publisher=FakePublisher(), worker_id="relay-test")

    result = asyncio.run(relay.run_once(limit=10))

    assert result == {"claimed": 1, "published": 1, "failed": 0}
    assert events == [("published", "outbox-1:msg-1")]


def test_outbox_relay_records_publish_failure_without_marking_published():
    events: list[tuple[str, str]] = []

    class FakeDatabase:
        def claim_pending_outbox_messages(self, **_kwargs):
            return [
                {
                    "id": "outbox-1",
                    "exchange": "flux.commands",
                    "routing_key": "corpus.process",
                    "message_type": "flux.corpus.process",
                    "payload": {"message_id": "msg-1", "routing_key": "corpus.process", "payload": {}},
                    "headers": {},
                }
            ]

        def mark_outbox_message_published(self, **kwargs):
            events.append(("published", kwargs["outbox_id"]))

        def mark_outbox_message_failed(self, *, outbox_id, error, **_kwargs):
            events.append(("failed", f"{outbox_id}:{error}"))

    class FailingPublisher:
        async def publish(self, **_kwargs):
            raise RuntimeError("broker down")

    relay = outbox_relay.OutboxRelay(database_module=FakeDatabase(), publisher=FailingPublisher(), worker_id="relay-test")

    result = asyncio.run(relay.run_once(limit=10))

    assert result == {"claimed": 1, "published": 0, "failed": 1}
    assert events == [("failed", "outbox-1:broker down")]


def test_callback_dispatcher_delivers_signed_callback():
    events: list[tuple[str, dict]] = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return True

        def complete_message_inbox(self, **kwargs):
            events.append(("inbox", kwargs))

        def claim_callback_delivery(self, **kwargs):
            events.append(("claim", kwargs))
            return {
                "id": "delivery-1",
                "message_id": "callback-msg-1",
                "callback_url": "http://127.0.0.1:8765/hook",
                "attempts": 1,
                "idempotency_key": "callback-msg-1",
                "payload": {"job_id": "job-1", "status": "completed"},
            }

        def complete_callback_delivery(self, **kwargs):
            events.append(("callback", kwargs))

    class FakeSettings:
        def resolve(self, key):
            values = {
                "callbacks.allowlist": [],
                "callbacks.signing_secret": "local-secret",
                "callbacks.timeout_seconds": 5,
            }
            return SimpleNamespace(raw_value=values[key])

    def fake_post(url, *, body, headers, timeout_seconds):
        assert url == "http://127.0.0.1:8765/hook"
        assert json.loads(body.decode("utf-8")) == {"job_id": "job-1", "status": "completed"}
        assert headers["Idempotency-Key"] == "callback-msg-1"
        assert headers["X-Flux-KB-Signature"].startswith("v1=")
        assert timeout_seconds == 5
        return {"status_code": 204}

    message = messaging.build_message(
        message_type="flux.callback.dispatch",
        routing_key=messaging.CALLBACK_DISPATCH_ROUTING_KEY,
        payload={"callback_delivery_id": "delivery-1"},
    )
    dispatcher = callback_dispatcher.CallbackDispatcher(
        database_module=FakeDatabase(),
        http_post=fake_post,
        settings_service=FakeSettings(),
    )

    result = dispatcher.handle(message)

    assert result["result"]["status"] == "delivered"
    assert events[-2] == ("callback", {"delivery_id": "delivery-1", "status": "delivered", "status_code": 204})
    assert events[-1][0] == "inbox"
    assert events[-1][1]["status"] == "handled"


def test_callback_dispatcher_retries_transient_http_failure():
    events: list[tuple[str, dict]] = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            return True

        def complete_message_inbox(self, **kwargs):
            events.append(("inbox", kwargs))

        def claim_callback_delivery(self, **_kwargs):
            return {
                "id": "delivery-1",
                "message_id": "callback-msg-1",
                "callback_url": "http://127.0.0.1:8765/hook",
                "attempts": 1,
                "idempotency_key": "callback-msg-1",
                "payload": {"job_id": "job-1"},
            }

        def complete_callback_delivery(self, **kwargs):
            events.append(("callback", kwargs))

    class FakeSettings:
        def resolve(self, key):
            values = {
                "callbacks.allowlist": [],
                "callbacks.signing_secret": "local-secret",
                "callbacks.timeout_seconds": 5,
            }
            return SimpleNamespace(raw_value=values[key])

    dispatcher = callback_dispatcher.CallbackDispatcher(
        database_module=FakeDatabase(),
        http_post=lambda *_args, **_kwargs: {"status_code": 503},
        settings_service=FakeSettings(),
    )
    message = messaging.build_message(
        message_type="flux.callback.dispatch",
        routing_key=messaging.CALLBACK_DISPATCH_ROUTING_KEY,
        payload={"callback_delivery_id": "delivery-1"},
    )

    with pytest.raises(messaging.RetryableMessageError):
        dispatcher.handle(message)

    assert events[0] == (
        "callback",
        {"delivery_id": "delivery-1", "status": "retrying", "status_code": 503, "error": "HTTP 503"},
    )
    assert events[1][0] == "inbox"
    assert events[1][1]["status"] == "failed"


def test_event_scheduler_enqueues_due_state_as_outbox_commands(monkeypatch):
    events: list[tuple[str, dict]] = []

    class FakeDatabase:
        def recover_interrupted_imap_sync_runs(self, **kwargs):
            events.append(("recover_imap", kwargs))
            return {"status": "ok"}

        def enqueue_due_imap_sync_commands(self, **kwargs):
            events.append(("imap", kwargs))
            return {"queued": 1}

        def enqueue_due_outlook_sync_commands(self, **kwargs):
            events.append(("outlook", kwargs))
            return {"queued": 1}

        def enqueue_message_outbox(self, **kwargs):
            events.append(("outbox", kwargs))
            return {"message_id": kwargs["message_id"], "status": "pending"}

        def record_runtime_component_heartbeat(self, **kwargs):
            events.append(("heartbeat", kwargs))

    monkeypatch.setattr(
        event_scheduler,
        "_operator_automation_policy_from_settings",
        lambda: {"enabled": True, "mode": "guarded", "interval_seconds": 1800, "max_actions_per_run": 7},
    )

    scheduler = event_scheduler.EventScheduler(database_module=FakeDatabase(), scheduler_id="scheduler-test")
    result = scheduler.run_once(limit=3, force_due=True)

    assert result["imap"]["queued"] == 1
    assert result["outlook"]["queued"] == 1
    automation = next(payload for name, payload in events if name == "outbox")
    assert automation["exchange"] == "flux.commands"
    assert automation["routing_key"] == "operator.automation.run"
    assert automation["payload"] == {"trigger": "scheduler", "mode": "guarded", "limit": 7, "requested_by": "scheduler-test"}
