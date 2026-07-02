from flux_llm_kb.retrieval_explain import build_query_snippet, explain_search_result, query_terms


def test_query_terms_are_unique_stable_and_normalized():
    assert query_terms("Dashboard dashboard RFP status?") == ["dashboard", "rfp", "status"]


def test_build_query_snippet_prefers_query_window_and_highlights_terms():
    text = "Intro text before the useful section. Dashboard operations show watcher health and mail state."

    snippet = build_query_snippet("watcher dashboard", text, source="summary", source_path="docs/ops.md", max_chars=72)

    assert snippet["source"] == "summary"
    assert snippet["source_path"] == "docs/ops.md"
    assert snippet["matched_terms"] == ["dashboard", "watcher"]
    assert "Dashboard operations show watcher health" in snippet["text"]
    assert [
        {key: highlight[key] for key in ("term", "start", "end")}
        for highlight in snippet["highlights"]
    ] == [
        {"term": "dashboard", "start": snippet["text"].lower().index("dashboard"), "end": snippet["text"].lower().index("dashboard") + len("dashboard")},
        {"term": "watcher", "start": snippet["text"].lower().index("watcher"), "end": snippet["text"].lower().index("watcher") + len("watcher")},
    ]


def test_build_query_snippet_falls_back_and_redacts_secret_like_text():
    snippet = build_query_snippet(
        "unmatched",
        "password=hunter2 appears before normal deployment notes.",
        source="summary",
        max_chars=80,
    )

    assert "hunter2" not in snippet["text"]
    assert "[REDACTED:password_assignment]" in snippet["text"]
    assert snippet["matched_terms"] == []
    assert snippet["highlights"] == []


def test_explain_search_result_uses_existing_retrieval_signals():
    item = {
        "kind": "corpus_chunk",
        "logical_kind": "file",
        "id": "chunk-1",
        "title": "architecture.md",
        "summary": "Dashboard retrieval uses Vespa RRF ranking and full text.",
        "score": 0.42,
        "streams": ["corpus_lexical", "vespa_rrf", "vespa_lexical", "vespa_dense"],
        "raw_scores": {"corpus_lexical": 0.9, "vespa_rrf": 0.016, "vespa_lexical": 1.2, "vespa_dense": 0.8},
        "retrieval_scope": "local",
        "retrieval_root_name": "docs",
        "source_path": "docs/architecture.md",
        "root_name": "docs",
        "trust_rank": 450,
        "duplicate_count": 2,
        "base_score": 0.3,
        "scope_score_boost": 1.15,
        "rank": 1,
        "rank_margin": 0.19,
        "lifecycle": {
            "state": "active",
            "score": 0.91,
            "current": True,
            "explanation": {"penalties": {"state": 1.0, "retention": 1.0}},
        },
    }

    explanation = explain_search_result("vespa dashboard", item)

    assert explanation["score"] == 0.42
    assert explanation["streams"] == ["corpus_lexical", "vespa_rrf", "vespa_lexical", "vespa_dense"]
    assert explanation["raw_scores"] == {"corpus_lexical": 0.9, "vespa_rrf": 0.016, "vespa_lexical": 1.2, "vespa_dense": 0.8}
    assert explanation["scope"] == {"label": "local", "root_name": "docs"}
    assert explanation["corpus"] == {
        "source_path": "docs/architecture.md",
        "root_name": "docs",
        "trust_rank": 450,
        "duplicate_count": 2,
        "related_evidence_count": 0,
    }
    assert explanation["adjustments"] == {"base_score": 0.3, "scope_score_boost": 1.15}
    assert explanation["confidence"] == {
        "band": "high",
        "factors": {
            "rank": 1,
            "rank_margin": 0.19,
            "stream_count": 4,
            "local_scope": True,
            "exact_signal": False,
            "lifecycle_score": 0.91,
            "suppression_present": True,
        },
    }


def test_explain_search_result_surfaces_filters_and_suppression_metadata():
    item = {
        "kind": "corpus_chunk",
        "logical_kind": "file",
        "id": "chunk-current",
        "title": "RFP Response",
        "summary": "Current response",
        "score": 0.7,
        "streams": ["corpus_lexical"],
        "raw_scores": {"corpus_lexical": 0.9},
        "source_path": "client/RFP Response v2 final.docx",
        "duplicate_count": 2,
        "version_family": {
            "key": "rfp response",
            "canonical_source_path": "client/RFP Response v2 final.docx",
            "suppressed_count": 1,
            "suppressed_source_paths": ["client/RFP Response v1.docx"],
        },
        "semantic_duplicate_cluster": {
            "cluster_id": "semantic-cluster-1",
            "threshold": 0.86,
            "max_similarity": 0.94,
            "suppressed_count": 1,
            "suppressed": [{"owner_id": "chunk-old", "similarity": 0.94, "source_path": "client/RFP Response copy.docx"}],
        },
        "retrieval_filters": {
            "logical_kinds": ["file"],
            "current_only": True,
            "lifecycle_states": [],
            "include_suppressed": True,
        },
        "lifecycle": {
            "state": "active",
            "score": 0.88,
            "current": True,
            "explanation": {"penalties": {"state": 1.0, "retention": 1.0}},
        },
    }

    explanation = explain_search_result("rfp response", item)

    assert explanation["filters"]["active"] == {
        "logical_kinds": ["file"],
        "current_only": True,
        "lifecycle_states": [],
        "include_suppressed": True,
    }
    assert explanation["suppression"] == {
        "exact_duplicates": {
            "suppressed_count": 2,
            "canonical_source_path": "client/RFP Response v2 final.docx",
            "reason": "exact_content_duplicate",
        },
        "version_family": {
            "key": "rfp response",
            "canonical_source_path": "client/RFP Response v2 final.docx",
            "suppressed_count": 1,
            "suppressed_source_paths": ["client/RFP Response v1.docx"],
            "reason": "same_document_version_family",
        },
        "semantic_duplicates": {
            "cluster_id": "semantic-cluster-1",
            "threshold": 0.86,
            "max_similarity": 0.94,
            "suppressed_count": 1,
            "suppressed": [{"owner_id": "chunk-old", "similarity": 0.94, "source_path": "client/RFP Response copy.docx"}],
            "reason": "semantic_near_duplicate",
        },
    }
    assert explanation["lifecycle"]["explanation"]["penalties"]["retention"] == 1.0


def test_explain_search_result_surfaces_deprioritization_when_lifecycle_penalizes_result():
    item = {
        "kind": "episode",
        "logical_kind": "episode",
        "id": "episode-stale",
        "title": "Old deployment note",
        "summary": "A stale deployment note.",
        "score": 0.12,
        "streams": ["claim_lifecycle"],
        "retrieval_scope": "global_fallback",
        "lifecycle": {
            "state": "stale",
            "score": 0.42,
            "current": False,
            "explanation": {
                "retention_action": "deprioritize",
                "penalties": {"state": 0.7, "retention": 0.6},
            },
        },
    }

    explanation = explain_search_result("deployment", item)

    assert explanation["confidence"]["band"] == "low"
    assert explanation["deprioritization"] == {
        "reasons": ["non_current", "state:stale", "retention:deprioritize", "penalty:state", "penalty:retention"],
        "lifecycle_score": 0.42,
        "penalties": {"state": 0.7, "retention": 0.6},
    }
