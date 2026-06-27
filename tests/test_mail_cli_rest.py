import json

from flux_llm_kb import cli, database
from flux_llm_kb.rest_api import create_app


def test_cli_settings_list_outputs_masked_values(monkeypatch, capsys):
    from flux_llm_kb.settings import ResolvedSetting

    class FakeSettingsService:
        def list(self):
            return [
                ResolvedSetting(
                    key="mail.imap.oauth_refresh_token",
                    value="***",
                    raw_value="secret",
                    source="db",
                    sensitive=True,
                    category="mail",
                    apply_mode="reload",
                    read_only=False,
                    affected_components=("mail",),
                    description="token",
                )
            ]

    monkeypatch.setattr(cli, "SettingsService", FakeSettingsService)

    assert cli.main(["settings", "list"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload[0]["value"] == "***"
    assert "raw_value" not in payload[0]


def test_cli_mail_profile_add_imap_registers_profile(monkeypatch, tmp_path, capsys):
    from flux_llm_kb import mail_ingestion

    monkeypatch.setattr(
        mail_ingestion,
        "add_mail_profile",
        lambda **kwargs: {"name": kwargs["name"], "source_type": kwargs["source_type"], "spool_path": str(kwargs["spool_path"])},
    )

    assert cli.main(
        [
            "mail",
            "profile",
            "add-imap",
            "--name",
            "gmail",
            "--account",
            "me@gmail.com",
            "--folder",
            "FluxCapture",
            "--spool",
            str(tmp_path),
        ]
    ) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["name"] == "gmail"
    assert payload["source_type"] == "imap"


def test_cli_mail_profile_add_outlook_defaults_to_no_post_process(monkeypatch, tmp_path, capsys):
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(mail_ingestion, "add_mail_profile", lambda **kwargs: captured.update(kwargs) or {"name": kwargs["name"]})

    assert cli.main(
        [
            "mail",
            "profile",
            "add-outlook",
            "--name",
            "outlook-catchup",
            "--folder",
            "Mailbox - Me\\Inbox\\Flux Capture",
            "--spool",
            str(tmp_path),
        ]
    ) == 0

    assert json.loads(capsys.readouterr().out)["name"] == "outlook-catchup"
    assert captured["source_type"] == "outlook_com"
    assert captured["account"] is None
    assert captured["server"] is None
    assert captured["post_process_policy"] == "none"


def test_rest_exposes_settings_and_mail_routes(monkeypatch):
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    app = create_app()
    routes = {route.path for route in app.routes}

    assert "/" in routes
    assert "/api/settings" in routes
    assert "/api/settings/{key}" in routes
    assert "/api/mail/status" in routes
    assert "/api/mail/profiles" in routes
    assert "/api/mail/profiles/{profile_name}/oauth-client-config" in routes
    assert "/api/mail/profiles/{profile_name}/post-process/dry-run" in routes
    assert "/api/mail/post-process/events" in routes
    assert "/api/mail/oauth/gmail/start" in routes
    assert "/api/mail/oauth/gmail/callback" in routes
    assert "/api/mail/oauth/status" in routes
    assert "/api/outlook-host/status" in routes
    assert "/api/outlook-host/request-sync" in routes
    assert "/api/outlook-host/profiles/{name}/enable" in routes
    assert "/api/outlook-host/profiles/{name}/disable" in routes


def test_rest_mail_post_process_dry_run_calls_mail_ingestion(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(
        mail_ingestion,
        "dry_run_mail_post_process",
        lambda **kwargs: captured.update(kwargs) or {
            "profile_name": kwargs["profile_name"],
            "dry_run": True,
            "events": [{"profile_name": kwargs["profile_name"], "status": "planned"}],
        },
    )
    client = TestClient(create_app())

    response = client.post("/api/mail/profiles/gmail/post-process/dry-run", json={"limit": 3})

    assert response.status_code == 200
    assert captured == {"profile_name": "gmail", "limit": 3}
    assert response.json()["events"][0]["status"] == "planned"


def test_rest_mail_post_process_events_list_database_events(monkeypatch):
    from fastapi.testclient import TestClient

    captured = {}
    monkeypatch.setattr(
        database,
        "list_mail_post_process_events",
        lambda **kwargs: captured.update(kwargs) or [{"profile_name": kwargs["profile_name"], "status": "applied"}],
    )
    client = TestClient(create_app())

    response = client.get("/api/mail/post-process/events?profile_name=gmail&limit=4")

    assert response.status_code == 200
    assert captured == {"profile_name": "gmail", "limit": 4}
    assert response.json()["events"][0]["status"] == "applied"


def test_cli_mail_oauth_start_outputs_authorization_url(monkeypatch, tmp_path, capsys):
    from flux_llm_kb import mail_oauth

    config_path = tmp_path / "client.json"
    config_path.write_text("{}", encoding="utf-8")
    monkeypatch.setattr(
        mail_oauth,
        "start_gmail_oauth",
        lambda **kwargs: {
            "profile_name": kwargs["profile_name"],
            "authorization_url": "https://accounts.google.com/o/oauth2/v2/auth?state=abc",
            "state": "abc",
        },
    )

    assert cli.main(["mail", "oauth", "gmail", "start", "--profile", "gmail", "--client-config", str(config_path)]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["profile_name"] == "gmail"
    assert payload["authorization_url"].startswith("https://accounts.google.com/")


def test_rest_mail_oauth_start_reports_missing_client_config_without_internal_error(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_oauth

    monkeypatch.setattr(
        mail_oauth,
        "start_gmail_oauth",
        lambda **_kwargs: (_ for _ in ()).throw(FileNotFoundError("missing client json")),
    )
    client = TestClient(create_app())

    response = client.post(
        "/api/mail/oauth/gmail/start",
        json={"profile_name": "gmail", "client_config_path": "private/missing-client.json"},
    )

    assert response.status_code == 200
    assert response.json()["status"] == "blocked_config_missing"
    assert "missing client json" in response.json()["message"]


def test_rest_mail_oauth_start_aliases_authorization_url_for_dashboard(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_oauth

    monkeypatch.setattr(
        mail_oauth,
        "start_gmail_oauth",
        lambda **kwargs: {
            "profile_name": kwargs["profile_name"],
            "authorization_url": "https://accounts.google.com/o/oauth2/v2/auth?state=abc",
            "status": "pending_user_authorization",
        },
    )
    client = TestClient(create_app())

    response = client.post(
        "/api/mail/oauth/gmail/start",
        json={"profile_name": "gmail", "client_config_path": "private/client.json"},
    )

    assert response.status_code == 200
    assert response.json()["auth_url"] == "https://accounts.google.com/o/oauth2/v2/auth?state=abc"


def test_rest_mail_profile_oauth_client_config_path_persists_metadata(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(
        mail_ingestion,
        "update_mail_profile_oauth_client_config_path",
        lambda **kwargs: captured.update(kwargs) or {
            "name": kwargs["profile_name"],
            "metadata": {"gmail_oauth_client_config_path": kwargs["client_config_path"]},
        },
    )
    client = TestClient(create_app())

    response = client.put(
        "/api/mail/profiles/gmail/oauth-client-config",
        json={"client_config_path": "private/client_secret_custom.json"},
    )

    assert response.status_code == 200
    assert captured == {"profile_name": "gmail", "client_config_path": "private/client_secret_custom.json"}
    assert response.json()["metadata"]["gmail_oauth_client_config_path"] == "private/client_secret_custom.json"


def test_root_oauth_callback_completes_gmail_consent(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_oauth

    captured = {}
    monkeypatch.setattr(
        mail_oauth,
        "complete_gmail_oauth",
        lambda **kwargs: captured.update(kwargs) or {"profile_name": "gmail", "provider": "gmail", "status": "configured"},
    )
    client = TestClient(create_app())

    response = client.get("/?state=state-1&code=code-1&scope=https%3A%2F%2Fmail.google.com%2F")

    assert response.status_code == 200
    assert captured == {"state": "state-1", "code": "code-1"}
    assert "Gmail OAuth configured" in response.text
    assert "/dashboard?tab=mail" in response.text


def test_root_oauth_callback_reports_provider_error_without_iis(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb import mail_oauth

    called = False

    def fake_complete(**_kwargs):
        nonlocal called
        called = True

    monkeypatch.setattr(mail_oauth, "complete_gmail_oauth", fake_complete)
    client = TestClient(create_app())

    response = client.get("/?state=state-1&error=access_denied")

    assert response.status_code == 200
    assert called is False
    assert "Gmail OAuth did not complete" in response.text
    assert "access_denied" in response.text


def test_cli_mail_oauth_status_masks_token_state(monkeypatch, capsys):
    from flux_llm_kb import mail_oauth

    monkeypatch.setattr(
        mail_oauth,
        "oauth_status",
        lambda profile_name=None: {"profiles": [{"profile_name": profile_name, "status": "configured", "has_refresh_token": True}]},
    )

    assert cli.main(["mail", "oauth", "status", "--profile", "gmail"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["profiles"][0]["has_refresh_token"] is True
    assert "secret" not in json.dumps(payload).lower()


def test_cli_outlook_host_status_outputs_payload(monkeypatch, capsys):
    from flux_llm_kb import outlook_host

    monkeypatch.setattr(outlook_host, "status", lambda: {"host": {"status": "host_offline"}})

    assert cli.main(["outlook-host", "status"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["host"]["status"] == "host_offline"


def test_cli_outlook_host_sync_requests_profile(monkeypatch, capsys):
    from flux_llm_kb import outlook_host

    monkeypatch.setattr(
        outlook_host,
        "request_sync",
        lambda profile_name, actor="cli": {"profile_name": profile_name, "status": "pending", "actor": actor},
    )

    assert cli.main(["outlook-host", "sync", "--profile", "outlook-catchup"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["profile_name"] == "outlook-catchup"
    assert payload["status"] == "pending"


def test_cli_mail_profile_add_imap_accepts_schedule_fields(monkeypatch, tmp_path, capsys):
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(mail_ingestion, "add_mail_profile", lambda **kwargs: captured.update(kwargs) or {"name": kwargs["name"]})

    assert cli.main(
        [
            "mail",
            "profile",
            "add-imap",
            "--name",
            "gmail-scheduled",
            "--account",
            "me@gmail.com",
            "--folder",
            "FluxCapture",
            "--spool",
            str(tmp_path),
            "--sync-enabled",
            "--sync-interval-seconds",
            "900",
            "--sync-window-days",
            "14",
            "--max-messages-per-run",
            "50",
        ]
    ) == 0

    assert json.loads(capsys.readouterr().out)["name"] == "gmail-scheduled"
    assert captured["sync_enabled"] is True
    assert captured["sync_interval_seconds"] == 900
    assert captured["sync_window_days"] == 14
    assert captured["max_messages_per_run"] == 50


def test_cli_mail_profile_add_imap_accepts_post_process_fields(monkeypatch, tmp_path, capsys):
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(mail_ingestion, "add_mail_profile", lambda **kwargs: captured.update(kwargs) or {"name": kwargs["name"]})

    assert cli.main(
        [
            "mail",
            "profile",
            "add-imap",
            "--name",
            "gmail",
            "--account",
            "me@gmail.com",
            "--folder",
            "FluxCapture",
            "--spool",
            str(tmp_path),
            "--post-process",
            "remove_label",
            "--processed-folder",
            "FluxProcessed",
            "--trash-folder",
            "[Gmail]/Trash",
            "--confirm-destructive-post-process",
        ]
    ) == 0

    assert json.loads(capsys.readouterr().out)["name"] == "gmail"
    assert captured["post_process_policy"] == "remove_label"
    assert captured["processed_folder"] == "FluxProcessed"
    assert captured["trash_folder"] == "[Gmail]/Trash"
    assert captured["destructive_post_process_confirmed"] is True


def test_cli_mail_post_process_dry_run_outputs_planned_events(monkeypatch, capsys):
    from flux_llm_kb import mail_ingestion

    captured = {}
    monkeypatch.setattr(
        mail_ingestion,
        "dry_run_mail_post_process",
        lambda **kwargs: captured.update(kwargs) or {"profile_name": kwargs["profile_name"], "events": [{"status": "planned"}]},
    )

    assert cli.main(["mail", "post-process", "dry-run", "--profile", "gmail", "--limit", "2"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert captured == {"profile_name": "gmail", "limit": 2}
    assert payload["events"][0]["status"] == "planned"


def test_cli_mail_post_process_events_outputs_database_events(monkeypatch, capsys):
    captured = {}
    monkeypatch.setattr(
        database,
        "list_mail_post_process_events",
        lambda **kwargs: captured.update(kwargs) or [{"profile_name": kwargs["profile_name"], "status": "applied"}],
    )

    assert cli.main(["mail", "post-process", "events", "--profile", "gmail", "--limit", "2"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert captured == {"profile_name": "gmail", "limit": 2}
    assert payload["events"][0]["status"] == "applied"
