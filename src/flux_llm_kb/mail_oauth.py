from __future__ import annotations

from dataclasses import dataclass
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import secrets
from typing import Any, Callable
from urllib import parse, request

from . import database


GMAIL_IMAP_SCOPE = "https://mail.google.com/"
DEFAULT_GOOGLE_AUTH_URI = "https://accounts.google.com/o/oauth2/v2/auth"
DEFAULT_GOOGLE_TOKEN_URI = "https://oauth2.googleapis.com/token"
DEFAULT_REDIRECT_URI = "http://127.0.0.1:8765/api/mail/oauth/gmail/callback"

TokenTransport = Callable[[str, dict[str, str]], dict[str, Any]]


class OAuthError(RuntimeError):
    pass


class OAuthAuthRequired(OAuthError):
    pass


class OAuthAuthExpired(OAuthError):
    pass


@dataclass(frozen=True)
class OAuthTokenResponse:
    access_token: str
    refresh_token: str | None
    expires_in: int
    scope: str
    token_type: str
    raw: dict[str, Any]


def load_google_client_config(path: str | Path) -> dict[str, Any]:
    payload = json.loads(Path(path).expanduser().read_text(encoding="utf-8"))
    config = payload.get("installed") or payload.get("web") or payload
    if not config.get("client_id"):
        raise ValueError("Google OAuth client config must contain client_id")
    return config


def create_pkce_verifier() -> str:
    return secrets.token_urlsafe(48)


def pkce_challenge(verifier: str) -> str:
    digest = hashlib.sha256(verifier.encode("ascii")).digest()
    return base64.urlsafe_b64encode(digest).decode("ascii").rstrip("=")


def build_gmail_authorization_url(
    *,
    client_config: dict[str, Any],
    redirect_uri: str,
    state: str,
    code_challenge: str,
) -> str:
    client_config = _client_config(client_config)
    auth_uri = str(client_config.get("auth_uri") or DEFAULT_GOOGLE_AUTH_URI)
    params = {
        "client_id": str(client_config["client_id"]),
        "redirect_uri": redirect_uri,
        "response_type": "code",
        "scope": GMAIL_IMAP_SCOPE,
        "state": state,
        "access_type": "offline",
        "prompt": "consent",
        "code_challenge": code_challenge,
        "code_challenge_method": "S256",
    }
    return f"{auth_uri}?{parse.urlencode(params)}"


def exchange_gmail_code(
    *,
    client_config: dict[str, Any],
    code: str,
    redirect_uri: str,
    code_verifier: str,
    transport: TokenTransport | None = None,
) -> OAuthTokenResponse:
    client_config = _client_config(client_config)
    data = {
        "client_id": str(client_config["client_id"]),
        "client_secret": str(client_config.get("client_secret") or ""),
        "code": code,
        "code_verifier": code_verifier,
        "grant_type": "authorization_code",
        "redirect_uri": redirect_uri,
    }
    return _token_response(_post_token(str(client_config.get("token_uri") or DEFAULT_GOOGLE_TOKEN_URI), data, transport))


def refresh_gmail_access_token(
    *,
    client_config: dict[str, Any],
    refresh_token: str,
    transport: TokenTransport | None = None,
) -> OAuthTokenResponse:
    client_config = _client_config(client_config)
    data = {
        "client_id": str(client_config["client_id"]),
        "client_secret": str(client_config.get("client_secret") or ""),
        "grant_type": "refresh_token",
        "refresh_token": refresh_token,
    }
    return _token_response(_post_token(str(client_config.get("token_uri") or DEFAULT_GOOGLE_TOKEN_URI), data, transport))


def start_gmail_oauth(
    *,
    profile_name: str,
    client_config_path: str | Path,
    redirect_uri: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    profiles = database.list_mail_profiles(name=profile_name, url=url)
    if not profiles:
        raise ValueError(f"mail profile not found: {profile_name}")
    client_config = load_google_client_config(client_config_path)
    redirect_uri = redirect_uri or _default_redirect_uri(client_config)
    state = secrets.token_urlsafe(32)
    verifier = create_pkce_verifier()
    authorization_url = build_gmail_authorization_url(
        client_config=client_config,
        redirect_uri=redirect_uri,
        state=state,
        code_challenge=pkce_challenge(verifier),
    )
    database.create_mail_oauth_state(
        profile_name=profile_name,
        provider="gmail",
        state=state,
        code_verifier=verifier,
        redirect_uri=redirect_uri,
        client_config=client_config,
        client_config_path=str(Path(client_config_path).expanduser().resolve()),
        url=url,
    )
    return {
        "profile_name": profile_name,
        "provider": "gmail",
        "state": state,
        "redirect_uri": redirect_uri,
        "authorization_url": authorization_url,
        "status": "pending_user_authorization",
    }


def complete_gmail_oauth(
    *,
    state: str,
    code: str,
    transport: TokenTransport | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    state_record = database.get_mail_oauth_state(state, url=url)
    if not state_record:
        raise OAuthAuthRequired("unknown OAuth state")
    if state_record.get("consumed_at"):
        raise OAuthAuthRequired("OAuth state was already used")
    expires_at = state_record.get("expires_at")
    if expires_at and _parse_dt(expires_at) < datetime.now(timezone.utc):
        raise OAuthAuthRequired("OAuth state expired")
    token = exchange_gmail_code(
        client_config=state_record["client_config"],
        code=code,
        redirect_uri=state_record["redirect_uri"],
        code_verifier=state_record["code_verifier"],
        transport=transport,
    )
    refresh_token = token.refresh_token
    if not refresh_token:
        existing = database.get_mail_oauth_token(state_record["profile_name"], provider="gmail", url=url)
        refresh_token = existing.get("refresh_token") if existing else None
    if not refresh_token:
        raise OAuthAuthRequired("Google did not return a refresh token; re-run consent with prompt=consent")
    database.upsert_mail_oauth_token(
        profile_name=state_record["profile_name"],
        provider="gmail",
        refresh_token=refresh_token,
        scope=token.scope,
        token_type=token.token_type,
        status="configured",
        client_config=state_record["client_config"],
        expires_at=_expires_at(token.expires_in),
        last_error=None,
        metadata={"client_config_path": state_record.get("client_config_path")},
        url=url,
    )
    database.consume_mail_oauth_state(state=state, url=url)
    return {"profile_name": state_record["profile_name"], "provider": "gmail", "status": "configured"}


def oauth_status(profile_name: str | None = None, *, url: str | None = None) -> dict[str, Any]:
    return _mask_status(database.mail_oauth_status(profile_name=profile_name, url=url))


def access_token_for_profile(
    profile_name: str,
    *,
    provider: str = "gmail",
    transport: TokenTransport | None = None,
    url: str | None = None,
) -> str | None:
    token_record = database.get_mail_oauth_token(profile_name, provider=provider, url=url)
    if not token_record or not token_record.get("refresh_token"):
        return None
    try:
        token = refresh_gmail_access_token(
            client_config=token_record["client_config"],
            refresh_token=token_record["refresh_token"],
            transport=transport,
        )
    except OAuthError as exc:
        status = "auth_expired" if "invalid_grant" in str(exc) else "blocked_auth_required"
        database.update_mail_oauth_token_status(
            profile_name=profile_name,
            provider=provider,
            status=status,
            last_error=str(exc),
            url=url,
        )
        if status == "auth_expired":
            raise OAuthAuthExpired(str(exc)) from exc
        raise
    database.update_mail_oauth_token_status(
        profile_name=profile_name,
        provider=provider,
        status="configured",
        expires_at=_expires_at(token.expires_in),
        last_error=None,
        url=url,
    )
    return token.access_token


def _post_token(url: str, data: dict[str, str], transport: TokenTransport | None) -> dict[str, Any]:
    if transport is not None:
        payload = transport(url, data)
    else:
        encoded = parse.urlencode(data).encode("utf-8")
        req = request.Request(url, data=encoded, headers={"Content-Type": "application/x-www-form-urlencoded"})
        try:
            with request.urlopen(req, timeout=30) as response:  # nosec B310: user-configured Google OAuth endpoint.
                payload = json.loads(response.read().decode("utf-8"))
        except Exception as exc:  # pragma: no cover - network/provider-specific
            raise OAuthError(str(exc)) from exc
    if "error" in payload:
        raise OAuthError(str(payload.get("error_description") or payload["error"]))
    return payload


def _token_response(payload: dict[str, Any]) -> OAuthTokenResponse:
    access_token = payload.get("access_token")
    if not access_token:
        raise OAuthError("token response did not include access_token")
    return OAuthTokenResponse(
        access_token=str(access_token),
        refresh_token=str(payload["refresh_token"]) if payload.get("refresh_token") else None,
        expires_in=int(payload.get("expires_in") or 0),
        scope=str(payload.get("scope") or GMAIL_IMAP_SCOPE),
        token_type=str(payload.get("token_type") or "Bearer"),
        raw=dict(payload),
    )


def _default_redirect_uri(client_config: dict[str, Any]) -> str:
    client_config = _client_config(client_config)
    redirect_uris = client_config.get("redirect_uris") or []
    for redirect_uri in redirect_uris:
        if str(redirect_uri).startswith("http://127.0.0.1"):
            return str(redirect_uri)
    return str(redirect_uris[0]) if redirect_uris else DEFAULT_REDIRECT_URI


def _expires_at(expires_in: int) -> str:
    return (datetime.now(timezone.utc) + timedelta(seconds=max(0, int(expires_in)))).isoformat()


def _parse_dt(value: Any) -> datetime:
    if isinstance(value, datetime):
        parsed = value
    else:
        parsed = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed


def _mask_status(payload: dict[str, Any]) -> dict[str, Any]:
    clean = json.loads(json.dumps(payload, default=str))
    for profile in clean.get("profiles", []):
        profile.pop("refresh_token", None)
        profile.pop("client_config", None)
        profile["has_refresh_token"] = bool(profile.get("has_refresh_token"))
    return clean


def _client_config(client_config: dict[str, Any]) -> dict[str, Any]:
    return client_config.get("installed") or client_config.get("web") or client_config
