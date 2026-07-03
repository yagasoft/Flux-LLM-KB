from __future__ import annotations

from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

from flux_llm_kb import model_activity
from flux_llm_kb.gpu_scheduler import GpuLeaseRejected


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


def test_record_model_activity_marks_busy_and_redacts_errors(monkeypatch):
    finished: list[dict[str, object]] = []
    busy_error = type("ModelRunnerBusy", (RuntimeError,), {})("scheduler busy for E:/Private/report.pdf password=secret")

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
    assert "E:/Private" not in message


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


def test_record_model_activity_is_best_effort_when_database_fails(monkeypatch):
    def broken_start(**_kwargs):
        raise RuntimeError("database unavailable")

    monkeypatch.setattr(model_activity.database, "start_model_activity_event", broken_start, raising=False)

    with model_activity.record_model_activity(service="model-runner", endpoint="/v1/rerank", action="rerank", activity_class="retrieval"):
        pass


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
            "started_at": now - timedelta(minutes=10),
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
    monkeypatch.setattr(model_activity, "get_gpu_scheduler", lambda: FakeScheduler())

    payload = model_activity.collect_model_activity_payload(window_minutes=60, limit=50)

    assert payload["active_count"] == 1
    assert payload["recent_count"] == 2
    assert payload["service_breakdown"] == [
        {"service": "model-runner", "count": 1, "active": 1, "failures": 0},
        {"service": "ollama", "count": 1, "active": 0, "failures": 1},
    ]
    assert payload["class_breakdown"] == [
        {"activity_class": "retrieval", "count": 1},
        {"activity_class": "vision_ocr", "count": 1},
    ]
    assert payload["events"][0]["metadata"] == {"batch_size": 2}
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
