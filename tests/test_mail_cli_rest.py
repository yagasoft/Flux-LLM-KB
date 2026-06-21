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


def test_rest_exposes_settings_and_mail_routes(monkeypatch):
    monkeypatch.setattr(database, "check_database", lambda: database.DatabaseStatus(True, "ok"))
    app = create_app()
    routes = {route.path for route in app.routes}

    assert "/api/settings" in routes
    assert "/api/settings/{key}" in routes
    assert "/api/mail/status" in routes
    assert "/api/mail/profiles" in routes
    assert "/api/mail/oauth/gmail/start" in routes
    assert "/api/mail/oauth/gmail/callback" in routes
    assert "/api/mail/oauth/status" in routes
    assert "/api/outlook-host/status" in routes
    assert "/api/outlook-host/request-sync" in routes
    assert "/api/outlook-host/profiles/{name}/enable" in routes
    assert "/api/outlook-host/profiles/{name}/disable" in routes


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
