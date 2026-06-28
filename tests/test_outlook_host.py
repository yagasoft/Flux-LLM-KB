from __future__ import annotations

from datetime import datetime, timedelta, timezone
import sys
import threading
import types

from flux_llm_kb import database


def test_outlook_host_status_marks_missing_heartbeat_offline(monkeypatch):
    from flux_llm_kb import outlook_host

    monkeypatch.setattr(database, "get_outlook_host_state", lambda host_id="default": None)
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [])
    monkeypatch.setattr(database, "list_outlook_sync_requests", lambda limit=20: [])

    payload = outlook_host.status()

    assert payload["host"]["status"] == "host_offline"
    assert payload["host"]["command"] == "flux-kb outlook-host run"
    assert payload["pending_requests"] == []


def test_outlook_host_status_marks_stale_heartbeat_not_running(monkeypatch):
    from flux_llm_kb import outlook_host

    stale_heartbeat = datetime.now(timezone.utc) - timedelta(minutes=10)
    monkeypatch.setattr(
        database,
        "get_outlook_host_state",
        lambda host_id="default": {
            "host_id": host_id,
            "status": "running",
            "command": "flux-kb outlook-host run",
            "heartbeat_at": stale_heartbeat.isoformat(),
            "last_error": None,
            "metadata": {},
        },
    )
    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [])
    monkeypatch.setattr(database, "list_outlook_sync_requests", lambda limit=20: [])

    payload = outlook_host.status()

    assert payload["host"]["status"] == "host_stale"
    assert payload["host"]["reported_status"] == "running"
    assert "heartbeat" in payload["host"]["last_error"].lower()


def test_outlook_sync_request_is_created_for_profile(monkeypatch):
    from flux_llm_kb import outlook_host

    calls = []
    monkeypatch.setattr(
        database,
        "create_outlook_sync_request",
        lambda **kwargs: calls.append(kwargs) or {"id": "req-1", "status": "pending", "profile_name": kwargs["profile_name"]},
    )

    payload = outlook_host.request_sync("outlook-catchup", actor="dashboard")

    assert payload == {"id": "req-1", "status": "pending", "profile_name": "outlook-catchup"}
    assert calls[0]["profile_name"] == "outlook-catchup"
    assert calls[0]["actor"] == "dashboard"


def test_outlook_sync_request_cancel_delegates_to_database(monkeypatch):
    from flux_llm_kb import outlook_host

    calls = []
    monkeypatch.setattr(
        database,
        "cancel_outlook_sync_request",
        lambda **kwargs: calls.append(kwargs) or {"id": kwargs["request_id"], "status": "cancelled", "cancelled": True},
    )

    payload = outlook_host.cancel_request("req-1", actor="dashboard")

    assert payload["status"] == "cancelled"
    assert payload["cancelled"] is True
    assert calls == [{"request_id": "req-1", "actor": "dashboard"}]


def test_outlook_host_claims_due_request_and_runs_com_sync(monkeypatch):
    from flux_llm_kb import outlook_host

    events = []
    monkeypatch.setattr(database, "record_outlook_host_heartbeat", lambda **kwargs: events.append(("heartbeat", kwargs)) or kwargs)
    monkeypatch.setattr(
        database,
        "claim_outlook_sync_request",
        lambda host_id="default": {"id": "req-1", "profile_name": "outlook-catchup", "status": "claimed"},
    )
    monkeypatch.setattr(
        outlook_host,
        "sync_outlook_profile",
        lambda profile_name: {"profile": profile_name, "status": "completed", "exported": 3},
    )
    monkeypatch.setattr(database, "complete_outlook_sync_request", lambda **kwargs: events.append(("complete", kwargs)) or kwargs)

    payload = outlook_host.run_once(host_id="host-1")

    assert payload["status"] == "completed"
    assert payload["profile"] == "outlook-catchup"
    assert ("heartbeat", {"host_id": "host-1", "status": "running", "metadata": {}}) in events
    assert events[-1][0] == "complete"
    assert events[-1][1]["status"] == "completed"


def test_outlook_host_heartbeats_while_sync_request_is_running(monkeypatch):
    from flux_llm_kb import outlook_host

    events = []
    saw_two_active_heartbeats = threading.Event()
    monkeypatch.setattr(outlook_host.platform, "system", lambda: "Windows")
    monkeypatch.setitem(sys.modules, "win32com", types.SimpleNamespace(client=types.SimpleNamespace()))
    monkeypatch.setitem(sys.modules, "win32com.client", types.SimpleNamespace())

    def fake_heartbeat(**kwargs):
        events.append(("heartbeat", kwargs))
        metadata = kwargs.get("metadata") or {}
        active = [
            event
            for event_type, event in events
            if event_type == "heartbeat" and (event.get("metadata") or {}).get("active_request_id") == "req-1"
        ]
        if len(active) >= 2:
            saw_two_active_heartbeats.set()
        return kwargs

    monkeypatch.setattr(database, "record_outlook_host_heartbeat", fake_heartbeat)
    monkeypatch.setattr(
        database,
        "claim_outlook_sync_request",
        lambda host_id="default": {"id": "req-1", "profile_name": "outlook-catchup", "status": "claimed"},
    )

    def slow_sync(profile_name):
        assert saw_two_active_heartbeats.wait(1.0)
        return {"profile": profile_name, "status": "completed", "exported": 3}

    monkeypatch.setattr(outlook_host, "sync_outlook_profile", slow_sync)
    monkeypatch.setattr(database, "complete_outlook_sync_request", lambda **kwargs: events.append(("complete", kwargs)) or kwargs)

    payload = outlook_host.run_once(host_id="host-1", heartbeat_interval_seconds=0.01)

    assert payload["status"] == "completed"
    active_heartbeats = [
        event
        for event_type, event in events
        if event_type == "heartbeat" and (event.get("metadata") or {}).get("active_request_id") == "req-1"
    ]
    assert len(active_heartbeats) >= 2
    assert all(event["status"] == "running" for event in active_heartbeats)
    assert events[-1][0] == "complete"


def test_outlook_host_reports_not_windows_without_crashing(monkeypatch):
    from flux_llm_kb import outlook_host

    events = []
    monkeypatch.setattr(outlook_host.platform, "system", lambda: "Linux")
    monkeypatch.setattr(database, "record_outlook_host_heartbeat", lambda **kwargs: events.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "claim_outlook_sync_request", lambda host_id="default": None)

    payload = outlook_host.run_once(host_id="host-1")

    assert payload["status"] == "blocked_not_windows"
    assert events[0]["status"] == "blocked_not_windows"


def test_outlook_host_loop_continues_after_internal_error(monkeypatch):
    from flux_llm_kb import outlook_host

    calls = []
    sleeps = []

    def flaky_once(*, host_id="default"):
        calls.append(host_id)
        if len(calls) == 1:
            raise RuntimeError("database claim failed")
        return {"status": "idle", "host_id": host_id}

    monkeypatch.setattr(outlook_host, "run_once", flaky_once)
    monkeypatch.setattr(outlook_host.time, "sleep", lambda seconds: sleeps.append(seconds))

    payload = outlook_host.run_forever(host_id="host-1", interval_seconds=2, max_iterations=2)

    assert calls == ["host-1", "host-1"]
    assert sleeps == [2, 2]
    assert payload["status"] == "stopped"
    assert payload["iterations"] == 2
    assert payload["error_count"] == 1
    assert "database claim failed" in payload["last_error"]
