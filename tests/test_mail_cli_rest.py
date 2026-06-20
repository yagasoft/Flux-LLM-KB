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
