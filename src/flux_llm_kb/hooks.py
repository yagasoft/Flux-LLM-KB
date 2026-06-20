from __future__ import annotations

import json
from typing import Any


def run_hook(event: str, stdin_text: str) -> dict[str, Any]:
    payload = _parse_json(stdin_text)
    if event == "user-prompt-submit":
        prompt = payload.get("prompt", "")
        return _additional_context(
            "UserPromptSubmit",
            f"Flux-LLM-KB preflight should retrieve a compact brief for: {prompt[:240]}",
        )
    if event == "pre-compact":
        return _additional_context("PreCompact", "Flux-LLM-KB capture should run before compaction.")
    if event == "stop":
        return {"continue": True}
    return {"continue": True}


def _additional_context(event_name: str, text: str) -> dict[str, Any]:
    return {
        "hookSpecificOutput": {
            "hookEventName": event_name,
            "additionalContext": text,
        }
    }


def _parse_json(stdin_text: str) -> dict[str, Any]:
    if not stdin_text.strip():
        return {}
    try:
        value = json.loads(stdin_text)
    except json.JSONDecodeError:
        return {}
    return value if isinstance(value, dict) else {}

