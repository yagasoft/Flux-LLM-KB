import base64
import json
from pathlib import Path

import pytest

from flux_llm_kb import database


CLIENT_CONFIG = {
    "installed": {
        "client_id": "client-id.apps.googleusercontent.com",
        "client_secret": "client-secret",
        "auth_uri": "https://accounts.google.com/o/oauth2/v2/auth",
        "token_uri": "https://oauth2.googleapis.com/token",
        "redirect_uris": ["http://127.0.0.1:8765/api/mail/oauth/gmail/callback"],
    }
}


def test_gmail_authorization_url_uses_pkce_loopback_and_mail_scope():
    from flux_llm_kb.mail_oauth import (
        GMAIL_IMAP_SCOPE,
        build_gmail_authorization_url,
        pkce_challenge,
    )

    url = build_gmail_authorization_url(
        client_config=CLIENT_CONFIG,
        redirect_uri="http://127.0.0.1:8765/api/mail/oauth/gmail/callback",
        state="state-1",
        code_challenge=pkce_challenge("verifier"),
    )

    assert "https://accounts.google.com/o/oauth2/v2/auth" in url
    assert "response_type=code" in url
    assert "access_type=offline" in url
    assert "code_challenge_method=S256" in url
    assert "state=state-1" in url
    assert f"scope={GMAIL_IMAP_SCOPE.replace(':', '%3A').replace('/', '%2F')}" in url


def test_pkce_challenge_matches_s256_encoding():
    from flux_llm_kb.mail_oauth import pkce_challenge

    expected = base64.urlsafe_b64encode(__import__("hashlib").sha256(b"verifier").digest()).decode("ascii").rstrip("=")

    assert pkce_challenge("verifier") == expected


def test_exchange_and_refresh_use_fake_http_transport():
    from flux_llm_kb.mail_oauth import exchange_gmail_code, refresh_gmail_access_token

    calls = []

    def fake_transport(url, data):
        calls.append((url, data))
        if data["grant_type"] == "authorization_code":
            return {
                "access_token": "access-1",
                "refresh_token": "refresh-1",
                "expires_in": 3600,
                "scope": "https://mail.google.com/",
                "token_type": "Bearer",
            }
        return {
            "access_token": "access-2",
            "expires_in": 1800,
            "scope": "https://mail.google.com/",
            "token_type": "Bearer",
        }

    exchanged = exchange_gmail_code(
        client_config=CLIENT_CONFIG,
        code="code-1",
        redirect_uri="http://127.0.0.1:8765/api/mail/oauth/gmail/callback",
        code_verifier="verifier",
        transport=fake_transport,
    )
    refreshed = refresh_gmail_access_token(
        client_config=CLIENT_CONFIG,
        refresh_token="refresh-1",
        transport=fake_transport,
    )

    assert exchanged.access_token == "access-1"
    assert exchanged.refresh_token == "refresh-1"
    assert refreshed.access_token == "access-2"
    assert calls[0][1]["code_verifier"] == "verifier"
    assert calls[1][1]["refresh_token"] == "refresh-1"


def test_oauth_start_creates_state_and_masks_status(monkeypatch, tmp_path):
    from flux_llm_kb import mail_oauth

    config_path = tmp_path / "client.json"
    config_path.write_text(json.dumps(CLIENT_CONFIG), encoding="utf-8")
    states = []
    metadata_updates = []

    monkeypatch.setattr(database, "list_mail_profiles", lambda name=None, url=None: [{"id": "profile-id", "name": name, "metadata": {}}])
    monkeypatch.setattr(database, "create_mail_oauth_state", lambda **kwargs: states.append(kwargs) or {"state": kwargs["state"]})
    monkeypatch.setattr(database, "update_mail_profile_metadata", lambda **kwargs: metadata_updates.append(kwargs) or {"name": kwargs["name"], "metadata": kwargs["metadata"]})
    monkeypatch.setattr(
        database,
        "mail_oauth_status",
        lambda profile_name=None, url=None: {
            "profiles": [
                {
                    "profile_name": profile_name,
                    "provider": "gmail",
                    "status": "configured",
                    "has_refresh_token": True,
                    "refresh_token": "secret",
                }
            ]
        },
    )

    started = mail_oauth.start_gmail_oauth(
        profile_name="gmail",
        client_config_path=config_path,
        redirect_uri="http://127.0.0.1:8765/api/mail/oauth/gmail/callback",
    )
    status = mail_oauth.oauth_status("gmail")

    assert started["profile_name"] == "gmail"
    assert "authorization_url" in started
    assert started["auth_url"] == started["authorization_url"]
    assert states[0]["provider"] == "gmail"
    assert states[0]["profile_name"] == "gmail"
    assert metadata_updates[0]["metadata"]["gmail_oauth_client_config_path"] == str(config_path)
    assert status["profiles"][0]["has_refresh_token"] is True
    assert "secret" not in json.dumps(status)


def test_complete_oauth_stores_refresh_token_without_persisting_access_token(monkeypatch):
    from flux_llm_kb import mail_oauth

    stored = {}
    monkeypatch.setattr(
        database,
        "get_mail_oauth_state",
        lambda state, url=None: {
            "state": state,
            "profile_name": "gmail",
            "provider": "gmail",
            "client_config": CLIENT_CONFIG,
            "redirect_uri": "http://127.0.0.1:8765/api/mail/oauth/gmail/callback",
            "code_verifier": "verifier",
            "consumed_at": None,
        },
    )
    monkeypatch.setattr(database, "consume_mail_oauth_state", lambda **kwargs: None)
    monkeypatch.setattr(database, "upsert_mail_oauth_token", lambda **kwargs: stored.update(kwargs) or kwargs)
    monkeypatch.setattr(
        mail_oauth,
        "exchange_gmail_code",
        lambda **kwargs: mail_oauth.OAuthTokenResponse(
            access_token="access",
            refresh_token="refresh",
            expires_in=3600,
            scope="https://mail.google.com/",
            token_type="Bearer",
            raw={"access_token": "access", "refresh_token": "refresh"},
        ),
    )

    result = mail_oauth.complete_gmail_oauth(state="state-1", code="code-1")

    assert result["status"] == "configured"
    assert stored["refresh_token"] == "refresh"
    assert "access" not in json.dumps(stored)


def test_access_token_refresh_reports_auth_expired(monkeypatch):
    from flux_llm_kb import mail_oauth

    monkeypatch.setattr(
        database,
        "get_mail_oauth_token",
        lambda profile_name, provider="gmail", url=None: {
            "profile_name": profile_name,
            "provider": provider,
            "refresh_token": "refresh",
            "client_config": CLIENT_CONFIG,
            "status": "configured",
        },
    )
    monkeypatch.setattr(database, "update_mail_oauth_token_status", lambda **kwargs: kwargs)
    monkeypatch.setattr(mail_oauth, "refresh_gmail_access_token", lambda **kwargs: (_ for _ in ()).throw(mail_oauth.OAuthError("invalid_grant")))

    with pytest.raises(mail_oauth.OAuthAuthExpired):
        mail_oauth.access_token_for_profile("gmail")
