import json

from flux_llm_kb import hook_policy
from flux_llm_kb.hooks import run_hook


DEFAULT_SETTINGS = {
    "capture.enabled": True,
    "codex.hooks.enabled": True,
    "codex.hooks.preflight_enabled": True,
    "codex.hooks.capture_enabled": True,
    "codex.hooks.token_budget": 900,
    "codex.hooks.min_prompt_chars": 32,
    "codex.hooks.capture_min_chars": 160,
    "codex.hooks.capture_max_chars": 8000,
}


class FakeSetting:
    def __init__(self, raw_value):
        self.raw_value = raw_value


def install_settings(monkeypatch, overrides=None):
    values = {**DEFAULT_SETTINGS, **(overrides or {})}

    class FakeSettingsService:
        def resolve(self, key):
            return FakeSetting(values[key])

    monkeypatch.setattr(hook_policy, "SettingsService", FakeSettingsService)


def test_user_prompt_hook_skips_short_prompts(monkeypatch):
    install_settings(monkeypatch)
    audits = []

    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook("user-prompt-submit", '{"prompt": "repeat the RFP analysis"}')

    assert output == {"continue": True}
    assert audits[-1]["event_type"] == "codex_hook.preflight_skipped"
    assert audits[-1]["details"]["reason"] == "prompt_too_short"


def test_user_prompt_hook_injects_real_brief_for_relevant_non_trivial_prompt(monkeypatch):
    install_settings(monkeypatch)
    observed = {}
    audits = []

    class FakeService:
        def search(self, query, limit=5):
            observed["search"] = {"query": query, "limit": limit}
            return [
                {
                    "title": "Prior RFP plan",
                    "summary": "Use the established RFP workflow.",
                    "score": 0.42,
                    "streams": ["lexical"],
                }
            ]

        def brief(self, query, token_budget=None):
            observed["brief"] = {"query": query, "token_budget": token_budget}
            return "### Prior RFP plan\nUse the established RFP workflow."

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "user-prompt-submit",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-1",
                "cwd": "E:/Repo",
                "model": "gpt-5",
                "prompt": "Please continue the customer RFP analysis using the prior project context.",
            }
        ),
    )

    context = output["hookSpecificOutput"]["additionalContext"]
    assert output["hookSpecificOutput"]["hookEventName"] == "UserPromptSubmit"
    assert "Flux-LLM-KB relevant memory" in context
    assert "Use the established RFP workflow." in context
    assert observed["search"]["limit"] == 5
    assert observed["brief"]["token_budget"] == 900
    assert audits[-1]["event_type"] == "codex_hook.preflight_injected"
    assert audits[-1]["details"]["session_id"] == "session-1"
    assert audits[-1]["details"]["turn_id"] == "turn-1"


def test_user_prompt_hook_skips_trivial_and_slash_prompts_without_search(monkeypatch):
    install_settings(monkeypatch)
    audits = []

    class FailingService:
        def search(self, *_args, **_kwargs):
            raise AssertionError("trivial prompts should not search")

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FailingService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook("user-prompt-submit", '{"prompt": "/help", "session_id": "session-1", "turn_id": "turn-1"}')

    assert output == {"continue": True}
    assert audits[-1]["event_type"] == "codex_hook.preflight_skipped"
    assert audits[-1]["details"]["reason"] == "slash_command"


def test_user_prompt_hook_warns_and_continues_when_preflight_fails(monkeypatch):
    install_settings(monkeypatch)
    audits = []

    class FailingService:
        def search(self, *_args, **_kwargs):
            raise RuntimeError("database unavailable")

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FailingService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "user-prompt-submit",
        '{"prompt": "Please use the stored architecture context for this implementation task.", "session_id": "session-1", "turn_id": "turn-2"}',
    )

    assert output["continue"] is True
    assert "Flux-LLM-KB preflight failed" in output["systemMessage"]
    assert audits[-1]["event_type"] == "codex_hook.preflight_error"
    assert "database unavailable" in audits[-1]["details"]["error"]


def test_stop_hook_captures_final_assistant_message_once(monkeypatch):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20, "codex.hooks.capture_max_chars": 80})
    audits = []
    captured = {}

    class FakeService:
        def remember(self, title, body, metadata=None):
            captured["title"] = title
            captured["body"] = body
            captured["metadata"] = metadata
            return type("Result", (), {"id": "episode-1", "redaction_count": 0})()

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: False)
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "stop",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-3",
                "cwd": "E:/Repo",
                "model": "gpt-5",
                "last_assistant_message": (
                    "Implemented the hook policy and verified the focused checks. "
                    "This longer sentence should be truncated before persistence."
                ),
            }
        ),
    )

    assert output == {"continue": True}
    assert captured["title"] == "Codex turn turn-3"
    assert captured["body"].endswith("[truncated]")
    assert len(captured["body"]) <= 94
    assert captured["metadata"] == {
        "source": "codex_hook_stop",
        "session_id": "session-1",
        "turn_id": "turn-3",
        "cwd": "E:/Repo",
        "model": "gpt-5",
        "truncated": True,
    }
    assert audits[-1]["event_type"] == "codex_hook.capture_saved"
    assert audits[-1]["target_id"] == "episode-1"


def test_stop_hook_skips_duplicate_capture(monkeypatch):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20})
    audits = []

    class FailingService:
        def remember(self, *_args, **_kwargs):
            raise AssertionError("duplicate turn should not be captured again")

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FailingService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: True)
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "stop",
        '{"session_id": "session-1", "turn_id": "turn-3", "last_assistant_message": "This final response is long enough to capture."}',
    )

    assert output == {"continue": True}
    assert audits[-1]["event_type"] == "codex_hook.capture_skipped"
    assert audits[-1]["details"]["reason"] == "duplicate_turn"


def test_stop_hook_skips_capture_when_disabled(monkeypatch):
    install_settings(monkeypatch, {"codex.hooks.capture_enabled": False})
    audits = []

    class FailingService:
        def remember(self, *_args, **_kwargs):
            raise AssertionError("disabled capture should not persist")

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FailingService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "stop",
        '{"session_id": "session-1", "turn_id": "turn-4", "last_assistant_message": "This final response is long enough to capture."}',
    )

    assert output == {"continue": True}
    assert audits[-1]["event_type"] == "codex_hook.capture_skipped"
    assert audits[-1]["details"]["reason"] == "capture_disabled"


def test_pre_compact_hook_remains_non_blocking():
    assert run_hook("pre-compact", '{"trigger": "manual"}') == {"continue": True}


def test_stop_hook_allows_codex_to_continue():
    assert run_hook("stop", "{}") == {"continue": True}
