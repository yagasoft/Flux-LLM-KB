from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum
import re
from typing import Mapping


_MAXIMUM_SCANNED_CHARACTERS = 16 * 1024
_WITHHELD_REASON = "secret-content-withheld"
_SECRET_ASSIGNMENT = re.compile(
    r"\b(?:password|pwd|access[_-]?token|api[_-]?key|client[_-]?secret|connection\s*string)\s*[:=]",
    re.IGNORECASE,
)
_CREDENTIAL_HEADER = re.compile(r"\b(?:authorization|cookie|set-cookie)\s*:", re.IGNORECASE)
_PRIVATE_KEY_ENVELOPE = re.compile(
    r"-{5}BEGIN[ \t]+(?:[A-Z0-9][A-Z0-9 -]{0,63}[ \t]+)?PRIVATE[ \t]+KEY(?:[ \t]+BLOCK)?-{5}",
    re.IGNORECASE,
)
_CREDENTIAL_URI = re.compile(
    r"\b[a-z][a-z0-9+.-]{0,31}://[^\s/?#:@]+:[^\s/?#@]+@",
    re.IGNORECASE,
)
_FORWARDED_HEADERS = {
    "forwarded",
    "via",
    "x-real-ip",
    "true-client-ip",
    "cf-connecting-ip",
}


class LocalDisclosureKind(StrEnum):
    RETAINED_DETAIL = "retained_detail"
    CODE_EXCERPT = "code_excerpt"
    SYMBOL = "symbol"
    REFERENCE = "reference"
    DIAGNOSTIC = "diagnostic"
    AUDIT_EVIDENCE = "audit_evidence"


@dataclass(frozen=True)
class LocalDisclosureResult:
    value: str | None
    withheld: bool
    reason_code: str | None


def evaluate_local_disclosure(value: str, kind: LocalDisclosureKind) -> LocalDisclosureResult:
    """Return retained-derived text only after the bounded local secret check."""
    del kind
    if len(value) > _MAXIMUM_SCANNED_CHARACTERS or _contains_secret(value):
        return LocalDisclosureResult(value=None, withheld=True, reason_code=_WITHHELD_REASON)
    return LocalDisclosureResult(value=value, withheld=False, reason_code=None)


def exceeds_local_disclosure_scan_bound(value: str) -> bool:
    """Expose the fixed scan bound to durable writers without exposing its detector."""
    return len(value) > _MAXIMUM_SCANNED_CHARACTERS


def is_direct_loopback_request(client_host: str | None, headers: Mapping[str, str]) -> bool:
    """Authorise an HTTP request only when its direct peer is loopback and unproxied."""
    if client_host not in {"127.0.0.1", "::1"}:
        return False
    return not any(
        name.lower() in _FORWARDED_HEADERS
        or name.lower().startswith(("forwarded-", "x-forwarded", "x-original", "proxy", "x-proxy"))
        for name in headers
    )


def _contains_secret(value: str) -> bool:
    return (
        "secret-content-sentinel" in value
        or _PRIVATE_KEY_ENVELOPE.search(value) is not None
        or _CREDENTIAL_URI.search(value) is not None
        or _SECRET_ASSIGNMENT.search(value) is not None
        or _CREDENTIAL_HEADER.search(value) is not None
    )
