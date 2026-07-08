from __future__ import annotations

import pytest


fastapi_testclient = pytest.importorskip("fastapi.testclient")


def _snapshot_payload() -> dict[str, object]:
    return {
        "generated_at": "2026-07-08T10:00:00+00:00",
        "health": {"database": {"ok": True}, "jobs": {"pending": 0}},
        "crawl": {"roots": [], "status": {}, "watchers": []},
        "jobs": {"jobs": [{"id": "job-1", "status": "running"}], "count": 1, "limit": 1, "offset": 0, "has_next": False},
        "retrieval": {"sources": 0},
        "modelActivity": {"events": [{"id": "event-1", "status": "running"}], "active_count": 1},
        "mail": {"profiles": []},
        "outlook": {"profiles": [], "pending_requests": []},
        "settings": [],
    }


def test_dashboard_snapshot_route_returns_full_dashboard_state(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    calls: list[dict[str, object]] = []

    def fake_snapshot(**kwargs):
        calls.append(kwargs)
        return _snapshot_payload()

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr("flux_llm_kb.rest_api.collect_dashboard_snapshot", fake_snapshot)
    client = fastapi_testclient.TestClient(create_app())

    response = client.get(
        "/api/dashboard/snapshot",
        params={
            "jobs_limit": 1,
            "jobs_offset": 0,
            "jobs_status": "running",
            "model_window_minutes": 9999,
            "model_limit": 9999,
        },
    )

    assert response.status_code == 200
    payload = response.json()
    assert list(payload.keys()) == [
        "generated_at",
        "health",
        "crawl",
        "jobs",
        "retrieval",
        "modelActivity",
        "mail",
        "outlook",
        "settings",
    ]
    assert payload["jobs"]["jobs"][0]["id"] == "job-1"
    assert payload["modelActivity"]["events"][0]["id"] == "event-1"
    assert calls == [
        {
            "jobs": {
                "limit": 1,
                "offset": 0,
                "status": ["running"],
                "root_name": None,
                "job_type": None,
                "job_source": None,
                "updated_from": None,
                "updated_to": None,
                "sort_by": "updated",
                "sort_dir": "desc",
            },
            "model_activity": {
                "window_minutes": 360,
                "limit": 200,
                "offset": 0,
                "include_control_plane": False,
            },
        }
    ]


def test_dashboard_stream_subscribe_receives_connected_snapshot_and_sections(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr("flux_llm_kb.rest_api.collect_dashboard_snapshot", lambda **_kwargs: _snapshot_payload())
    monkeypatch.setattr("flux_llm_kb.dashboard_realtime.stream_broker_status", lambda: {"status": "ok", "rabbitmq": True})
    client = fastapi_testclient.TestClient(create_app())

    with client.websocket_connect("/api/dashboard/stream") as websocket:
        connected = websocket.receive_json()
        assert connected["type"] == "dashboard.connected"
        assert connected["stream"]["status"] == "ok"

        websocket.send_json(
            {
                "type": "dashboard.subscribe",
                "sections": ["jobs", "modelActivity"],
                "activeTab": "state",
                "jobs": {"limit": 1, "offset": 0, "status": ["running"]},
                "modelActivity": {"windowMinutes": 60, "limit": 25, "offset": 0},
            }
        )

        snapshot = websocket.receive_json()
        assert snapshot["type"] == "dashboard.snapshot"
        assert snapshot["payload"]["jobs"]["jobs"][0]["id"] == "job-1"

        section_messages = [websocket.receive_json(), websocket.receive_json()]
        assert {message["section"] for message in section_messages} == {"jobs", "modelActivity"}
        assert all(message["type"] == "dashboard.section" for message in section_messages)
        assert all("sequence" in message for message in section_messages)


def test_dashboard_stream_reports_degraded_broker_without_polling_fallback(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr("flux_llm_kb.rest_api.collect_dashboard_snapshot", lambda **_kwargs: _snapshot_payload())
    monkeypatch.setattr(
        "flux_llm_kb.dashboard_realtime.stream_broker_status",
        lambda: {"status": "degraded", "rabbitmq": False, "reason": "rabbitmq unavailable"},
    )
    client = fastapi_testclient.TestClient(create_app())

    with client.websocket_connect("/api/dashboard/stream") as websocket:
        connected = websocket.receive_json()
        assert connected["type"] == "dashboard.connected"
        assert connected["stream"] == {"status": "degraded", "rabbitmq": False, "reason": "rabbitmq unavailable"}

        websocket.send_json({"type": "dashboard.subscribe", "sections": ["health"]})
        assert websocket.receive_json()["type"] == "dashboard.snapshot"


def test_dashboard_broker_event_mapping_and_event_payload_are_sanitized():
    from flux_llm_kb import dashboard_realtime, messaging

    job_message = messaging.FluxMessage(
        message_type="corpus.job.completed",
        routing_key="corpus.job.completed",
        payload={"job_id": "job-1", "status": "completed", "details": {"path": "ignored"}},
    )
    assert dashboard_realtime.dashboard_sections_for_event(job_message) == (
        ("crawl", "corpus.job.completed"),
        ("jobs", "corpus.job.completed"),
        ("health", "corpus.job.completed"),
    )

    model_message = messaging.FluxMessage(
        message_type="dashboard.section.changed",
        routing_key="dashboard.modelActivity.changed",
        payload={
            "section": "modelActivity",
            "reason": "model_activity.started",
            "event": {"service": "ollama", "status": "running", "details": "ignored"},
        },
    )
    assert dashboard_realtime.dashboard_sections_for_event(model_message) == (("modelActivity", "model_activity.started"),)

    event = dashboard_realtime.event_message(
        section="modelActivity",
        reason="model_activity.started",
        event={"service": "ollama", "status": "running", "raw": "ignored", "details": {"raw": "ignored"}},
    )
    assert event["type"] == "dashboard.event"
    assert event["message"] == "ollama: running"
    assert event["payload"] == {"service": "ollama", "status": "running"}


def test_dashboard_job_mutation_emits_sanitized_dashboard_event(monkeypatch):
    from flux_llm_kb.rest_api import create_app

    emitted: list[dict[str, object]] = []

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: object())
    monkeypatch.setattr(
        "flux_llm_kb.rest_api.database.cancel_corpus_job",
        lambda **_kwargs: {"job_id": "job-1", "status": "cancelled_operator", "cancelled": True},
    )
    monkeypatch.setattr("flux_llm_kb.dashboard_realtime.emit_dashboard_change", lambda **kwargs: emitted.append(kwargs))
    client = fastapi_testclient.TestClient(create_app())

    response = client.post("/api/dashboard/jobs/job-1/cancel")

    assert response.status_code == 200
    assert emitted == [
        {
            "section": "jobs",
            "reason": "job.cancelled",
            "event": {"job_id": "job-1", "status": "cancelled_operator", "cancelled": True},
        }
    ]
