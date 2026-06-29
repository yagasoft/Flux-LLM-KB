from flux_llm_kb import database
from flux_llm_kb.service import KnowledgeService


def _episode(item_id, title, streams, score=0.5, summary=None):
    return {
        "id": item_id,
        "title": title,
        "summary": summary or title,
        "score": score,
        "streams": streams,
    }


def _episode_with_metadata(item_id, title, streams, score=0.5, metadata=None, summary=None):
    item = _episode(item_id, title, streams, score=score, summary=summary)
    item["metadata"] = metadata or {}
    return item


def _chunk(item_id, title, streams, score=0.5, source_path="note.md", summary=None):
    return {
        "id": item_id,
        "asset_id": f"asset-{item_id}",
        "title": title,
        "summary": summary or title,
        "score": score,
        "streams": streams,
        "raw_scores": {},
        "source_path": source_path,
        "duplicate_count": 0,
        "trust_rank": 500,
    }


def test_remember_enriches_workspace_metadata_for_unmonitored_git_workspace(monkeypatch):
    captured = {}

    def fake_insert_episode(**kwargs):
        captured.update(kwargs)
        return "episode-1"

    monkeypatch.setattr(database, "list_monitored_roots", lambda: [])
    monkeypatch.setattr(database, "insert_episode", fake_insert_episode)
    monkeypatch.setattr("flux_llm_kb.service._git_repo_root", lambda _cwd: "E:\\LLM KB")

    result = KnowledgeService().remember(
        "Scoped finalization",
        "Durable memory for the workspace.",
        cwd="E:\\LLM KB\\src",
    )

    assert result.id == "episode-1"
    assert captured["metadata"]["cwd"] == "E:\\LLM KB\\src"
    assert captured["metadata"]["workspace_root"] == "E:\\LLM KB"
    assert captured["metadata"]["workspace_key"] == "path:e:/llm kb"


def test_local_first_uses_scoped_results_when_they_have_lexical_evidence(monkeypatch):
    calls = []

    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        calls.append(("episodes", cwd, root_path))
        if root_path:
            return [_episode("local-episode", "Local Flux plan", ["lexical"], score=0.8)]
        return [_episode("global-episode", "Unrelated global memory", ["lexical"], score=0.99)]

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, url=None):
        calls.append(("corpus", root_name))
        if root_name == "flux":
            return [_chunk("local-chunk", "Local corpus note", ["corpus_lexical"], score=0.7)]
        return [_chunk("global-chunk", "Global corpus note", ["corpus_lexical"], score=0.95)]

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    results = KnowledgeService().search(
        "workspace scoped brief",
        limit=5,
        cwd="E:\\LLM KB\\src",
        scope_mode="local_first",
    )

    assert {item["id"] for item in results} == {"local-episode", "local-chunk"}
    assert all(item["retrieval_scope"] == "local" for item in results)
    assert ("episodes", "E:\\LLM KB\\src", "E:\\LLM KB") in calls
    assert ("corpus", "flux") in calls


def test_local_first_falls_back_to_global_when_scoped_results_have_no_lexical_or_fuzzy_evidence(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        if root_path:
            return [_episode("local-vector", "Local semantic-only memory", ["vector"], score=0.8)]
        return [_episode("global-episode", "Global fallback memory", ["lexical"], score=0.5)]

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, url=None):
        if root_name == "flux":
            return [_chunk("local-trust", "Local trust-only chunk", ["corpus_trust"], score=0.8)]
        return [_chunk("global-chunk", "Global fallback chunk", ["corpus_fuzzy"], score=0.4)]

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    results = KnowledgeService().search(
        "workspace scoped brief",
        limit=5,
        cwd="E:\\LLM KB",
        scope_mode="local_first",
    )

    assert {item["id"] for item in results} == {"global-episode", "global-chunk"}
    assert all(item["retrieval_scope"] == "global_fallback" for item in results)


def test_local_only_never_returns_global_fallback(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )
    monkeypatch.setattr(database, "search_episodes", lambda *args, **kwargs: [])
    monkeypatch.setattr(database, "search_corpus_chunks", lambda *args, **kwargs: [])

    results = KnowledgeService().search(
        "workspace scoped brief",
        cwd="E:\\LLM KB",
        scope_mode="local_only",
    )

    assert results == []


def test_root_name_scope_searches_root_workspace_episode(monkeypatch):
    calls = []

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        calls.append({"cwd": cwd, "root_path": root_path, "workspace_key": workspace_key})
        if workspace_key == "root:docs":
            return [_episode("root-episode", "Root-scoped memory", ["lexical"], score=0.9)]
        return []

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, url=None):
        if root_name == "docs":
            return [_chunk("root-corpus", "Root corpus note", ["corpus_lexical"], score=0.4)]
        return []

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    results = KnowledgeService().search(
        "root scoped memory",
        root_name="docs",
        scope_mode="local_only",
    )

    assert [item["id"] for item in results] == ["root-episode", "root-corpus"]
    assert all(item["retrieval_scope"] == "local" for item in results)
    assert calls == [{"cwd": None, "root_path": None, "workspace_key": "root:docs"}]


def test_episode_logical_kind_filter_prefilters_local_raw_search(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None: [
            _episode("root-episode", "Root-scoped memory", ["lexical"], score=0.9)
        ],
    )

    def fail_corpus_search(*_args, **_kwargs):
        raise AssertionError("episode-only searches should not query corpus chunks")

    monkeypatch.setattr(database, "search_corpus_chunks", fail_corpus_search)

    results = KnowledgeService().search(
        "root scoped memory",
        root_name="docs",
        scope_mode="local_only",
        filters={"logical_kinds": ["episode"]},
    )

    assert [item["id"] for item in results] == ["root-episode"]


def test_explain_passes_filters_into_raw_search_before_trace_filtering(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None: [
            _episode("root-episode", "Root-scoped memory", ["lexical"], score=0.9)
        ],
    )

    def fail_corpus_search(*_args, **_kwargs):
        raise AssertionError("explain should pass logical kind filters into raw search")

    monkeypatch.setattr(database, "search_corpus_chunks", fail_corpus_search)

    payload = KnowledgeService().explain(
        "root scoped memory",
        root_name="docs",
        scope_mode="local_only",
        filters={"logical_kinds": ["episode"]},
    )

    assert [item["id"] for item in payload["results"]] == ["root-episode"]


def test_workspace_boosted_blends_local_and_strong_cross_workspace_results(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        if root_path:
            return [_episode("local-episode", "Local scoped decision", ["lexical"], score=0.6)]
        return [
            _episode_with_metadata(
                "global-episode",
                "Cross workspace previous fix",
                ["fuzzy"],
                score=0.9,
                metadata={"workspace_key": "path:e:/other"},
            )
        ]

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, url=None):
        if root_name == "flux":
            return [_chunk("local-chunk", "Local corpus note", ["corpus_lexical"], score=0.7)]
        global_chunk = _chunk("global-chunk", "General indexed PC document", ["corpus_vector"], score=0.65)
        global_chunk["root_name"] = "docs"
        return [
            _chunk("local-chunk", "Duplicate local from global search", ["corpus_lexical"], score=0.99),
            global_chunk,
            _chunk("weak-trust", "Broad trust-only document", ["corpus_trust"], score=0.99),
        ]

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    results = KnowledgeService().search(
        "expanded mid-turn search",
        limit=5,
        cwd="E:\\LLM KB\\src",
        scope_mode="workspace_boosted",
    )

    ids = [item["id"] for item in results]
    scopes = {item["id"]: item["retrieval_scope"] for item in results}
    assert {"local-episode", "local-chunk", "global-episode", "global-chunk"}.issubset(ids)
    assert "weak-trust" not in ids
    assert ids.count("local-chunk") == 1
    assert scopes["local-episode"] == "local"
    assert scopes["local-chunk"] == "local"
    assert scopes["global-episode"] == "cross_workspace"
    assert scopes["global-chunk"] == "cross_workspace"


def test_workspace_boosted_labels_unscoped_global_episode_without_cross_workspace_claim(monkeypatch):
    monkeypatch.setattr(database, "list_monitored_roots", lambda: [])

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        if workspace_key:
            return [_episode_with_metadata("local-episode", "Local scoped decision", ["lexical"], score=0.6)]
        return [
            _episode_with_metadata(
                "unscoped",
                "Legacy unscoped memory",
                ["fuzzy"],
                score=0.9,
                metadata={},
            ),
            _episode_with_metadata(
                "other-workspace",
                "Other workspace memory",
                ["fuzzy"],
                score=0.8,
                metadata={"workspace_key": "path:e:/other"},
            ),
        ]

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", lambda *args, **kwargs: [])
    monkeypatch.setattr("flux_llm_kb.service._git_repo_root", lambda _cwd: "E:\\LLM KB")

    results = KnowledgeService().search(
        "expanded mid-turn search",
        limit=5,
        cwd="E:\\LLM KB\\src",
        scope_mode="workspace_boosted",
    )
    scopes = {item["id"]: item["retrieval_scope"] for item in results}

    assert scopes["local-episode"] == "local"
    assert scopes["unscoped"] == "unscoped_global"
    assert scopes["other-workspace"] == "cross_workspace"


def test_workspace_boosted_caps_cross_workspace_results_when_local_evidence_exists(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None: [
            _episode("local-episode", "Local lexical match", ["lexical"], score=0.6)
        ]
        if root_path
        else [
            _episode(f"global-{index}", f"Strong global match {index}", ["fuzzy"], score=0.95 - index / 100)
            for index in range(4)
        ],
    )
    monkeypatch.setattr(database, "search_corpus_chunks", lambda *args, **kwargs: [])

    results = KnowledgeService().search(
        "expanded mid-turn search",
        limit=4,
        cwd="E:\\LLM KB",
        scope_mode="workspace_boosted",
    )

    assert any(item["retrieval_scope"] == "local" for item in results)
    assert sum(item["retrieval_scope"] == "cross_workspace" for item in results) <= 2


def test_global_scope_preserves_broad_results(monkeypatch):
    monkeypatch.setattr(database, "list_monitored_roots", lambda: [])
    monkeypatch.setattr(database, "search_episodes", lambda *args, **kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda *args, **kwargs: [_chunk("trust-only", "Trust ranked global note", ["corpus_trust"], score=0.9)],
    )

    results = KnowledgeService().search("broad search", scope_mode="global")

    assert [item["id"] for item in results] == ["trust-only"]
    assert results[0]["retrieval_scope"] == "global"


def test_cwd_only_scope_does_not_mix_global_corpus_into_local_results(monkeypatch):
    monkeypatch.setattr(database, "list_monitored_roots", lambda: [])

    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None: [
            _episode("local-episode", f"Scoped to {workspace_key}", ["lexical"], score=0.8)
        ]
        if workspace_key
        else [_episode("global-episode", "Global episode", ["lexical"], score=0.9)],
    )
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, *, limit=5, root_name=None, url=None: [
            _chunk("global-corpus", "Global corpus", ["corpus_lexical"], score=0.99)
        ],
    )

    results = KnowledgeService().search(
        "workspace scoped brief",
        cwd="E:\\Unwatched Repo",
        scope_mode="local_first",
    )

    assert [item["id"] for item in results] == ["local-episode"]


def test_unmonitored_git_workspace_uses_workspace_key_for_local_episode_search(monkeypatch):
    calls = []
    monkeypatch.setattr(database, "list_monitored_roots", lambda: [])
    monkeypatch.setattr("flux_llm_kb.service._git_repo_root", lambda _cwd: "E:\\LLM KB")

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, workspace_key=None, url=None):
        calls.append({"cwd": cwd, "root_path": root_path, "workspace_key": workspace_key})
        if workspace_key == "path:e:/llm kb":
            return [_episode("scoped-finalize", "Scoped finalize memory", ["lexical"], score=0.8)]
        return [_episode("global", "Global memory", ["lexical"], score=0.9)]

    monkeypatch.setattr(database, "search_episodes", fake_search_episodes)
    monkeypatch.setattr(database, "search_corpus_chunks", lambda *args, **kwargs: [])

    results = KnowledgeService().search(
        "workspace scoped brief",
        cwd="E:\\LLM KB\\src",
        scope_mode="local_only",
    )

    assert [item["id"] for item in results] == ["scoped-finalize"]
    assert results[0]["retrieval_scope"] == "local"
    assert calls[0]["workspace_key"] == "path:e:/llm kb"


def test_brief_keeps_current_evidence_filtering_with_scoped_search(monkeypatch):
    monkeypatch.setattr(
        KnowledgeService,
        "search",
        lambda self, query, limit=5, cwd=None, root_name=None, scope_mode="local_first": [
            {
                "id": "retired",
                "title": "Retired",
                "summary": "old context",
                "score": 0.99,
                "lifecycle": {"current": False, "state": "retired"},
            },
            {
                "id": "current",
                "title": "Current",
                "summary": "current context",
                "score": 0.5,
            },
        ],
    )

    brief = KnowledgeService().brief(
        "workspace scoped brief",
        token_budget=100,
        cwd="E:\\LLM KB",
        scope_mode="local_first",
    )

    assert "Current" in brief
    assert "Retired" not in brief


def test_explain_includes_additive_corpus_retrieval_timings(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )
    monkeypatch.setattr(database, "search_episodes", lambda *_args, **_kwargs: [])

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, filters=None, diagnostics=None, url=None):
        if diagnostics is not None:
            diagnostics.setdefault("streams", {})["corpus_vector"] = {
                "duration_ms": 2.5,
                "rows": 1,
                "plan": "root_scoped_hnsw_candidates",
            }
        return [_chunk("local-chunk", "Local corpus note", ["corpus_vector"], score=0.7)]

    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    payload = KnowledgeService().explain("local corpus", cwd="E:\\LLM KB", scope_mode="local_only")

    assert payload["results"][0]["id"] == "local-chunk"
    assert payload["retrieval_timing"]["scopes"]["local"]["corpus"]["streams"]["corpus_vector"] == {
        "duration_ms": 2.5,
        "rows": 1,
        "plan": "root_scoped_hnsw_candidates",
    }


def test_search_corpus_chunks_accepts_root_name_filter_in_all_streams():
    source = open(database.__file__, encoding="utf-8").read()
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "root_name: str | None = None" in function
    assert "r.name = %s" in function
    assert "r.name AS root_name" in source
    assert "root_name_params" in function


def test_search_episodes_accepts_cwd_and_root_path_filter():
    source = open(database.__file__, encoding="utf-8").read()
    function = source.split("def search_episodes", 1)[1].split("\ndef ", 1)[0]

    assert "cwd: str | None = None" in function
    assert "root_path: str | None = None" in function
    assert "workspace_key: str | None = None" in function
    assert "metadata->>'workspace_key'" in function
    assert "metadata->>'cwd'" in function
    assert "_episode_scope_sql" in function


def test_path_scope_sql_escapes_windows_backslashes_for_like_prefixes():
    _sql, params = database._path_scope_sql("metadata->>'cwd'", root_path="E:\\LLM KB")

    assert "E:\\LLM KB" in params
    assert "E:\\\\LLM KB\\\\%" in params
    assert "E:/LLM KB/%" in params
