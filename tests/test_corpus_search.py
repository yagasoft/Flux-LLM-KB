import json

from flux_llm_kb import database
from flux_llm_kb import service
from flux_llm_kb.service import KnowledgeService


def test_normalize_retrieval_filters_canonicalizes_contract():
    filters = service.normalize_retrieval_filters(
        {
            "logical_kinds": ["mail", "file", "mail"],
            "current_only": True,
            "lifecycle_states": ["active", "stale", "active"],
            "include_suppressed": True,
            "file_kind": ["code", "code"],
            "language": ["Python", "python"],
            "symbol_kind": "Function",
            "relationship": "Definition",
            "path_glob": ["src/*.py", "src/*.py"],
            "include_generated": True,
        }
    )

    assert filters == {
        "logical_kinds": ["file", "mail"],
        "current_only": True,
        "lifecycle_states": ["active", "stale"],
        "include_suppressed": True,
        "file_kinds": ["code"],
        "languages": ["python"],
        "symbol_kinds": ["function"],
        "relationships": ["definition"],
        "path_globs": ["src/*.py"],
        "include_generated": True,
    }


def test_service_search_includes_corpus_chunks(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, limit=5, **_kwargs: [
            {
                "id": "episode-1",
                "title": "Prior decision",
                "summary": "Use PostgreSQL for memory.",
                "score": 0.2,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 1.0},
            }
        ],
    )
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=5, **_kwargs: [
            {
                "id": "chunk-1",
                "title": "architecture.md",
                "summary": "Crawler dashboard health is centralized.",
                "score": 0.9,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 1.0},
                "source_path": "docs/architecture.md",
            }
        ],
    )

    results = KnowledgeService().search("crawler dashboard", limit=5)

    assert [result["kind"] for result in results] == ["corpus_chunk", "episode"]
    assert results[0]["source_path"] == "docs/architecture.md"


def test_service_search_adds_query_snippet_and_retrieval_explanation(monkeypatch):
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-1",
                "asset_id": "asset-1",
                "title": "architecture.md",
                "summary": "Dashboard retrieval uses pgvector, lifecycle scoring, and snippets.",
                "score": 0.9,
                "streams": ["corpus_lexical", "corpus_vector"],
                "raw_scores": {"corpus_lexical": 1.0, "corpus_vector": 0.5},
                "source_path": "docs/architecture.md",
                "root_name": "docs",
                "duplicate_count": 1,
                "trust_rank": 450,
            }
        ],
    )

    result = KnowledgeService().search("pgvector dashboard", limit=5)[0]

    assert result["excerpt"] == result["snippet"]["text"]
    assert result["snippet"]["matched_terms"] == ["dashboard", "pgvector"]
    assert result["snippet"]["source_path"] == "docs/architecture.md"
    assert result["retrieval_explanation"]["streams"] == ["corpus_lexical", "corpus_vector"]
    assert result["retrieval_explanation"]["scope"] == {"label": "global"}
    assert result["retrieval_explanation"]["corpus"]["source_path"] == "docs/architecture.md"
    assert result["retrieval_explanation"]["corpus"]["duplicate_count"] == 1


def test_service_search_forwards_code_filters_and_exposes_code_explanation(monkeypatch):
    captured = {}
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])

    def fake_search_corpus_chunks(query, limit=20, root_name=None, filters=None, **_kwargs):
        captured.update({"query": query, "limit": limit, "root_name": root_name, "filters": filters})
        return [
            {
                "id": "chunk-code",
                "asset_id": "asset-code",
                "title": "src/orders.py::OrderService.build_invoice",
                "summary": "def build_invoice(self, order_id): return order_id",
                "score": 0.97,
                "streams": ["code_symbol_exact", "corpus_lexical"],
                "raw_scores": {"code_symbol_exact": 2.5, "corpus_lexical": 0.8},
                "source_path": "src/orders.py",
                "root_name": "repo",
                "duplicate_count": 0,
                "trust_rank": 500,
                "file_kind": "code",
                "code": {
                    "language": "python",
                    "primary_symbol": "OrderService.build_invoice",
                    "symbol_kind": "method",
                    "relationship": "definition",
                    "range": {"line_start": 6, "line_end": 7},
                    "parser_status": "parsed",
                },
            }
        ]

    monkeypatch.setattr(database, "search_corpus_chunks", fake_search_corpus_chunks)

    results = KnowledgeService().search(
        "build_invoice",
        root_name="repo",
        filters={
            "file_kind": "code",
            "language": "python",
            "symbol_kind": "method",
            "relationship": "definition",
            "path_glob": "src/*.py",
            "include_generated": True,
        },
    )

    assert captured["filters"] == {
        "logical_kinds": [],
        "current_only": False,
        "lifecycle_states": [],
        "include_suppressed": False,
        "file_kinds": ["code"],
        "languages": ["python"],
        "symbol_kinds": ["method"],
        "relationships": ["definition"],
        "path_globs": ["src/*.py"],
        "include_generated": True,
    }
    assert results[0]["code"]["primary_symbol"] == "OrderService.build_invoice"
    assert results[0]["retrieval_explanation"]["code"] == results[0]["code"]
    assert results[0]["retrieval_explanation"]["streams"][0] == "code_symbol_exact"


def test_service_explain_surfaces_semantic_duplicate_suppression_without_raw_duplicate_content(monkeypatch):
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-canonical",
                "asset_id": "asset-canonical",
                "title": "Architecture",
                "summary": "Flux retrieval architecture uses pgvector and local ranking.",
                "score": 0.9,
                "streams": ["corpus_lexical", "corpus_vector"],
                "raw_scores": {"corpus_lexical": 1.0, "corpus_vector": 0.8},
                "source_path": "docs/architecture.md",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 900,
                "semantic_duplicate_cluster": {
                    "cluster_id": "cluster-1",
                    "canonical_owner_id": "chunk-canonical",
                    "suppressed_count": 1,
                    "reason": "semantic_near_duplicate",
                    "threshold": 0.9,
                    "max_similarity": 0.94,
                    "suppressed": [
                        {
                            "owner_id": "chunk-copy",
                            "owner_table": "asset_chunks",
                            "similarity": 0.94,
                            "label": "Architecture Copy",
                            "source_path": "docs/archive/architecture-copy.md",
                        }
                    ],
                },
            }
        ],
    )

    payload = KnowledgeService().explain(
        "pgvector architecture",
        limit=5,
        token_budget=100,
        filters={"include_suppressed": True},
    )

    assert payload["results"][0]["retrieval_explanation"]["suppression"]["semantic_duplicates"] == {
        "cluster_id": "cluster-1",
        "suppressed_count": 1,
        "reason": "semantic_near_duplicate",
        "threshold": 0.9,
        "max_similarity": 0.94,
        "suppressed": [
            {
                "owner_id": "chunk-copy",
                "owner_table": "asset_chunks",
                "similarity": 0.94,
                "label": "Architecture Copy",
                "source_path": "docs/archive/architecture-copy.md",
            }
        ],
    }
    assert payload["suppression"]["semantic_duplicates"][0]["suppressed_count"] == 1
    assert "Flux retrieval architecture uses pgvector" in payload["results"][0]["summary"]
    assert "raw duplicate" not in json.dumps(payload).lower()


def test_service_semantic_duplicate_methods_forward_to_database(monkeypatch):
    calls = {}

    def fake_refresh(**kwargs):
        calls["refresh"] = kwargs
        return {"created_clusters": 1}

    def fake_list(**kwargs):
        calls["list"] = kwargs
        return {"clusters": []}

    monkeypatch.setattr(database, "refresh_semantic_duplicate_clusters", fake_refresh)
    monkeypatch.setattr(database, "list_semantic_duplicate_clusters", fake_list)

    refresh = KnowledgeService().refresh_semantic_duplicate_clusters(
        memory_class="corpus",
        root_name="docs",
        threshold=0.91,
        limit=25,
    )
    listed = KnowledgeService().list_semantic_duplicate_clusters(memory_class="claim", root_name=None, limit=7)

    assert refresh == {"created_clusters": 1}
    assert listed == {"clusters": []}
    assert calls["refresh"] == {"memory_class": "corpus", "root_name": "docs", "threshold": 0.91, "limit": 25}
    assert calls["list"] == {"memory_class": "claim", "root_name": None, "limit": 7}


def test_service_search_formats_mail_manifest_results(monkeypatch):
    manifest = {
        "subject": "YsTrader alert: shared market data unavailable",
        "sender": "YsTrader <alerts@example.com>",
        "recipients": ["me@example.com"],
        "received_at": "Mon, 22 Jun 2026 19:19:26 +0000",
        "source_folder": "FluxCapture",
        "attachment_count": 1,
    }
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-1",
                "title": "manifest.json",
                "summary": json.dumps(manifest),
                "score": 0.032,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.24},
                "source_path": "export-1/manifest.json",
                "duplicate_count": 0,
                "trust_rank": 450,
            }
        ],
    )

    results = KnowledgeService().search("ystrader", limit=5)

    assert results[0]["title"] == "Mail: YsTrader alert: shared market data unavailable"
    assert "From YsTrader" in results[0]["summary"]
    assert "FluxCapture" in results[0]["summary"]
    assert "1 attachment" in results[0]["summary"]
    assert results[0]["excerpt"] == results[0]["summary"]


def test_service_search_collapses_mail_spool_siblings(monkeypatch):
    manifest = {
        "export_id": "export-1",
        "profile_name": "gmail-capture",
        "subject": "Customer RFP",
        "sender": "Sender <sender@example.com>",
        "recipients": ["me@example.com"],
        "source_folder": "FluxCapture",
        "attachment_count": 1,
    }
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-body",
                "asset_id": "asset-body",
                "title": "body.txt",
                "summary": "Please review the Customer RFP",
                "score": 0.060,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.4},
                "source_path": "export-1/body.txt",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
            {
                "id": "chunk-manifest",
                "asset_id": "asset-manifest",
                "title": "manifest.json",
                "summary": json.dumps(manifest),
                "score": 0.050,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.3},
                "source_path": "export-1/manifest.json",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
            {
                "id": "chunk-attachment",
                "asset_id": "asset-attachment",
                "title": "rfp.pdf",
                "summary": "Customer RFP attachment",
                "score": 0.040,
                "streams": ["corpus_fuzzy"],
                "raw_scores": {"corpus_fuzzy": 0.2},
                "source_path": "export-1/attachments/rfp.pdf",
                "duplicate_count": 0,
                "trust_rank": 450,
            },
        ],
    )

    results = KnowledgeService().search("customer rfp", limit=5)

    assert len(results) == 1
    assert results[0]["logical_kind"] == "mail"
    assert results[0]["title"] == "Mail: Customer RFP"
    assert results[0]["source_path"] == "export-1/manifest.json"
    assert results[0]["related_evidence_count"] == 2
    assert results[0]["detail_ref"] == {"kind": "corpus_chunk", "id": "chunk-manifest"}


def test_service_brief_uses_configured_token_budget(monkeypatch):
    observed = {}

    class FakeSetting:
        raw_value = 321

    class FakeSettingsService:
        def resolve(self, key):
            assert key == "retrieval.token_budget"
            return FakeSetting()

    monkeypatch.setattr(service, "SettingsService", FakeSettingsService)
    monkeypatch.setattr(KnowledgeService, "search", lambda self, query, limit=10, **_kwargs: [])
    monkeypatch.setattr(
        service,
        "pack_context",
        lambda candidates, token_budget: (observed.setdefault("token_budget", token_budget), "brief")[1],
    )

    assert KnowledgeService().brief("anything") == "brief"
    assert observed["token_budget"] == 321


def test_service_brief_prefers_current_lifecycle_evidence(monkeypatch):
    def fake_search(_self, _query, limit=10, **_kwargs):
        return [
            {
                "kind": "episode",
                "id": "old",
                "title": "Old Decision",
                "summary": "Use the retired path.",
                "score": 0.99,
                "lifecycle": {"state": "superseded", "current": False, "audit_visible": True},
            },
            {
                "kind": "episode",
                "id": "new",
                "title": "Current Decision",
                "summary": "Use the current path.",
                "score": 0.5,
                "lifecycle": {"state": "active", "current": True, "audit_visible": False},
            },
        ]

    monkeypatch.setattr(KnowledgeService, "search", fake_search)

    brief = KnowledgeService().brief("decision", token_budget=100)

    assert "Current Decision" in brief
    assert "Old Decision" not in brief


def test_service_explain_returns_results_and_brief_selection_trace(monkeypatch):
    def fake_search_raw(_self, _query, *, limit=10, **_kwargs):
        return [
            {
                "kind": "episode",
                "id": "old",
                "title": "Old Decision",
                "summary": "Use the retired path.",
                "score": 0.99,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 0.8},
                "snippet": {"text": "Use the retired path.", "matched_terms": [], "highlights": [], "source": "summary"},
                "retrieval_explanation": {"score": 0.99, "streams": ["lexical"], "raw_scores": {"lexical": 0.8}, "scope": {"label": "global"}},
                "lifecycle": {"state": "superseded", "current": False, "audit_visible": True},
            },
            {
                "kind": "episode",
                "id": "new",
                "title": "Current Decision",
                "summary": "Use the current retrieval path.",
                "score": 0.5,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 0.4},
                "snippet": {"text": "Use the current retrieval path.", "matched_terms": ["retrieval"], "highlights": [], "source": "summary"},
                "retrieval_explanation": {"score": 0.5, "streams": ["lexical"], "raw_scores": {"lexical": 0.4}, "scope": {"label": "global"}},
                "lifecycle": {"state": "active", "current": True, "audit_visible": False},
            },
        ][:limit]

    monkeypatch.setattr(KnowledgeService, "_search_raw", fake_search_raw)

    payload = KnowledgeService().explain("retrieval decision", limit=2, token_budget=30)

    assert payload["query"] == "retrieval decision"
    assert [result["id"] for result in payload["results"]] == ["old", "new"]
    assert "Current Decision" in payload["brief"]["text"]
    assert "Old Decision" not in payload["brief"]["text"]
    assert [item["id"] for item in payload["brief"]["packed"]] == ["new"]
    assert payload["brief"]["excluded"][0]["id"] == "old"
    assert payload["brief"]["excluded"][0]["reason"] == "non_current"


def test_service_search_preserves_lifecycle_and_graph_metadata(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, limit=5, **_kwargs: [
            {
                "id": "episode-1",
                "title": "Graph decision",
                "summary": "Claim-backed graph result.",
                "score": 0.8,
                "streams": ["claim_lifecycle", "graph"],
                "raw_scores": {"claim_lifecycle": 0.7, "graph": 0.4},
                "lifecycle": {"state": "active", "score": 0.7, "current": True},
                "graph": {"matched_claim_ids": ["claim-1"], "entity_ids": ["entity-1"]},
            }
        ],
    )
    monkeypatch.setattr(database, "search_corpus_chunks", lambda query, limit=20, **_kwargs: [])

    result = KnowledgeService().search("graph decision", limit=5)[0]

    assert result["lifecycle"]["state"] == "active"
    assert result["graph"]["matched_claim_ids"] == ["claim-1"]
    assert result["streams"] == ["claim_lifecycle", "graph"]


def test_service_search_applies_logical_kind_and_current_filters(monkeypatch):
    monkeypatch.setattr(
        database,
        "search_episodes",
        lambda query, limit=5, **_kwargs: [
            {
                "id": "old-episode",
                "title": "Old Decision",
                "summary": "Use the old path.",
                "score": 0.95,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 0.8},
                "lifecycle": {"state": "superseded", "current": False, "audit_visible": True},
            },
            {
                "id": "current-episode",
                "title": "Current Decision",
                "summary": "Use the current path.",
                "score": 0.80,
                "streams": ["lexical"],
                "raw_scores": {"lexical": 0.7},
                "lifecycle": {"state": "active", "current": True, "audit_visible": False},
            },
        ],
    )
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-1",
                "asset_id": "asset-1",
                "title": "RFP",
                "summary": "A matching file.",
                "score": 0.70,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 1.0},
                "source_path": "docs/rfp.md",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 500,
            }
        ],
    )

    results = KnowledgeService().search(
        "current path",
        filters={"logical_kinds": ["episode"], "current_only": True},
    )

    assert [result["id"] for result in results] == ["current-episode"]
    assert results[0]["retrieval_explanation"]["filters"]["active"]["logical_kinds"] == ["episode"]
    assert results[0]["retrieval_explanation"]["filters"]["active"]["current_only"] is True


def test_service_explain_returns_filter_trace_and_suppression_metadata(monkeypatch):
    monkeypatch.setattr(database, "search_episodes", lambda query, limit=5, **_kwargs: [])
    monkeypatch.setattr(
        database,
        "search_corpus_chunks",
        lambda query, limit=20, **_kwargs: [
            {
                "id": "chunk-v1",
                "asset_id": "asset-v1",
                "title": "RFP Response",
                "summary": "older response body should not appear in suppression trace",
                "score": 0.91,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.9},
                "source_path": "client/RFP Response v1.docx",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 500,
            },
            {
                "id": "chunk-v2",
                "asset_id": "asset-v2",
                "title": "RFP Response",
                "summary": "newer response",
                "score": 0.89,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.8},
                "source_path": "client/RFP Response v2 final.docx",
                "root_name": "docs",
                "duplicate_count": 3,
                "trust_rank": 900,
            },
            {
                "id": "chunk-not-mail",
                "asset_id": "asset-file",
                "title": "Delivery Plan",
                "summary": "file-only result",
                "score": 0.75,
                "streams": ["corpus_lexical"],
                "raw_scores": {"corpus_lexical": 0.7},
                "source_path": "client/Delivery Plan.docx",
                "root_name": "docs",
                "duplicate_count": 0,
                "trust_rank": 500,
            },
        ],
    )

    payload = KnowledgeService().explain(
        "rfp response",
        limit=5,
        token_budget=100,
        filters={"logical_kinds": ["mail"], "include_suppressed": True},
    )

    assert payload["results"] == []
    assert payload["filters"] == {
        "logical_kinds": ["mail"],
        "current_only": False,
        "lifecycle_states": [],
        "include_suppressed": True,
    }
    assert {item["reason"] for item in payload["filter_trace"]["excluded"]} == {"logical_kind"}
    assert all("summary" not in item for item in payload["filter_trace"]["excluded"])
    assert "older response body should not appear" not in str(payload["filter_trace"])
    assert payload["suppression"]["version_families"][0]["suppressed_count"] == 1
    assert payload["suppression"]["exact_duplicates"][0]["suppressed_count"] == 3
