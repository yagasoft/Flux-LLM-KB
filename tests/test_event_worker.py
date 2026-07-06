from __future__ import annotations

import pytest

from flux_llm_kb import database, event_worker, messaging
from flux_llm_kb.service import KnowledgeService
from flux_llm_kb.worker import JobProcessResult


def test_event_worker_run_loop_defers_initial_broker_connection_to_consume(monkeypatch):
    events = []

    class FakeConsumer:
        async def __aenter__(self):  # pragma: no cover - should not be used
            raise AssertionError("initial broker connection should be handled by consume retry loop")

        async def __aexit__(self, *_exc_info):  # pragma: no cover - should not be used
            return False

        async def consume(self, *, queue_name, handler):
            events.append(("consume", queue_name, callable(handler)))

    monkeypatch.setattr(event_worker.messaging, "RabbitMqConsumer", FakeConsumer)

    payload = event_worker.run_worker(queue_name=messaging.COMMAND_OUTLOOK_QUEUE, worker_id="host-1")

    assert payload == {"status": "stopped", "queue": messaging.COMMAND_OUTLOOK_QUEUE}
    assert events == [("consume", messaging.COMMAND_OUTLOOK_QUEUE, True)]


def test_event_worker_marks_message_handled_after_success():
    events = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return True

        def complete_message_inbox(self, **kwargs):
            events.append(("complete", kwargs))

    class FakeService:
        def process_corpus_job_by_id(self, **kwargs):
            events.append(("process", kwargs))
            return {"job_id": kwargs["job_id"], "status": "completed", "retryable": False}

    message = messaging.build_message(
        message_type="flux.corpus.process",
        routing_key=messaging.CORPUS_PROCESS_ROUTING_KEY,
        job_id="job-1",
        payload={"job_id": "job-1"},
    )
    worker = event_worker.EventWorker(service=FakeService(), database_module=FakeDatabase(), worker_id="worker-1")

    result = worker.handle(message)

    assert result["status"] == "handled"
    assert events[1] == (
        "process",
        {
            "job_id": "job-1",
            "worker_id": "worker-1",
            "broker_message_id": message.message_id,
            "correlation_id": message.correlation_id,
            "causation_id": message.causation_id,
        },
    )
    assert events[2][0] == "complete"
    assert events[2][1]["status"] == "handled"


def test_event_worker_marks_retryable_result_failed_and_rejects_for_broker_retry():
    events = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return True

        def complete_message_inbox(self, **kwargs):
            events.append(("complete", kwargs))

    class FakeService:
        def process_corpus_job_by_id(self, **_kwargs):
            return {"job_id": "job-1", "status": "retrying_gpu_busy", "retryable": True}

    message = messaging.build_message(
        message_type="flux.corpus.process",
        routing_key=messaging.CORPUS_PROCESS_ROUTING_KEY,
        job_id="job-1",
        payload={"job_id": "job-1"},
    )
    worker = event_worker.EventWorker(service=FakeService(), database_module=FakeDatabase())

    with pytest.raises(messaging.RetryableMessageError):
        worker.handle(message)

    assert events[-1][0] == "complete"
    assert events[-1][1]["status"] == "failed"
    assert events[-1][1]["error"] == "retrying_gpu_busy"


def test_event_worker_dispatches_gpu_eviction_request(monkeypatch):
    events = []

    class FakeDatabase:
        def begin_message_inbox(self, **kwargs):
            events.append(("begin", kwargs))
            return True

        def complete_message_inbox(self, **kwargs):
            events.append(("complete", kwargs))

    def fake_process_gpu_eviction_request(**kwargs):
        events.append(("evict", kwargs))
        return {"eviction_id": kwargs["eviction_id"], "status": "succeeded", "retryable": False}

    monkeypatch.setattr(event_worker, "process_gpu_eviction_request", fake_process_gpu_eviction_request, raising=False)
    message = messaging.build_message(
        message_type="flux.gpu.eviction.request",
        routing_key=messaging.GPU_EVICTION_ROUTING_KEY,
        payload={"eviction_id": "eviction-1"},
        correlation_id="corr-1",
        causation_id="cause-1",
    )
    worker = event_worker.EventWorker(database_module=FakeDatabase(), worker_id="worker-1")

    result = worker.handle(message)

    assert result["status"] == "handled"
    assert events[1] == (
        "evict",
        {
            "eviction_id": "eviction-1",
            "worker_id": "worker-1",
            "broker_message_id": message.message_id,
            "correlation_id": "corr-1",
            "causation_id": "cause-1",
        },
    )
    assert events[-1][0] == "complete"
    assert events[-1][1]["status"] == "handled"


def test_process_corpus_job_by_id_writes_terminal_state_before_event(monkeypatch):
    events = []
    job = {
        "id": "job-1",
        "job_type": "corpus_extract_text",
        "job_family": "text",
        "resource_class": "cpu",
        "payload": {"root_name": "docs", "path": "safe.md"},
        "attempts": 1,
    }

    monkeypatch.setattr(
        database,
        "claim_corpus_job_by_id",
        lambda **kwargs: events.append(("claim", kwargs)) or job,
    )
    monkeypatch.setattr(database, "complete_corpus_job", lambda **kwargs: events.append(("complete", kwargs)))
    monkeypatch.setattr(database, "enqueue_capture_job_event", lambda **kwargs: events.append(("event", kwargs)) or {"id": "outbox-1"})

    service = KnowledgeService()
    monkeypatch.setattr(
        service,
        "_process_claimed_corpus_job",
        lambda claimed: (claimed, 12, JobProcessResult(status="indexed", telemetry={"stage": "done"})),
    )

    result = service.process_corpus_job_by_id(
        job_id="job-1",
        worker_id="worker-1",
        broker_message_id="message-1",
        correlation_id="corr-1",
    )

    assert result["status"] == "completed"
    assert [event[0] for event in events] == ["claim", "complete", "event"]
    assert events[0][1]["broker_message_id"] == "message-1"
    assert events[2][1]["event_type"] == "corpus.job.completed"
    assert events[2][1]["correlation_id"] == "corr-1"
    assert events[2][1]["causation_id"] == "message-1"
