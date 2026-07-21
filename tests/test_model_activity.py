from __future__ import annotations

from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

import pytest

from flux_llm_kb import database, model_activity
from flux_llm_kb.gpu_scheduler import GpuLeaseRejected, GpuModelResidency, GpuSchedulerConfig, InProcessGpuScheduler


@pytest.mark.parametrize(
    ("surface", "expected_class"),
    [
        ("mcp", "interactive"),
        ("api", "interactive"),
        ("cli", "interactive"),
        ("worker", "background"),
        ("gpu_scheduler", "background"),
        ("unknown", "background"),
        ("", "background"),
    ],
)
def test_caller_surface_exposes_an_opaque_request_context(surface, expected_class):
    with model_activity.caller_surface(surface, request_id="request-opaque"):
        context = model_activity.current_model_request_context()

    assert context == {"request_class": expected_class, "request_id": "request-opaque"}


def test_record_model_activity_sanitizes_metadata_and_completes(monkeypatch):
    started: list[dict[str, object]] = []
    finished: list[dict[str, object]] = []

    def fake_start(**kwargs):
        started.append(kwargs)
        return "event-1"

    def fake_finish(**kwargs):
        finished.append(kwargs)

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", fake_start, raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", fake_finish, raising=False)
    monkeypatch.setattr(model_activity, "_utc_now", lambda: datetime(2026, 7, 3, 1, 25, 58, tzinfo=UTC))
    monkeypatch.setattr(model_activity.time, "monotonic", lambda: 100.0)

    with model_activity.caller_surface("mcp"):
        with model_activity.record_model_activity(
            service="model-runner",
            endpoint="/v1/rerank",
            action="rerank",
            activity_class="retrieval",
            model="Qwen/Qwen3-Reranker-4B",
            metadata={
                "batch_size": 2,
                "query": "private question",
                "path": "E:/Private/report.pdf",
                "content_b64": "private bytes",
                "duration_hint_ms": 12,
            },
        ):
            pass

    assert started == [
        {
            "service": "model-runner",
            "endpoint": "/v1/rerank",
            "action": "rerank",
            "activity_class": "retrieval",
            "caller_surface": "mcp",
            "model": "Qwen/Qwen3-Reranker-4B",
            "metadata": {"batch_size": 2, "duration_hint_ms": 12},
        }
    ]
    assert finished == [
        {
            "event_id": "event-1",
            "status": "completed",
            "duration_ms": 0,
            "error_class": None,
            "error_message": None,
        }
    ]


def test_record_model_activity_emits_dashboard_state_events(monkeypatch):
    emitted: list[dict[str, object]] = []

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-1", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(model_activity.dashboard_realtime, "emit_dashboard_change", lambda **kwargs: emitted.append(kwargs), raising=False)
    monkeypatch.setattr(model_activity.time, "monotonic", lambda: 100.0)

    with model_activity.record_model_activity(
        service="model-runner",
        endpoint="/v1/rerank",
        action="rerank",
        activity_class="retrieval",
        caller_surface="mcp",
        model="Qwen/Qwen3-Reranker-4B",
        metadata={"batch_size": 2, "path": "E:/Private/report.pdf"},
    ):
        pass

    assert emitted == [
        {
            "section": "modelActivity",
            "reason": "model_activity.started",
            "event": {
                "id": "event-1",
                "service": "model-runner",
                "endpoint": "/v1/rerank",
                "action": "rerank",
                "activity_class": "retrieval",
                "caller_surface": "mcp",
                "model": "Qwen/Qwen3-Reranker-4B",
                "metadata": {"batch_size": 2},
                "status": "running",
            },
        },
        {
            "section": "modelActivity",
            "reason": "model_activity.completed",
            "event": {"id": "event-1", "status": "completed", "duration_ms": 0, "error_class": None},
        },
    ]


def test_resident_gpu_model_records_model_loading_activity(monkeypatch):
    started: list[dict[str, object]] = []
    finished: list[dict[str, object]] = []

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **kwargs: started.append(kwargs) or "resident-event", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)
    monkeypatch.setattr(model_activity.time, "monotonic", lambda: 100.0)

    scheduler = InProcessGpuScheduler(GpuSchedulerConfig(enabled=True, mode="in_process"))

    scheduler.record_model_residency(
        GpuModelResidency(
            model_id="PP-OCRv5",
            task_type="ocr_image",
            estimated_vram_mb=1200,
            resident=True,
            metadata={"component": "worker"},
        )
    )

    assert started == [
        {
            "service": "worker",
            "endpoint": "/gpu/residency",
            "action": "model_loading",
            "activity_class": "model_loading",
            "caller_surface": "gpu_scheduler",
            "model": "PP-OCRv5",
            "metadata": {"component": "worker", "resident": True, "task_type": "ocr_image"},
        }
    ]
    assert finished == [
        {
            "event_id": "resident-event",
            "status": "completed",
            "duration_ms": 0,
            "error_class": None,
            "error_message": None,
        }
    ]


def test_non_resident_gpu_model_does_not_record_model_loading_activity(monkeypatch):
    started: list[dict[str, object]] = []

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **kwargs: started.append(kwargs) or "resident-event", raising=False)

    scheduler = InProcessGpuScheduler(GpuSchedulerConfig(enabled=True, mode="in_process"))

    scheduler.record_model_residency(
        GpuModelResidency(
            model_id="PP-OCRv5",
            task_type="ocr_image",
            estimated_vram_mb=1200,
            resident=False,
            metadata={"component": "worker"},
        )
    )

    assert started == []


def test_record_model_activity_marks_busy_and_keeps_errors_when_redactions_disabled(monkeypatch):
    finished: list[dict[str, object]] = []
    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)
    busy_error = type("ModelRunnerBusy", (RuntimeError,), {})(
        "scheduler busy for E:/Docs/report.pdf " + "password" + "=sample"
    )

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-busy", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)

    try:
        with model_activity.record_model_activity(service="model-runner", endpoint="/v1/embeddings", action="embedding", activity_class="retrieval"):
            raise busy_error
    except RuntimeError:
        pass

    assert finished[0]["status"] == "busy"
    assert finished[0]["error_class"] == "ModelRunnerBusy"
    message = str(finished[0]["error_message"])
    assert ("password" + "=sample") in message
    assert "E:/Docs/report.pdf" in message


def test_record_model_activity_redacts_errors_when_enabled(monkeypatch):
    finished: list[dict[str, object]] = []
    monkeypatch.setenv("FLUX_KB_REDACTIONS_ENABLED", "true")
    busy_error = type("ModelRunnerBusy", (RuntimeError,), {})(
        "scheduler busy for E:/Docs/report.pdf " + "password" + "=sample"
    )

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-busy", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)

    try:
        with model_activity.record_model_activity(service="model-runner", endpoint="/v1/embeddings", action="embedding", activity_class="retrieval"):
            raise busy_error
    except RuntimeError:
        pass

    assert finished[0]["status"] == "busy"
    assert finished[0]["error_class"] == "ModelRunnerBusy"
    message = str(finished[0]["error_message"])
    assert "password" not in message.lower()
    assert "E:/Docs" not in message


def test_record_model_activity_marks_scheduler_rejections_busy(monkeypatch):
    finished: list[dict[str, object]] = []

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-rejected", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)

    try:
        with model_activity.record_model_activity(service="ollama", endpoint="/api/generate", action="vision_generate", activity_class="vision_ocr"):
            raise GpuLeaseRejected("vram budget exceeded")
    except GpuLeaseRejected:
        pass

    assert finished[0]["status"] == "busy"
    assert finished[0]["error_class"] == "GpuLeaseRejected"


def test_record_model_activity_marks_paddle_dependency_errors_blocked(monkeypatch):
    finished: list[dict[str, object]] = []
    monkeypatch.delenv("FLUX_KB_REDACTIONS_ENABLED", raising=False)

    class DependencyError(RuntimeError):
        pass

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", lambda **_kwargs: "event-dependency", raising=False)
    monkeypatch.setattr(model_activity.database, "finish_model_activity_event", lambda **kwargs: finished.append(kwargs), raising=False)

    try:
        with model_activity.record_model_activity(service="worker", endpoint="/v1/ocr/document", action="ocr_document", activity_class="vision_ocr"):
            raise DependencyError("PaddleOCR-VL dependency missing for E:/Docs/report.pdf " + "token" + "=sample")
    except DependencyError:
        pass

    assert finished[0]["status"] == "blocked_missing_dependency"
    assert finished[0]["error_class"] == "DependencyError"
    message = str(finished[0]["error_message"])
    assert "PaddleOCR-VL dependency missing" in message
    assert "E:/Docs/report.pdf" in message
    assert ("token" + "=sample") in message


def test_record_model_activity_is_best_effort_when_database_fails(monkeypatch):
    def broken_start(**_kwargs):
        raise RuntimeError("database unavailable")

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", broken_start, raising=False)

    with model_activity.record_model_activity(service="model-runner", endpoint="/v1/rerank", action="rerank", activity_class="retrieval"):
        pass


@pytest.mark.parametrize("model", ["llava:latest", "qwen2.5vl:7b", "qwen3-vl:8b"])
def test_model_activity_recorder_does_not_write_live_database_under_pytest(monkeypatch, model):
    calls = {"load_psycopg": 0}

    def fail_load_psycopg():
        calls["load_psycopg"] += 1
        raise AssertionError("live database write attempted")

    monkeypatch.delenv("FLUX_KB_TEST_DATABASE_URL", raising=False)
    monkeypatch.delenv("FLUX_KB_ALLOW_MODEL_ACTIVITY_TEST_WRITES", raising=False)
    monkeypatch.setattr(database, "_load_psycopg", fail_load_psycopg)

    with model_activity.record_model_activity(
        service="ollama",
        endpoint="/api/generate",
        action="vision_generate",
        activity_class="vision_ocr",
        model=model,
    ):
        pass

    assert calls["load_psycopg"] == 0


def test_model_activity_database_writes_are_blocked_under_pytest_without_disposable_db(monkeypatch):
    def fail_load_psycopg():
        raise AssertionError("live database write attempted")

    monkeypatch.delenv("FLUX_KB_TEST_DATABASE_URL", raising=False)
    monkeypatch.delenv("FLUX_KB_ALLOW_MODEL_ACTIVITY_TEST_WRITES", raising=False)
    monkeypatch.setattr(database, "_load_psycopg", fail_load_psycopg)

    with pytest.raises(RuntimeError, match="model activity writes are disabled under pytest"):
        database.start_model_activity_event(
            service="ollama",
            endpoint="/api/generate",
            action="vision_generate",
            activity_class="vision_ocr",
            model="llava:latest",
        )


def test_model_activity_database_write_guard_requires_opt_in_test_database(monkeypatch):
    live_url = "postgresql://flux:flux@127.0.0.1:5432/flux_llm_kb"
    test_url = "postgresql://flux:flux@127.0.0.1:55432/flux_llm_kb_test"

    monkeypatch.setenv("FLUX_KB_TEST_DATABASE_URL", test_url)
    monkeypatch.delenv("FLUX_KB_ALLOW_MODEL_ACTIVITY_TEST_WRITES", raising=False)

    with pytest.raises(RuntimeError, match="model activity writes are disabled under pytest"):
        database._guard_model_activity_test_write(test_url)

    monkeypatch.setenv("FLUX_KB_ALLOW_MODEL_ACTIVITY_TEST_WRITES", "1")

    with pytest.raises(RuntimeError, match="FLUX_KB_TEST_DATABASE_URL"):
        database._guard_model_activity_test_write(live_url)

    assert database._model_activity_write_url(None) == test_url
    database._guard_model_activity_test_write(test_url)


def test_collect_model_activity_payload_summarizes_events_and_scheduler(monkeypatch):
    now = datetime(2026, 7, 3, 1, 30, tzinfo=UTC)
    events = [
        {
            "id": "event-running",
            "service": "model-runner",
            "endpoint": "/v1/rerank",
            "action": "rerank",
            "activity_class": "retrieval",
            "caller_surface": "mcp",
            "model": "Qwen/Qwen3-Reranker-4B",
            "status": "running",
            "started_at": now - timedelta(minutes=1),
            "completed_at": None,
            "duration_ms": None,
            "error_class": None,
            "error_message": None,
            "metadata": {"query": "must not leak", "batch_size": 2},
        },
        {
            "id": "event-failed",
            "service": "ollama",
            "endpoint": "/api/generate",
            "action": "vision_generate",
            "activity_class": "vision_ocr",
            "caller_surface": "worker",
            "model": "qwen3-vl:8b",
            "status": "failed",
            "started_at": now - timedelta(minutes=2),
            "completed_at": now - timedelta(minutes=1),
            "duration_ms": 5000,
            "error_class": "RuntimeError",
            "error_message": "redacted failure",
            "metadata": {},
        },
    ]

    class FakeScheduler:
        def status(self):
            return {
                "enabled": True,
                "mode": "postgres",
                "running": [{"created_at": (now - timedelta(seconds=30)).timestamp()}],
                "waiting": [{"created_at": (now - timedelta(seconds=20)).timestamp()}],
                "recent": [{"released_at": (now - timedelta(seconds=10)).timestamp()}],
                "timeouts": 1,
                "rejections": 2,
                "model_residency": [
                    {
                        "component": "model-runner",
                        "model_id": "Snowflake/snowflake-arctic-embed-l-v2.0",
                        "task_type": "embedding",
                        "resident": True,
                        "last_used_at": (now - timedelta(seconds=5)).timestamp(),
                    }
                ],
                "live_gpu_memory": {"ok": True, "gpus": [{"memory_used_mb": 8120, "memory_total_mb": 16380}]},
                "evictions": {
                    "recent": [{"created_at": (now - timedelta(minutes=3)).timestamp()}],
                    "attempts": 1,
                    "successes": 1,
                    "failures": 0,
                },
            }

    monkeypatch.setattr(model_activity, "_utc_now", lambda: now)
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", lambda **_kwargs: events, raising=False)
    monkeypatch.setattr(model_activity.database, "count_model_activity_events", lambda **_kwargs: 2, raising=False)
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: FakeScheduler())

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=50)

    assert payload["active_count"] == 1
    assert payload["recent_count"] == 2
    assert payload["total_count"] == 2
    assert payload["page_count"] == 1
    assert payload["has_next"] is False
    assert payload["service_breakdown"] == [
        {"service": "model-runner", "count": 1, "active": 1, "failures": 0},
        {"service": "ollama", "count": 1, "active": 0, "failures": 1},
    ]
    assert payload["class_breakdown"] == [
        {"activity_class": "retrieval", "count": 1},
        {"activity_class": "vision_ocr", "count": 1},
    ]
    event_metadata = {event["id"]: event["metadata"] for event in payload["events"]}
    assert event_metadata["event-running"] == {"batch_size": 2}
    assert payload["scheduler"]["mode"] == "postgres"
    assert payload["scheduler"]["running_count"] == 1
    assert payload["scheduler"]["waiting_count"] == 1
    assert payload["scheduler"]["evictions_recent_count"] == 1
    assert payload["scheduler"]["oldest_wait_age_ms"] == 20000
    assert payload["scheduler"]["resident_models"] == [
        {
            "service": "model-runner",
            "model": "Snowflake/snowflake-arctic-embed-l-v2.0",
            "task_type": "embedding",
            "last_used_at": "2026-07-03T01:29:55+00:00",
        }
    ]
    assert payload["scheduler"]["live_gpu_memory"] == {"available": True, "used_mb": 8120, "total_mb": 16380}


def test_model_activity_scheduler_summary_passes_through_reconciliation_evidence(monkeypatch):
    evidence = {"state": "healthy", "observation_id": "obs-1", "counters": {"cas_rejections": 2}}
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: SimpleNamespace(status=lambda: {"mode": "postgres", "runtime_reconciliation": evidence}))

    summary = model_activity._scheduler_summary(now=datetime(2026, 7, 3, tzinfo=UTC), event_last_at=None)

    assert summary["runtime_reconciliation"] == evidence


def test_collect_model_activity_payload_recovers_stale_running_before_listing(monkeypatch):
    now = datetime(2026, 7, 3, 1, 30, tzinfo=UTC)
    calls: list[tuple[str, object | None]] = []

    class FakeScheduler:
        config = SimpleNamespace(stale_after_seconds=180)

        def status(self):
            return {"enabled": True, "mode": "postgres"}

    def fake_recover(**kwargs):
        calls.append(("recover", kwargs["stale_after_seconds"]))
        return {"recovered": 1}

    def fake_list(**kwargs):
        calls.append(("list", kwargs))
        return []

    monkeypatch.setattr(model_activity, "_utc_now", lambda: now)
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: calls.append(("prune", None)), raising=False)
    monkeypatch.setattr(model_activity.database, "recover_stale_model_activity_events", fake_recover, raising=False)
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", fake_list, raising=False)
    monkeypatch.setattr(model_activity.database, "count_model_activity_events", lambda **_kwargs: 0, raising=False)

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=50)

    assert payload["active_count"] == 0
    assert calls[:3] == [
        ("prune", None),
        ("recover", 360),
        ("list", {"window_minutes": 60, "limit": 50, "offset": 0, "include_control_plane": False}),
    ]


def test_collect_model_activity_payload_stale_recovery_uses_five_minute_floor(monkeypatch):
    calls: list[int] = []

    class FakeScheduler:
        config = SimpleNamespace(stale_after_seconds=60)

        def status(self):
            return {"enabled": True, "mode": "in_process"}

    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: FakeScheduler())
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(
        model_activity.database,
        "recover_stale_model_activity_events",
        lambda **kwargs: calls.append(kwargs["stale_after_seconds"]) or {"recovered": 0},
        raising=False,
    )
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", lambda **_kwargs: [], raising=False)
    monkeypatch.setattr(model_activity.database, "count_model_activity_events", lambda **_kwargs: 0, raising=False)

    model_activity.collect_model_activity_payload()

    assert calls == [300]


def test_collect_model_activity_payload_hides_control_plane_by_default(monkeypatch):
    now = datetime(2026, 7, 3, 1, 30, tzinfo=UTC)
    events = [
        {
            "id": "event-health",
            "service": "model-runner",
            "endpoint": "/health",
            "action": "health",
            "activity_class": "control_plane",
            "caller_surface": "",
            "model": "",
            "status": "completed",
            "started_at": now - timedelta(seconds=30),
            "completed_at": now - timedelta(seconds=29),
            "duration_ms": 10,
            "error_class": None,
            "error_message": None,
            "metadata": {},
        },
        {
            "id": "event-rerank",
            "service": "model-runner",
            "endpoint": "/v1/rerank",
            "action": "rerank",
            "activity_class": "retrieval",
            "caller_surface": "mcp",
            "model": "Qwen/Qwen3-Reranker-4B",
            "status": "completed",
            "started_at": now - timedelta(seconds=20),
            "completed_at": now - timedelta(seconds=18),
            "duration_ms": 1842,
            "error_class": None,
            "error_message": None,
            "metadata": {"batch_size": 2},
        },
    ]

    calls: list[dict[str, object]] = []

    def fake_list_model_activity_events(**kwargs):
        calls.append(kwargs)
        return events

    monkeypatch.setattr(model_activity, "_utc_now", lambda: now)
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", fake_list_model_activity_events, raising=False)
    monkeypatch.setattr(model_activity.database, "count_model_activity_events", lambda **_kwargs: 1, raising=False)
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: SimpleNamespace(status=lambda: {"enabled": True, "mode": "postgres"}))

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=50)

    assert payload["recent_count"] == 1
    assert payload["service_breakdown"] == [{"service": "model-runner", "count": 1, "active": 0, "failures": 0}]
    assert payload["class_breakdown"] == [{"activity_class": "retrieval", "count": 1}]
    assert [event["id"] for event in payload["events"]] == ["event-rerank"]
    assert calls == [{"window_minutes": 60, "limit": 50, "offset": 0, "include_control_plane": False}]
    assert "/health" not in str(payload)


def test_collect_model_activity_payload_returns_pagination_metadata(monkeypatch):
    now = datetime(2026, 7, 3, 1, 30, tzinfo=UTC)
    events = [
        {
            "id": "event-newer",
            "service": "paddle-runner",
            "endpoint": "/v1/ocr/image",
            "action": "ocr_image",
            "activity_class": "vision_ocr",
            "caller_surface": "worker",
            "model": "PP-OCRv5",
            "status": "completed",
            "started_at": now - timedelta(minutes=2),
            "completed_at": now - timedelta(minutes=1),
            "duration_ms": 250,
            "error_class": None,
            "error_message": None,
            "metadata": {"document": False},
        }
    ]
    calls: list[dict[str, object]] = []

    def fake_list_model_activity_events(**kwargs):
        calls.append(kwargs)
        return events

    monkeypatch.setattr(model_activity, "_utc_now", lambda: now)
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", fake_list_model_activity_events, raising=False)
    monkeypatch.setattr(model_activity.database, "count_model_activity_events", lambda **kwargs: 101, raising=False)
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: SimpleNamespace(status=lambda: {"enabled": True, "mode": "postgres"}))

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=50, offset=50)

    assert payload["limit"] == 50
    assert payload["offset"] == 50
    assert payload["total_count"] == 101
    assert payload["page_count"] == 3
    assert payload["has_next"] is True
    assert [event["id"] for event in payload["events"]] == ["event-newer"]
    assert calls == [{"window_minutes": 60, "limit": 50, "offset": 50, "include_control_plane": False}]


def test_collect_model_activity_payload_tolerates_scheduler_failures(monkeypatch):
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(model_activity.database, "list_model_activity_events", lambda **_kwargs: [], raising=False)
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: SimpleNamespace(status=lambda: (_ for _ in ()).throw(RuntimeError("scheduler down"))))

    payload = model_activity.collect_model_activity_payload()

    assert payload["recent_count"] == 0
    assert payload["scheduler"]["mode"] == "unavailable"


def test_collect_model_activity_payload_classifies_stale_running_events(monkeypatch):
    now = datetime(2026, 7, 3, 1, 30, tzinfo=UTC)
    monkeypatch.setattr(model_activity, "_utc_now", lambda: now)
    monkeypatch.setattr(model_activity.database, "prune_model_activity_events", lambda **_kwargs: None, raising=False)
    monkeypatch.setattr(
        model_activity.database,
        "recover_stale_model_activity_events",
        lambda **_kwargs: (_ for _ in ()).throw(RuntimeError("recovery unavailable")),
        raising=False,
    )
    monkeypatch.setattr(
        model_activity.database,
        "list_model_activity_events",
        lambda **_kwargs: [
            {
                "id": "event-stale",
                "service": "model-runner",
                "endpoint": "/v1/embeddings",
                "action": "embedding",
                "activity_class": "retrieval",
                "caller_surface": "api",
                "model": "Snowflake/test",
                "status": "running",
                "started_at": now - timedelta(hours=2),
                "completed_at": None,
                "duration_ms": None,
                "error_class": None,
                "error_message": None,
                "metadata": {},
            }
        ],
        raising=False,
    )
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: SimpleNamespace(status=lambda: {"enabled": True, "mode": "in_process"}))

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=10)

    assert payload["active_count"] == 0
    assert payload["events"][0]["status"] == "stale_running"
