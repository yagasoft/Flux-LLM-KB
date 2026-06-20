from flux_llm_kb.hooks import run_hook


def test_user_prompt_hook_adds_compact_retrieval_instruction():
    output = run_hook("user-prompt-submit", '{"prompt": "repeat the RFP analysis"}')

    hook_output = output["hookSpecificOutput"]
    assert hook_output["hookEventName"] == "UserPromptSubmit"
    assert "RFP analysis" in hook_output["additionalContext"]


def test_stop_hook_allows_codex_to_continue():
    assert run_hook("stop", "{}") == {"continue": True}
