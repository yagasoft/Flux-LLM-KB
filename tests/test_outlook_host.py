from __future__ import annotations

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


def test_outlook_host_reports_not_windows_without_crashing(monkeypatch):
    from flux_llm_kb import outlook_host

    events = []
    monkeypatch.setattr(outlook_host.platform, "system", lambda: "Linux")
    monkeypatch.setattr(database, "record_outlook_host_heartbeat", lambda **kwargs: events.append(kwargs) or kwargs)
    monkeypatch.setattr(database, "claim_outlook_sync_request", lambda host_id="default": None)

    payload = outlook_host.run_once(host_id="host-1")

    assert payload["status"] == "blocked_not_windows"
    assert events[0]["status"] == "blocked_not_windows"
