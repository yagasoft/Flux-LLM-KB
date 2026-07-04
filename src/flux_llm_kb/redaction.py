from __future__ import annotations

from dataclasses import dataclass
import os
import re
from typing import Pattern


@dataclass(frozen=True)
class RedactionFinding:
    kind: str
    value: str


@dataclass(frozen=True)
class _Rule:
    kind: str
    pattern: Pattern[str]
    group: int = 0


_RULES = [
    _Rule("openai_api_key", re.compile(r"\bsk-[A-Za-z0-9_-]{20,}\b")),
    _Rule(
        "password_assignment",
        re.compile(r"(?i)\b(password|passwd|pwd)\s*[:=]\s*([^\s;,'\"]+)"),
        group=2,
    ),
]


def redact_text(text: str) -> tuple[str, list[RedactionFinding]]:
    """Redact common secrets before persistence."""
    if not redactions_enabled():
        return text, []

    findings: list[RedactionFinding] = []
    redacted = text

    for rule in _RULES:
        redacted = _apply_rule(redacted, rule, findings)

    return redacted, findings


def redactions_enabled() -> bool:
    env_value = os.environ.get("FLUX_KB_REDACTIONS_ENABLED")
    if env_value is not None:
        return _bool_from_text(env_value)
    try:
        from .settings import SettingsService

        return bool(SettingsService().resolve("privacy.redactions.enabled").raw_value)
    except Exception:
        return False


def _bool_from_text(value: str) -> bool:
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


def _apply_rule(text: str, rule: _Rule, findings: list[RedactionFinding]) -> str:
    def replace(match: re.Match[str]) -> str:
        value = match.group(rule.group)
        if value.startswith("[REDACTED:"):
            return match.group(0)
        findings.append(RedactionFinding(kind=rule.kind, value=value))
        marker = f"[REDACTED:{rule.kind}]"
        if rule.group == 0:
            return marker
        start, end = match.span(rule.group)
        whole = match.group(0)
        rel_start = start - match.start(0)
        rel_end = end - match.start(0)
        return f"{whole[:rel_start]}{marker}{whole[rel_end:]}"

    return rule.pattern.sub(replace, text)
