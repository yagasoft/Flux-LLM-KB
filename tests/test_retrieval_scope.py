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


def test_local_first_uses_scoped_results_when_they_have_lexical_evidence(monkeypatch):
    calls = []

    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, url=None):
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

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, url=None):
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


def test_workspace_boosted_blends_local_and_strong_cross_workspace_results(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    def fake_search_episodes(query, *, limit=5, cwd=None, root_path=None, url=None):
        if root_path:
            return [_episode("local-episode", "Local scoped decision", ["lexical"], score=0.6)]
        return [_episode("global-episode", "Cross workspace previous fix", ["fuzzy"], score=0.9)]

    def fake_search_corpus_chunks(query, *, limit=5, root_name=None, url=None):
        if root_name == "flux":
            return [_chunk("local-chunk", "Local corpus note", ["corpus_lexical"], score=0.7)]
        return [
            _chunk("local-chunk", "Duplicate local from global search", ["corpus_lexical"], score=0.99),
            _chunk("global-chunk", "General indexed PC document", ["corpus_vector"], score=0.65),
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


def test_workspace_boosted_caps_cross_workspace_results_when_local_evidence_exists(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_monitored_roots",
        lambda: [{"name": "flux", "root_path": "E:\\LLM KB", "enabled": True}],
    )

    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, *, limit=5, cwd=None, root_path=None, url=None: [
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
        lambda query, *, limit=5, cwd=None, root_path=None, url=None: [
            _episode("local-episode", f"Scoped to {cwd}", ["lexical"], score=0.8)
        ]
        if cwd
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


def test_search_corpus_chunks_accepts_root_name_filter_in_all_streams():
    source = open(database.__file__, encoding="utf-8").read()
    function = source.split("def search_corpus_chunks", 1)[1].split("\ndef ", 1)[0]

    assert "root_name: str | None = None" in function
    assert "r.name = %s" in function
    assert "r.name AS root_name" in function
    assert "root_name_params" in function


def test_search_episodes_accepts_cwd_and_root_path_filter():
    source = open(database.__file__, encoding="utf-8").read()
    function = source.split("def search_episodes", 1)[1].split("\ndef ", 1)[0]

    assert "cwd: str | None = None" in function
    assert "root_path: str | None = None" in function
    assert "metadata->>'cwd'" in function
    assert "_path_scope_sql" in function


def test_path_scope_sql_escapes_windows_backslashes_for_like_prefixes():
    _sql, params = database._path_scope_sql("metadata->>'cwd'", root_path="E:\\LLM KB")

    assert "E:\\LLM KB" in params
    assert "E:\\\\LLM KB\\\\%" in params
    assert "E:/LLM KB/%" in params
