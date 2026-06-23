from pathlib import Path

from flux_llm_kb import database


def _profile(tmp_path):
    return {
        "name": "gmail",
        "source_type": "imap",
        "enabled": True,
        "account": "me@gmail.com",
        "server": "imap.gmail.com",
        "folder_paths": ["FluxCapture"],
        "spool_path": str(tmp_path),
        "post_process_policy": "none",
        "metadata": {},
        "sync_enabled": True,
        "sync_interval_seconds": 900,
    }


class EmptyImapClient:
    def __init__(self, host):
        self.host = host

    def authenticate_xoauth2(self, user, token):
        self.auth = (user, token)

    def select(self, folder):
        return "OK", [b"1"]

    def response(self, key):
        return "OK", [b"1"]

    def uid(self, command, *args):
        if command == "SEARCH":
            return "OK", [b""]
        raise AssertionError(command)

    def close(self):
        return None

    def logout(self):
        return None


def test_scheduled_imap_sync_claims_explicit_run_before_work(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    profile = _profile(tmp_path)
    claimed = [{"id": "run-1", "attempt_count": 1, "trigger": "schedule", **profile}]
    calls = {"claim": [], "running": [], "complete": []}

    monkeypatch.setattr(
        database,
        "claim_due_imap_sync_runs",
        lambda *, limit=10, worker_id="flux-kb-mail-worker": calls["claim"].append((limit, worker_id)) or claimed,
    )
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: calls["running"].append(kwargs))
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: calls["complete"].append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: profile | {"metadata": kwargs["metadata"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: "fresh-access-token")

    result = mail_ingestion.sync_due_mail_profiles(limit=3, worker_id="worker-a", imap_client_factory=EmptyImapClient)

    assert calls["claim"] == [(3, "worker-a")]
    assert calls["running"] == [{"run_id": "run-1", "worker_id": "worker-a"}]
    assert calls["complete"][0]["run_id"] == "run-1"
    assert calls["complete"][0]["status"] == "completed"
    assert result["profiles"][0]["run_id"] == "run-1"
    assert result["profiles"][0]["status"] == "completed"


def test_manual_imap_sync_creates_explicit_run_request(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    profile = _profile(tmp_path)
    calls = {"create": [], "running": [], "complete": []}

    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None: [profile])
    monkeypatch.setattr(
        database,
        "create_imap_sync_run",
        lambda **kwargs: calls["create"].append(kwargs) or {"id": "run-manual", "status": "queued"},
    )
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **kwargs: calls["running"].append(kwargs))
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: calls["complete"].append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: profile | {"metadata": kwargs["metadata"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: "fresh-access-token")

    result = mail_ingestion.sync_mail_profile(profile_name="gmail", imap_client_factory=EmptyImapClient)

    assert calls["create"] == [{"profile_name": "gmail", "trigger": "manual", "requested_by": "dashboard"}]
    assert calls["running"] == [{"run_id": "run-manual", "worker_id": "manual"}]
    assert calls["complete"][0]["run_id"] == "run-manual"
    assert result["profiles"][0]["run_id"] == "run-manual"
    assert result["profiles"][0]["status"] == "completed"


def test_auth_required_completes_claimed_run_as_blocked_without_fetching(monkeypatch, tmp_path):
    from flux_llm_kb import mail_ingestion, mail_oauth

    profile = _profile(tmp_path)
    claimed = [{"id": "run-1", "attempt_count": 1, "trigger": "schedule", **profile}]
    completed = []
    factories = []

    monkeypatch.setattr(database, "claim_due_imap_sync_runs", lambda **_kwargs: claimed)
    monkeypatch.setattr(database, "mark_mail_sync_run_running", lambda **_kwargs: None)
    monkeypatch.setattr(database, "complete_mail_sync_run", lambda **kwargs: completed.append(kwargs) or {"id": kwargs["run_id"], "status": kwargs["status"]})
    monkeypatch.setattr(mail_ingestion, "sync_mail_spool", lambda profile_name=None: {"profiles": [], "count": 0})
    monkeypatch.setattr(mail_oauth, "access_token_for_profile", lambda profile_name: None)

    def client_factory(host):
        factories.append(host)
        return EmptyImapClient(host)

    result = mail_ingestion.sync_due_mail_profiles(limit=1, worker_id="worker-a", imap_client_factory=client_factory)

    assert factories == []
    assert completed[0]["status"] == "blocked_auth_required"
    assert completed[0]["backoff_seconds"] >= 3600
    assert result["profiles"][0]["status"] == "blocked_auth_required"


def test_database_imap_scheduler_state_machine_uses_atomic_claims_and_run_history():
    source = Path(database.__file__).read_text(encoding="utf-8")
    migrations = "\n".join(path.read_text(encoding="utf-8") for path in sorted((Path(database.__file__).parent / "sql").glob("*.sql")))

    assert "def claim_due_imap_sync_runs" in source
    claim_function = source.split("def claim_due_imap_sync_runs", 1)[1].split("def ", 1)[0]
    assert "FOR UPDATE SKIP LOCKED" in claim_function
    assert "NOT EXISTS" in claim_function
    assert "status IN ('queued', 'claimed', 'running', 'backoff')" in claim_function
    assert "drift_seconds" in claim_function
    assert "missed_runs" in claim_function

    assert "def create_imap_sync_run" in source
    assert "def complete_mail_sync_run" in source
    assert "def list_mail_sync_runs" in source
    assert "scheduler" in source
    assert "claimed_by" in migrations
    assert "next_attempt_at" in migrations
    assert "drift_seconds" in migrations
