import os

import pytest

from flux_llm_kb import database
from flux_llm_kb.settings import SettingsService
from flux_llm_kb.settings_registry import APPLY_REINDEX_REQUIRED, SETTING_REGISTRY


def test_settings_registry_contains_runtime_and_mail_defaults():
    keys = {definition.key for definition in SETTING_REGISTRY}

    assert "retrieval.token_budget" in keys
    assert "crawler.max_inline_bytes" in keys
    assert "watcher.interval_seconds" in keys
    assert "mail.imap.poll_interval_seconds" in keys
    assert "mail.post_process.default_policy" in keys


def test_settings_service_uses_env_over_database_and_masks_secret(monkeypatch):
    stored = {
        "retrieval.token_budget": {"value": 800, "updated_at": "db-time"},
        "mail.imap.oauth_refresh_token": {"value": "stored-token", "updated_at": "db-time"},
    }
    monkeypatch.setenv("FLUX_KB_TOKEN_BUDGET", "1600")
    monkeypatch.setattr(database, "get_runtime_setting", lambda key: stored.get(key))

    service = SettingsService()
    token_budget = service.resolve("retrieval.token_budget")
    secret = service.resolve("mail.imap.oauth_refresh_token")

    assert token_budget.value == 1600
    assert token_budget.source == "env"
    assert secret.value == "***"
    assert secret.raw_value == "stored-token"
    assert secret.sensitive is True


def test_setting_update_requires_confirmation_for_reindex(monkeypatch):
    calls = []
    monkeypatch.delenv("FLUX_KB_EMBEDDING_MODEL", raising=False)
    monkeypatch.setattr(database, "get_runtime_setting", lambda _key: None)
    monkeypatch.setattr(database, "set_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"key": kwargs["key"]})
    monkeypatch.setattr(database, "enqueue_runtime_control_request", lambda **_kwargs: {"id": "request-1"})

    service = SettingsService()

    with pytest.raises(ValueError, match="confirmation"):
        service.set("embedding.model", "flux-hash-v2", actor="tester")

    result = service.set("embedding.model", "flux-hash-v2", actor="tester", confirmed=True)

    assert result["apply_mode"] == APPLY_REINDEX_REQUIRED
    assert calls[0]["key"] == "embedding.model"
    assert calls[0]["value"] == "flux-hash-v2"


def test_setting_reset_removes_database_override(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "delete_runtime_setting", lambda **kwargs: calls.append(kwargs) or {"deleted": True})

    result = SettingsService().reset("retrieval.token_budget", actor="tester")

    assert result == {"deleted": True}
    assert calls[0]["key"] == "retrieval.token_budget"
