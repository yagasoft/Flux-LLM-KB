from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from . import database
from .service import KnowledgeService
from .settings import SettingsService


@dataclass(frozen=True)
class CodexHookPolicySettings:
    enabled: bool = True
    preflight_enabled: bool = True
    capture_enabled: bool = True
    capture_setting_enabled: bool = True
    token_budget: int = 900
    min_prompt_chars: int = 32
    capture_min_chars: int = 160
    capture_max_chars: int = 8000


def handle_user_prompt_submit(payload: dict[str, Any]) -> dict[str, Any]:
    settings = load_policy_settings()
    prompt = str(payload.get("prompt") or "")
    skip_reason = _preflight_skip_reason(prompt, settings)
    common = _common_details(payload)
    if skip_reason:
        _audit("codex_hook.preflight_skipped", {**common, "reason": skip_reason, "prompt_chars": len(prompt.strip())})
        return {"continue": True}

    try:
        service = KnowledgeService()
        results = service.search(prompt, limit=5)
        if not _has_relevant_evidence(results):
            _audit(
                "codex_hook.preflight_skipped",
                {**common, "reason": "no_relevant_evidence", "result_count": len(results)},
            )
            return {"continue": True}

        brief = service.brief(prompt, token_budget=settings.token_budget).strip()
        if not brief:
            _audit("codex_hook.preflight_skipped", {**common, "reason": "empty_brief", "result_count": len(results)})
            return {"continue": True}

        _audit(
            "codex_hook.preflight_injected",
            {
                **common,
                "result_count": len(results),
                "token_budget": settings.token_budget,
                "brief_chars": len(brief),
            },
        )
        return _additional_context("UserPromptSubmit", f"Flux-LLM-KB relevant memory:\n{brief}")
    except Exception as exc:  # pragma: no cover - exact integration failures are environment-specific
        _audit("codex_hook.preflight_error", {**common, "error": str(exc), "error_type": type(exc).__name__})
        return {"continue": True, "systemMessage": "Flux-LLM-KB preflight failed; continuing without memory brief."}


def handle_stop(payload: dict[str, Any]) -> dict[str, Any]:
    settings = load_policy_settings()
    common = _common_details(payload)
    message = str(payload.get("last_assistant_message") or "").strip()
    skip_reason = _capture_skip_reason(message, settings, payload)
    if skip_reason:
        _audit("codex_hook.capture_skipped", {**common, "reason": skip_reason, "message_chars": len(message)})
        return {"continue": True}

    assert common["session_id"] is not None
    assert common["turn_id"] is not None
    try:
        if database.codex_hook_capture_exists(session_id=common["session_id"], turn_id=common["turn_id"]):
            _audit("codex_hook.capture_skipped", {**common, "reason": "duplicate_turn", "message_chars": len(message)})
            return {"continue": True}

        body, truncated = _truncate_capture(message, settings.capture_max_chars)
        result = KnowledgeService().remember(
            f"Codex turn {common['turn_id']}",
            body,
            metadata={
                "source": "codex_hook_stop",
                "session_id": common["session_id"],
                "turn_id": common["turn_id"],
                "cwd": common["cwd"],
                "model": common["model"],
                "truncated": truncated,
            },
        )
        _audit(
            "codex_hook.capture_saved",
            {**common, "message_chars": len(message), "captured_chars": len(body), "redaction_count": result.redaction_count},
            target_table="episodes",
            target_id=result.id,
        )
        return {"continue": True}
    except Exception as exc:  # pragma: no cover - exact integration failures are environment-specific
        _audit("codex_hook.capture_error", {**common, "error": str(exc), "error_type": type(exc).__name__})
        return {"continue": True, "systemMessage": "Flux-LLM-KB capture failed; continuing without storing this turn."}


def codex_hook_policy_status() -> dict[str, Any]:
    settings = load_policy_settings()
    if not settings.enabled:
        status = "disabled"
    elif settings.preflight_enabled or (settings.capture_enabled and settings.capture_setting_enabled):
        status = "active"
    else:
        status = "disabled"
    try:
        recent_events = database.recent_codex_hook_audit_events(limit=5)
    except Exception:
        recent_events = []
    return {
        "status": status,
        "enabled": settings.enabled,
        "preflight_enabled": settings.preflight_enabled,
        "capture_enabled": settings.capture_enabled and settings.capture_setting_enabled,
        "capture_setting_enabled": settings.capture_setting_enabled,
        "token_budget": settings.token_budget,
        "min_prompt_chars": settings.min_prompt_chars,
        "capture_min_chars": settings.capture_min_chars,
        "capture_max_chars": settings.capture_max_chars,
        "recent_events": recent_events,
    }


def load_policy_settings() -> CodexHookPolicySettings:
    settings = SettingsService()
    return CodexHookPolicySettings(
        enabled=_resolve_bool(settings, "codex.hooks.enabled", True),
        preflight_enabled=_resolve_bool(settings, "codex.hooks.preflight_enabled", True),
        capture_enabled=_resolve_bool(settings, "codex.hooks.capture_enabled", True),
        capture_setting_enabled=_resolve_bool(settings, "capture.enabled", True),
        token_budget=_resolve_int(settings, "codex.hooks.token_budget", 900),
        min_prompt_chars=_resolve_int(settings, "codex.hooks.min_prompt_chars", 32),
        capture_min_chars=_resolve_int(settings, "codex.hooks.capture_min_chars", 160),
        capture_max_chars=_resolve_int(settings, "codex.hooks.capture_max_chars", 8000),
    )


def _preflight_skip_reason(prompt: str, settings: CodexHookPolicySettings) -> str | None:
    stripped = prompt.strip()
    if not settings.enabled:
        return "hooks_disabled"
    if not settings.preflight_enabled:
        return "preflight_disabled"
    if not stripped:
        return "empty_prompt"
    if stripped.startswith("/"):
        return "slash_command"
    if len(stripped) < settings.min_prompt_chars:
        return "prompt_too_short"
    if _is_trivial_prompt(stripped):
        return "trivial_prompt"
    return None


def _capture_skip_reason(message: str, settings: CodexHookPolicySettings, payload: dict[str, Any]) -> str | None:
    if not settings.enabled:
        return "hooks_disabled"
    if not settings.capture_enabled or not settings.capture_setting_enabled:
        return "capture_disabled"
    if not payload.get("session_id") or not payload.get("turn_id"):
        return "missing_turn_identity"
    if not message:
        return "empty_message"
    if len(message) < settings.capture_min_chars:
        return "message_too_short"
    return None


def _is_trivial_prompt(prompt: str) -> bool:
    normalized = " ".join(prompt.lower().strip(" .!?\t\r\n").split())
    return normalized in {
        "hi",
        "hello",
        "hey",
        "thanks",
        "thank you",
        "ok",
        "okay",
        "continue",
        "go on",
        "proceed",
        "yes",
        "no",
        "yep",
        "nope",
    }


def _has_relevant_evidence(results: list[dict[str, Any]]) -> bool:
    for result in results:
        streams = {str(stream) for stream in result.get("streams", [])}
        if any("lexical" in stream or "fuzzy" in stream for stream in streams):
            return True
    return False


def _truncate_capture(message: str, max_chars: int) -> tuple[str, bool]:
    if len(message) <= max_chars:
        return message, False
    marker = "\n\n[truncated]"
    keep = max(0, max_chars - len(marker))
    return f"{message[:keep].rstrip()}{marker}", True


def _common_details(payload: dict[str, Any]) -> dict[str, Any]:
    return {
        "session_id": _optional_str(payload.get("session_id")),
        "turn_id": _optional_str(payload.get("turn_id")),
        "cwd": _optional_str(payload.get("cwd")),
        "model": _optional_str(payload.get("model")),
    }


def _additional_context(event_name: str, text: str) -> dict[str, Any]:
    return {"hookSpecificOutput": {"hookEventName": event_name, "additionalContext": text}}


def _audit(
    event_type: str,
    details: dict[str, Any],
    *,
    target_table: str | None = None,
    target_id: str | None = None,
) -> None:
    try:
        database.record_audit_event(
            event_type=event_type,
            target_table=target_table,
            target_id=target_id,
            details=details,
        )
    except Exception:
        return


def _resolve_bool(settings: SettingsService, key: str, fallback: bool) -> bool:
    try:
        return bool(settings.resolve(key).raw_value)
    except Exception:
        return fallback


def _resolve_int(settings: SettingsService, key: str, fallback: int) -> int:
    try:
        return int(settings.resolve(key).raw_value)
    except Exception:
        return fallback


def _optional_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
