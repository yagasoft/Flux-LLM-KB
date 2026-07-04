from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
import hmac
import ipaddress
import json
from typing import Any
from urllib.parse import urlparse


@dataclass(frozen=True)
class CallbackPolicy:
    allow_private_networks: bool = True
    allow_loopback: bool = True
    allowlist: tuple[str, ...] = ()


@dataclass(frozen=True)
class CallbackUrlDecision:
    allowed: bool
    reason: str
    url: str


def validate_callback_url(url: str, policy: CallbackPolicy | None = None) -> CallbackUrlDecision:
    effective = policy or CallbackPolicy()
    clean_url = str(url or "").strip()
    parsed = urlparse(clean_url)
    if parsed.scheme not in {"http", "https"}:
        return CallbackUrlDecision(False, "callback URL must use http or https", clean_url)
    if not parsed.hostname:
        return CallbackUrlDecision(False, "callback URL must include a host", clean_url)
    host = parsed.hostname.lower()
    allowlist_match = _matches_allowlist(clean_url, host, effective.allowlist)
    if allowlist_match:
        return CallbackUrlDecision(True, "allowlisted", clean_url)
    if host == "localhost" and effective.allow_loopback:
        return CallbackUrlDecision(True, "loopback", clean_url)
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return CallbackUrlDecision(False, "callback URL host is not loopback, private-network, or allowlisted", clean_url)
    if address.is_loopback and effective.allow_loopback:
        return CallbackUrlDecision(True, "loopback", clean_url)
    if address.is_private and effective.allow_private_networks:
        return CallbackUrlDecision(True, "private-network", clean_url)
    return CallbackUrlDecision(False, "callback URL host is not loopback, private-network, or allowlisted", clean_url)


def sign_callback(
    *,
    body: bytes,
    secret: str,
    message_id: str,
    timestamp: str | None = None,
) -> dict[str, str]:
    if not secret:
        raise ValueError("callback signing secret is required")
    clean_timestamp = timestamp or datetime.now(UTC).isoformat().replace("+00:00", "Z")
    signing_input = b".".join(
        [
            clean_timestamp.encode("utf-8"),
            str(message_id or "").encode("utf-8"),
            body,
        ]
    )
    digest = hmac.new(secret.encode("utf-8"), signing_input, hashlib.sha256).hexdigest()
    return {
        "Content-Type": "application/json",
        "Idempotency-Key": str(message_id or ""),
        "X-Flux-KB-Timestamp": clean_timestamp,
        "X-Flux-KB-Signature": f"v1={digest}",
    }


def build_callback_body(event: dict[str, Any]) -> bytes:
    return json.dumps(event, sort_keys=True, separators=(",", ":"), default=str).encode("utf-8")


def _matches_allowlist(url: str, host: str, allowlist: tuple[str, ...]) -> bool:
    for item in allowlist:
        clean = str(item or "").strip().lower().rstrip("/")
        if not clean:
            continue
        if clean.startswith("http://") or clean.startswith("https://"):
            if url.lower().rstrip("/").startswith(clean):
                return True
            continue
        if host == clean or host.endswith(f".{clean}"):
            return True
    return False
