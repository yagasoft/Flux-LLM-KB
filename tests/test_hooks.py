import json

from flux_llm_kb import hook_policy
from flux_llm_kb.hooks import run_hook


DEFAULT_SETTINGS = {
    "capture.enabled": True,
    "codex.hooks.enabled": True,
    "codex.hooks.preflight_enabled": True,
    "codex.hooks.capture_enabled": True,
    "codex.hooks.capture_guidance_enabled": True,
    "codex.hooks.reference_indexing_enabled": True,
    "codex.hooks.reference_max_count": 5,
    "codex.hooks.reference_max_bytes": 1048576,
    "codex.hooks.reference_fetch_timeout_seconds": 3,
    "codex.hooks.reference_allow_private_urls": False,
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
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first"):
            observed["search"] = {
                "query": query,
                "limit": limit,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
            return [
                {
                    "title": "Prior RFP plan",
                    "summary": "Use the established RFP workflow.",
                    "score": 0.42,
                    "streams": ["lexical"],
                    "retrieval_scope": "local",
                }
            ]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first"):
            observed["brief"] = {
                "query": query,
                "token_budget": token_budget,
                "cwd": cwd,
                "root_name": root_name,
                "scope_mode": scope_mode,
            }
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
    assert observed["search"]["cwd"] == "E:/Repo"
    assert observed["search"]["scope_mode"] == "local_first"
    assert observed["brief"]["token_budget"] == 900
    assert observed["brief"]["cwd"] == "E:/Repo"
    assert observed["brief"]["scope_mode"] == "local_first"
    assert audits[-1]["event_type"] == "codex_hook.preflight_injected"
    assert audits[-1]["details"]["session_id"] == "session-1"
    assert audits[-1]["details"]["turn_id"] == "turn-1"
    assert audits[-1]["details"]["retrieval_scope"] == "local"
    assert audits[-1]["details"]["scope_mode"] == "local_first"


def test_user_prompt_hook_reruns_non_code_filters_for_code_heavy_prompt(monkeypatch):
    install_settings(monkeypatch)
    calls = []
    audits = []

    class FakeService:
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls.append(("search", filters))
            if filters and filters.get("file_kinds") == ["text", "document", "image"]:
                return [
                    {
                        "title": "AGENTS.md",
                        "summary": "If closeout fails, report failed_step and log_path.",
                        "score": 0.91,
                        "file_kind": "text",
                        "streams": ["corpus_lexical"],
                        "retrieval_scope": "local",
                    }
                ]
            return [
                {
                    "title": "src/flux_llm_kb/hooks.py::failed_step",
                    "summary": "failed_step = result.failed_step",
                    "score": 0.22,
                    "file_kind": "code",
                    "streams": ["code_symbol_exact"],
                    "retrieval_scope": "local",
                },
                {
                    "title": "src/flux_llm_kb/hooks.py::log_path",
                    "summary": "log_path = result.log_path",
                    "score": 0.2,
                    "file_kind": "code",
                    "streams": ["code_symbol_exact"],
                    "retrieval_scope": "local",
                },
            ]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first", filters=None):
            calls.append(("brief", filters))
            if filters and filters.get("file_kinds") == ["text", "document", "image"]:
                return "### AGENTS.md\nIf closeout fails, report failed_step and log_path."
            return "### hooks.py\nfailed_step = result.failed_step"

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "user-prompt-submit",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-2",
                "cwd": "E:/Repo",
                "model": "gpt-5",
                "prompt": "If complete-feature.ps1 fails, report the JSON failed_step and log_path from AGENTS.md.",
            }
        ),
    )

    context = output["hookSpecificOutput"]["additionalContext"]
    expected_filters = {
        "logical_kinds": ["file"],
        "file_kinds": ["text", "document", "image"],
    }
    assert ("search", expected_filters) in calls
    assert ("brief", expected_filters) in calls
    assert "AGENTS.md" in context
    assert "hooks.py" not in context
    assert audits[-1]["event_type"] == "codex_hook.preflight_injected"
    assert audits[-1]["details"]["fallback_reason"] == "code_dominated_non_code_prompt"
    assert "prompt" not in audits[-1]["details"]


def test_user_prompt_hook_labels_global_fallback_memory(monkeypatch):
    install_settings(monkeypatch)
    audits = []

    class FakeService:
        def search(self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first"):
            return [
                {
                    "title": "Prior global plan",
                    "summary": "Use fallback workflow.",
                    "score": 0.42,
                    "streams": ["lexical"],
                    "retrieval_scope": "global_fallback",
                }
            ]

        def brief(self, query, token_budget=None, cwd=None, root_name=None, scope_mode="local_first"):
            return "### Prior global plan\nUse fallback workflow."

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "user-prompt-submit",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-fallback",
                "cwd": "E:/Repo",
                "prompt": "Please continue the customer RFP analysis using the prior project context.",
            }
        ),
    )

    context = output["hookSpecificOutput"]["additionalContext"]
    assert "Flux-LLM-KB global fallback memory" in context
    assert "Use fallback workflow." in context
    assert audits[-1]["details"]["retrieval_scope"] == "global_fallback"


def test_user_prompt_hook_injects_indexable_capture_guidance_without_memory_evidence(monkeypatch):
    install_settings(monkeypatch)
    audits = []

    class FakeService:
        def search(self, query, limit=5, **_kwargs):
            return []

        def brief(self, *_args, **_kwargs):
            raise AssertionError("brief should not run without relevant evidence")

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "user-prompt-submit",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-guidance",
                "prompt": "Please implement the next Flux roadmap item with tests and deployment.",
            }
        ),
    )

    context = output["hookSpecificOutput"]["additionalContext"]
    assert output["hookSpecificOutput"]["hookEventName"] == "UserPromptSubmit"
    assert "Make the final assistant message indexable for Flux-LLM-KB" in context
    assert "files changed or referenced" in context
    assert "commands or tests run" in context
    assert "Do not include secrets" in context
    assert audits[-1]["event_type"] == "codex_hook.preflight_guidance"
    assert audits[-1]["details"]["reason"] == "no_relevant_evidence"


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


def test_stop_hook_indexes_public_web_references_from_final_message(monkeypatch):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20})
    audits = []
    remembered = []

    class FakeService:
        def remember(self, title, body, metadata=None):
            remembered.append({"title": title, "body": body, "metadata": metadata})
            return type("Result", (), {"id": f"episode-{len(remembered)}", "redaction_count": 0})()

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: False)
    monkeypatch.setattr(hook_policy.database, "codex_hook_reference_exists", lambda **_kwargs: False, raising=False)
    monkeypatch.setattr(
        hook_policy,
        "_fetch_web_reference",
        lambda url, settings: {"title": "Codex MCP docs", "text": "Codex MCP tools are configured in config.toml."},
        raising=False,
    )
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    output = run_hook(
        "stop",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-web",
                "last_assistant_message": (
                    "Implemented MCP configuration. Reference: "
                    "https://developers.openai.com/codex/mcp for the Codex MCP config contract."
                ),
            }
        ),
    )

    assert output == {"continue": True}
    assert len(remembered) == 2
    assert remembered[1]["title"] == "Referenced web page: Codex MCP docs"
    assert remembered[1]["body"] == "Codex MCP tools are configured in config.toml."
    assert remembered[1]["metadata"]["source"] == "codex_hook_reference"
    assert remembered[1]["metadata"]["reference_type"] == "web"
    assert remembered[1]["metadata"]["url"] == "https://developers.openai.com/codex/mcp"
    assert remembered[1]["metadata"]["parent_episode_id"] == "episode-1"
    assert any(item["event_type"] == "codex_hook.reference_indexed" for item in audits)
    assert audits[-1]["event_type"] == "codex_hook.capture_saved"
    assert audits[-1]["details"]["references_indexed"] == 1


def test_stop_hook_rejects_private_web_references_by_default(monkeypatch):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20})
    audits = []

    class FakeService:
        def remember(self, title, body, metadata=None):
            return type("Result", (), {"id": "episode-1", "redaction_count": 0})()

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: False)
    monkeypatch.setattr(hook_policy.database, "codex_hook_reference_exists", lambda **_kwargs: False, raising=False)
    monkeypatch.setattr(
        hook_policy,
        "_fetch_web_reference",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(AssertionError("private URL should not be fetched")),
        raising=False,
    )
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    run_hook(
        "stop",
        '{"session_id":"session-1","turn_id":"turn-private","last_assistant_message":"Checked local status at http://127.0.0.1:8765/api/dashboard/health after deployment."}',
    )

    skipped = [item for item in audits if item["event_type"] == "codex_hook.reference_skipped"]
    assert skipped
    assert skipped[-1]["details"]["reason"] == "private_url"
    assert skipped[-1]["details"]["reference"] == "http://127.0.0.1:8765/api/dashboard/health"


def test_stop_hook_syncs_file_references_under_monitored_roots(monkeypatch, tmp_path):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20})
    docs_root = tmp_path / "docs"
    docs_root.mkdir()
    referenced = docs_root / "implementation.md"
    referenced.write_text("implementation notes", encoding="utf-8")
    audits = []
    synced = []

    class FakeService:
        def remember(self, title, body, metadata=None):
            return type("Result", (), {"id": "episode-1", "redaction_count": 0})()

        def sync_corpus(self, **kwargs):
            synced.append(kwargs)
            return {"files_seen": 1, "chunks_indexed": 1, "jobs_queued": 0}

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: False)
    monkeypatch.setattr(hook_policy.database, "codex_hook_reference_exists", lambda **_kwargs: False, raising=False)
    monkeypatch.setattr(
        hook_policy.database,
        "list_monitored_roots",
        lambda: [{"name": "docs", "root_path": str(docs_root), "enabled": True}],
    )
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    run_hook(
        "stop",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-file",
                "cwd": str(tmp_path),
                "last_assistant_message": "Updated docs/implementation.md and verified it indexed correctly.",
            }
        ),
    )

    assert synced == [{"path": str(referenced), "reason": "codex_hook_reference"}]
    indexed = [item for item in audits if item["event_type"] == "codex_hook.reference_indexed"]
    assert indexed[-1]["details"]["reference_type"] == "file"
    assert indexed[-1]["details"]["root_name"] == "docs"


def test_stop_hook_rejects_file_references_outside_monitored_roots(monkeypatch, tmp_path):
    install_settings(monkeypatch, {"codex.hooks.capture_min_chars": 20})
    outside = tmp_path / "outside.txt"
    outside.write_text("outside", encoding="utf-8")
    audits = []
    synced = []

    class FakeService:
        def remember(self, title, body, metadata=None):
            return type("Result", (), {"id": "episode-1", "redaction_count": 0})()

        def sync_corpus(self, **kwargs):
            synced.append(kwargs)
            return {}

    monkeypatch.setattr(hook_policy, "KnowledgeService", lambda: FakeService())
    monkeypatch.setattr(hook_policy.database, "codex_hook_capture_exists", lambda **_kwargs: False)
    monkeypatch.setattr(hook_policy.database, "codex_hook_reference_exists", lambda **_kwargs: False, raising=False)
    monkeypatch.setattr(hook_policy.database, "list_monitored_roots", lambda: [])
    monkeypatch.setattr(hook_policy.database, "record_audit_event", lambda **kwargs: audits.append(kwargs))

    run_hook(
        "stop",
        json.dumps(
            {
                "session_id": "session-1",
                "turn_id": "turn-outside",
                "cwd": str(tmp_path),
                "last_assistant_message": f"Reviewed {outside} after implementation.",
            }
        ),
    )

    assert synced == []
    skipped = [item for item in audits if item["event_type"] == "codex_hook.reference_skipped"]
    assert skipped[-1]["details"]["reason"] == "file_not_under_monitored_root"


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
