from __future__ import annotations

import json
from typing import Any

from .hook_policy import handle_stop, handle_user_prompt_submit


def run_hook(event: str, stdin_text: str) -> dict[str, Any]:
    payload = _parse_json(stdin_text)
    if event == "user-prompt-submit":
        return handle_user_prompt_submit(payload)
    if event == "pre-compact":
        return {"continue": True}
    if event == "stop":
        return handle_stop(payload)
    return {"continue": True}


def _parse_json(stdin_text: str) -> dict[str, Any]:
    if not stdin_text.strip():
        return {}
    try:
        value = json.loads(stdin_text)
    except json.JSONDecodeError:
        return {}
    return value if isinstance(value, dict) else {}
