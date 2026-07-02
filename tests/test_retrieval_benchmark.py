import json

from flux_llm_kb import database
from flux_llm_kb import retrieval_benchmark
from flux_llm_kb.retrieval_benchmark import evaluate_retrieval_cases
from flux_llm_kb.retrieval_benchmark import build_governance_shadow_proposals
from flux_llm_kb.service import KnowledgeService
from flux_llm_kb.service import _retrieval_benchmark_expected_id


def test_retrieval_benchmark_metrics_cover_search_brief_scope_and_suppression():
    cases = [
        {
            "id": "case-top",
            "category": "semantic_duplicate",
            "query": "alpha query",
            "expected_ids": ["chunk-alpha"],
            "expected_brief_ids": ["chunk-alpha"],
            "expected_scope": "local",
            "expect_suppression": True,
            "semantic_similarity": 0.92,
            "expected_semantic_duplicate": True,
        },
        {
            "id": "case-miss",
            "category": "semantic_guardrail",
            "query": "beta query",
            "expected_ids": ["chunk-beta"],
            "expected_brief_ids": ["chunk-beta"],
            "expected_scope": "local",
            "expect_suppression": False,
            "semantic_similarity": 0.81,
            "expected_semantic_duplicate": False,
        },
    ]
    observations = {
        "case-top": {
            "results": [
                {
                    "id": "chunk-alpha",
                    "kind": "corpus_chunk",
                    "logical_kind": "file",
                    "score": 0.91,
                    "streams": ["corpus_lexical"],
                    "retrieval_scope": "local",
                    "retrieval_explanation": {
                        "confidence": {"band": "high"},
                        "suppression": {"exact_duplicates": {"suppressed_count": 1}},
                    },
                }
            ],
            "brief": {"packed": [{"id": "chunk-alpha", "tokens": 7}], "excluded": []},
            "elapsed_ms": 12,
        },
        "case-miss": {
            "results": [
                {
                    "id": "chunk-other",
                    "kind": "episode",
                    "logical_kind": "episode",
                    "score": 0.4,
                    "streams": ["episode_vector"],
                    "retrieval_scope": "global_fallback",
                    "retrieval_explanation": {
                        "confidence": {"band": "low"},
                    },
                }
            ],
            "brief": {"packed": [{"id": "chunk-other", "tokens": 40}], "excluded": [{"id": "chunk-beta"}]},
            "elapsed_ms": 18,
        },
    }

    report = evaluate_retrieval_cases(cases, observations, limit_per_query=5)

    assert report["metrics"]["top1_accuracy"] == 0.5
    assert report["metrics"]["precision_at_3"] == 0.5
    assert report["metrics"]["recall_at_5"] == 0.5
    assert report["metrics"]["mrr"] == 0.5
    assert report["metrics"]["ndcg_at_5"] == 0.5
    assert report["metrics"]["brief_recall"] == 0.5
    assert report["metrics"]["brief_dilution"] == 0.5
    assert report["metrics"]["scope_pass_count"] == 1
    assert report["metrics"]["suppression_pass_count"] == 2
    assert report["calibration_summary"]["confidence_bands"] == {"high": 1, "low": 1}
    assert report["calibration_summary"]["case_categories"] == {
        "semantic_duplicate": {"passed": 1, "failed": 0, "total": 1},
        "semantic_guardrail": {"passed": 0, "failed": 1, "total": 1},
    }
    assert report["calibration_summary"]["semantic_thresholds"] == [
        {"threshold": 0.82, "evaluated_count": 2, "false_positive_count": 0, "false_negative_count": 0, "pass_count": 2},
        {"threshold": 0.86, "evaluated_count": 2, "false_positive_count": 0, "false_negative_count": 0, "pass_count": 2},
        {"threshold": 0.9, "evaluated_count": 2, "false_positive_count": 0, "false_negative_count": 0, "pass_count": 2},
    ]
    assert report["passed_count"] == 1
    assert report["failed_count"] == 1
    assert report["case_results"][0]["query_hash"].startswith("sha256:")
    assert report["case_results"][0]["category"] == "semantic_duplicate"
    assert report["case_results"][0]["confidence_band"] == "high"
    assert report["case_results"][0]["score_evidence"] == {
        "top_score": 0.91,
        "runner_up_score": None,
        "rank_margin": None,
        "top_streams": ["corpus_lexical"],
        "top_scope": "local",
    }
    assert report["case_results"][1]["failure_details"] == [
        {"reason": "top1_miss", "message": "Expected evidence was not ranked first."},
        {"reason": "recall_miss", "message": "Expected evidence was missing from the top 5 results."},
        {"reason": "brief_miss", "message": "Expected evidence was missing from the packed brief."},
        {"reason": "scope_miss", "message": "The top result came from an unexpected retrieval scope."},
    ]
    build_retrieval_recommendations = getattr(retrieval_benchmark, "build_retrieval_recommendations", None)
    assert callable(build_retrieval_recommendations)
    recommendations = build_retrieval_recommendations(report)
    assert recommendations["settings_mutated"] is False
    assert recommendations["candidates"] == [
        {
            "kind": "semantic_duplicate_threshold",
            "threshold": 0.86,
            "evidence_count": 2,
            "false_positive_count": 0,
            "false_negative_count": 0,
            "rationale": "Synthetic semantic duplicate calibration passed for 2/2 cases at threshold 0.86.",
        }
    ]
    assert "alpha query" not in json.dumps(report)
    assert "beta query" not in json.dumps(report)


def test_retrieval_benchmark_suppression_check_targets_expected_hits():
    report = evaluate_retrieval_cases(
        [
            {
                "id": "case-target",
                "category": "scoped_corpus",
                "query": "target query",
                "expected_ids": ["chunk-target"],
                "expected_scope": "local",
                "expect_suppression": False,
            }
        ],
        {
            "case-target": {
                "results": [
                    {
                        "id": "chunk-target",
                        "score": 0.9,
                        "retrieval_scope": "local",
                        "duplicate_count": 0,
                    },
                    {
                        "id": "chunk-runner-up",
                        "score": 0.4,
                        "retrieval_scope": "local",
                        "duplicate_count": 1,
                    },
                ],
                "brief": {"packed": [{"id": "chunk-target"}]},
            }
        },
    )

    case = report["case_results"][0]
    assert case["status"] == "passed"
    assert case["observed_suppression"] is False


def test_service_retrieval_benchmark_records_sanitized_metadata(monkeypatch):
    recorded = []

    class FakeService(KnowledgeService):
        def _prepare_retrieval_benchmark_cases(self, suite):
            assert suite == "standard"
            return [
                {
                    "id": "case-alpha",
                    "query": "alpha retrieval",
                        "expected_ids": ["chunk-alpha"],
                        "expected_brief_ids": ["chunk-alpha"],
                        "expected_scope": "local",
                        "expect_suppression": True,
                        "semantic_similarity": 0.91,
                        "expected_semantic_duplicate": True,
                    }
                ], lambda: None

        def search(self, query, limit=5, **_kwargs):
            return [
                {
                    "id": f"chunk-{query.split()[0]}",
                    "kind": "corpus_chunk",
                    "logical_kind": "file",
                    "title": "Synthetic result",
                    "summary": "Synthetic public-safe summary",
                    "score": 0.9,
                    "streams": ["corpus_lexical"],
                        "retrieval_scope": "local",
                        "retrieval_explanation": {
                            "confidence": {"band": "high"},
                            "suppression": {"exact_duplicates": {"suppressed_count": 1}},
                        },
                }
            ]

        def explain(self, query, limit=5, token_budget=None, **_kwargs):
            return {
                "query": query,
                "results": self.search(query, limit=limit),
                "brief": {"packed": [{"id": f"chunk-{query.split()[0]}", "tokens": 4}], "excluded": []},
            }

    monkeypatch.setattr(
        database,
        "record_retrieval_benchmark_run",
        lambda **kwargs: recorded.append(kwargs)
        or {
            "id": "retrieval-run-1",
            "suite": kwargs["suite"],
            "status": kwargs["status"],
            "query_count": kwargs["query_count"],
            "created_at": "2026-06-25T10:00:00+00:00",
        },
    )
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **_kwargs: [
            {
                "id": "retrieval-run-1",
                "suite": "standard",
                "label": "nightly",
                "metrics": {"top1_accuracy": 1.0},
                "previous_metrics": {"top1_accuracy": 0.8},
                "metric_deltas": {"top1_accuracy": 0.2},
                "calibration_summary": {"confidence_bands": {"high": 1}},
                "recommendation_metadata": {
                    "settings_mutated": False,
                    "candidates": [
                        {
                            "kind": "semantic_duplicate_threshold",
                            "threshold": 0.86,
                            "evidence_count": 1,
                            "false_positive_count": 0,
                            "false_negative_count": 0,
                            "rationale": "Synthetic semantic duplicate calibration passed for 1/1 cases at threshold 0.86.",
                        }
                    ],
                },
            }
        ],
    )

    result = FakeService().run_retrieval_benchmark(suite="standard", label="nightly", compare_label="baseline")

    assert result["suite"] == "standard"
    assert result["label"] == "nightly"
    assert result["compare_label"] == "baseline"
    assert result["status"] == "completed"
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["candidates"][0]["kind"] == "semantic_duplicate_threshold"
    assert result["metric_deltas"] == {"top1_accuracy": 0.2}
    assert result["calibration_summary"]["confidence_bands"] == {"high": 1}
    assert result["metrics"]["top1_accuracy"] == 1.0
    assert recorded[0]["suite"] == "standard"
    assert recorded[0]["label"] == "nightly"
    assert recorded[0]["query_count"] == result["query_count"]
    assert recorded[0]["metadata"]["suite_version"] == "v2"
    assert recorded[0]["recommendation_metadata"]["candidates"][0]["kind"] == "semantic_duplicate_threshold"
    serialized = json.dumps(recorded[0], default=str)
    assert "raw_query" not in serialized
    assert "Synthetic public-safe summary" not in serialized


def test_service_retrieval_benchmark_history_uses_database(monkeypatch):
    calls = []
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **kwargs: calls.append(kwargs)
        or [{"id": "retrieval-run-1", "suite": "standard", "metrics": {"top1_accuracy": 1.0}}],
    )

    result = KnowledgeService().retrieval_benchmark_history(suite="standard", label="nightly", limit=5)

    assert result == {
        "suite": "standard",
        "runs": [{"id": "retrieval-run-1", "suite": "standard", "metrics": {"top1_accuracy": 1.0}}],
    }
    assert calls == [{"suite": "standard", "label": "nightly", "limit": 5}]


def test_retrieval_benchmark_expected_id_uses_source_path_without_search_fallback(monkeypatch):
    monkeypatch.setattr(
        database,
        "list_source_assets",
        lambda **kwargs: [
            {
                "id": "asset-target",
                "path": "target.md",
                "canonical_asset_id": None,
            }
        ]
        if kwargs["root_name"] == "docs" and kwargs["path"] == "target.md"
        else [],
    )
    monkeypatch.setattr(
        database,
        "get_source_asset",
        lambda asset_id: {"id": asset_id, "chunks": [{"id": "chunk-target", "chunk_index": 0}]},
    )

    class FakeService(KnowledgeService):
        def search(self, query, limit=10, **_kwargs):
            return [{"id": "chunk-unrelated", "source_path": "other.md"}]

    expected_id = _retrieval_benchmark_expected_id(
        FakeService(),
        query="query that misses target",
        root_name="docs",
        source_path="target.md",
        filters=None,
    )

    assert expected_id == "chunk-target"


def test_retrieval_benchmark_expected_id_can_target_code_symbol(monkeypatch):
    monkeypatch.setattr(database, "list_source_assets", lambda **_kwargs: [])

    class MetadataService(KnowledgeService):
        def search(self, query, limit=10, **_kwargs):
            return [
                {
                    "id": "chunk-caller",
                    "source_path": "src/acceleration.py",
                    "title": "src/acceleration.py::collect_acceleration_status",
                    "code": {"primary_symbol": "collect_acceleration_status"},
                },
                {
                    "id": "chunk-helper",
                    "source_path": "src/acceleration.py",
                    "title": "src/acceleration.py::_watcher_backend_status",
                    "code": {"primary_symbol": "_watcher_backend_status"},
                },
            ]

    class TitleService(KnowledgeService):
        def search(self, query, limit=10, **_kwargs):
            return [
                {
                    "id": "chunk-caller",
                    "source_path": "src/acceleration.py",
                    "title": "src/acceleration.py::collect_acceleration_status",
                },
                {
                    "id": "chunk-helper",
                    "source_path": "src/acceleration.py",
                    "title": "src/acceleration.py::_watcher_backend_status",
                },
            ]

    assert (
        _retrieval_benchmark_expected_id(
            MetadataService(),
            query="_watcher_backend_status",
            root_name="docs",
            source_path="src/acceleration.py",
            filters=None,
            expected_symbol="_watcher_backend_status",
        )
        == "chunk-helper"
    )
    assert (
        _retrieval_benchmark_expected_id(
            TitleService(),
            query="_watcher_backend_status",
            root_name="docs",
            source_path="src/acceleration.py",
            filters=None,
            expected_symbol="_watcher_backend_status",
        )
        == "chunk-helper"
    )


def test_governance_shadow_proposals_are_metadata_only_and_non_mutating():
    report = {
        "candidates": [
            {
                "id": "claim-stale",
                "memory_class": "claim",
                "quality_bucket": "review",
                "reason": "stale",
                "label": "Private claim text should not be copied",
                "confidence": 0.21,
                "lifecycle_state": "stale",
            },
            {
                "id": "cluster-1",
                "memory_class": "corpus",
                "quality_bucket": "deprioritize",
                "reason": "semantic_duplicate",
                "metadata": {"root_name": "docs", "suppressed_count": 3},
            },
            {
                "id": "claim-current",
                "memory_class": "claim",
                "quality_bucket": "healthy",
                "reason": "current",
                "lifecycle_state": "active",
            },
        ]
    }

    proposals = build_governance_shadow_proposals(report)

    assert proposals["settings_mutated"] is False
    assert proposals["candidate_count"] == 2
    assert proposals["proposal_categories"] == {"review": 1, "deprioritize": 1}
    assert proposals["candidates"] == [
        {
            "target_id": "claim-stale",
            "memory_class": "claim",
            "proposal": "review",
            "reason": "stale",
            "evidence": {"confidence": 0.21, "lifecycle_state": "stale", "quality_bucket": "review"},
        },
        {
            "target_id": "cluster-1",
            "memory_class": "corpus",
            "proposal": "deprioritize",
            "reason": "semantic_duplicate",
            "evidence": {"quality_bucket": "deprioritize", "suppressed_count": 3},
        },
    ]
    assert "Private claim text" not in json.dumps(proposals)


def test_service_retrieval_benchmark_governance_shadow_suite_records_metadata(monkeypatch):
    recorded = []

    class FakeService(KnowledgeService):
        def _prepare_retrieval_benchmark_cases(self, suite):
            assert suite == "governance-shadow"
            return [
                {
                    "id": "governance-stale",
                    "category": "governance_stale",
                    "query": "stale governance evidence",
                    "expected_ids": ["claim-stale"],
                    "expected_brief_ids": ["claim-stale"],
                },
                {
                    "id": "governance-current-guardrail",
                    "category": "governance_guardrail_current",
                    "query": "current protected governance guardrail",
                    "expected_ids": ["claim-current"],
                    "expected_brief_ids": ["claim-current"],
                },
            ], lambda: None

        def explain(self, query, limit=5, token_budget=None, **_kwargs):
            item_id = "claim-current" if "current" in query else "claim-stale"
            return {
                "results": [
                    {
                        "id": item_id,
                        "kind": "claim",
                        "logical_kind": "claim",
                        "score": 0.9,
                        "streams": ["claim_lifecycle"],
                        "retrieval_scope": "local",
                        "retrieval_explanation": {"confidence": {"band": "high"}},
                    }
                ],
                "brief": {"packed": [{"id": item_id, "tokens": 6}], "excluded": []},
            }

    monkeypatch.setattr(
        database,
        "record_retrieval_benchmark_run",
        lambda **kwargs: recorded.append(kwargs)
        or {
            "id": "retrieval-run-1",
            "suite": kwargs["suite"],
            "status": kwargs["status"],
            "query_count": kwargs["query_count"],
            "created_at": "2026-06-25T10:00:00+00:00",
        },
    )
    monkeypatch.setattr(database, "list_retrieval_benchmark_runs", lambda **_kwargs: [])

    result = FakeService().run_retrieval_benchmark(suite="governance-shadow", label="shadow")

    assert result["suite"] == "governance-shadow"
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["purpose"] == "governance_shadow_evaluation"
    assert result["recommendations"]["governance_shadow"]["proposal_case_count"] == 1
    assert result["recommendations"]["governance_shadow"]["guardrail_pass_count"] == 1
    assert recorded[0]["suite"] == "governance-shadow"
    assert recorded[0]["metadata"]["governance_shadow"]["guardrail_case_count"] == 1


def test_governance_shadow_suite_includes_expanded_governance_cases(monkeypatch):
    episode_ids = iter(
        [
            "episode-stale",
            "episode-low-confidence",
            "episode-duplicate-a",
            "episode-duplicate-b",
            "episode-current",
            "episode-contradiction",
            "episode-capture",
            "episode-feedback",
        ]
    )
    claim_ids = iter(["claim-stale", "claim-low-confidence", "claim-current", "claim-contradiction"])

    monkeypatch.setattr(database, "insert_episode", lambda **_kwargs: next(episode_ids))
    monkeypatch.setattr(database, "upsert_claim", lambda **_kwargs: {"id": next(claim_ids)})
    monkeypatch.setattr(database, "transition_claim", lambda **_kwargs: None)
    monkeypatch.setattr(database, "forget_episode", lambda _episode_id: None)

    cases, cleanup = KnowledgeService()._prepare_governance_shadow_benchmark_cases()
    cleanup()

    categories = {case["category"] for case in cases}
    assert {
        "governance_apply_recover",
        "governance_stale_proposal_conflict",
        "governance_duplicate_cluster",
        "governance_capture_ingestion",
        "governance_feedback_gap",
        "governance_guardrail_current",
    } <= categories


def test_service_retrieval_benchmark_standard_suite_includes_expanded_code_cases(monkeypatch):
    calls = []
    episode_ids = iter(["episode-current", "episode-stale"])

    monkeypatch.setattr(database, "add_monitored_root", lambda **kwargs: calls.append(("add_root", kwargs)))
    monkeypatch.setattr(database, "delete_monitored_root", lambda **kwargs: calls.append(("delete_root", kwargs)))
    monkeypatch.setattr(database, "forget_episode", lambda episode_id: calls.append(("forget", episode_id)))
    monkeypatch.setattr(database, "insert_episode", lambda **kwargs: next(episode_ids))
    monkeypatch.setattr(database, "upsert_claim", lambda **kwargs: {"id": "claim-stale"})
    monkeypatch.setattr(database, "transition_claim", lambda **kwargs: calls.append(("transition", kwargs)))
    monkeypatch.setattr(
        database,
        "refresh_semantic_duplicate_clusters",
        lambda **kwargs: calls.append(("refresh_semantic", kwargs)) or {"created_clusters": 1, "created_members": 2},
    )
    monkeypatch.setattr(
        database,
        "sync_search_index",
        lambda **kwargs: calls.append(("sync_search_index", kwargs)) or {"indexed": 12, "failed": 0},
    )
    monkeypatch.setattr(
        database,
        "_delete_search_index_records_for_root",
        lambda **kwargs: calls.append(("delete_search_index_records", kwargs)) or 29,
        raising=False,
    )
    monkeypatch.setattr(
        database,
        "list_source_assets",
        lambda **kwargs: [{"id": f"asset-{kwargs['path']}", "path": kwargs["path"], "canonical_asset_id": None}],
    )
    monkeypatch.setattr(
        database,
        "get_source_asset",
        lambda asset_id: {"id": asset_id, "chunks": [{"id": f"chunk-{asset_id}", "chunk_index": 0}]},
    )

    class FakeService(KnowledgeService):
        def sync_corpus(self, **_kwargs):
            calls.append(("sync", _kwargs))

        def search(self, query, limit=10, **_kwargs):
            if "_benchmark_private_helper" in query:
                return [
                    {
                        "id": "chunk-helper",
                        "source_path": "service_impl.py",
                        "title": "service_impl.py::_benchmark_private_helper",
                        "code": {"primary_symbol": "_benchmark_private_helper"},
                    }
                ]
            if "caller" in query or "test" in query:
                return [{"id": "chunk-test", "source_path": "tests/test_service_impl.py"}]
            if "route" in query:
                return [{"id": "chunk-route", "source_path": "web/routes.ts"}]
            if "generated" in query:
                return [{"id": "chunk-generated", "source_path": "generated/client.py"}]
            if "migration" in query:
                return [{"id": "chunk-migration", "source_path": "db/migrations/0001_create_benchmark_orders.sql"}]
            if "config" in query:
                return [{"id": "chunk-config", "source_path": "config/app.yaml"}]
            if "disambiguate" in query:
                return [{"id": "chunk-app-service", "source_path": "app/service_impl.py"}]
            return [{"id": "chunk-default", "source_path": "alpha-decision.md"}]

    cases, cleanup = FakeService()._prepare_retrieval_benchmark_cases("standard")
    cleanup()

    by_category = {case["category"]: case for case in cases}
    for category in {
        "code_caller",
        "code_test",
        "code_route",
        "code_generated_suppression",
        "code_config",
        "code_migration",
        "code_cross_root",
        "code_exact_definition",
    }:
        assert category in by_category
    assert by_category["code_exact_definition"]["expected_ids"] == ["chunk-helper"]
    assert by_category["code_exact_definition"]["expected_symbol"] == "_benchmark_private_helper"
    assert by_category["code_exact_definition"]["filters"]["file_kinds"] == ["code"]
    assert by_category["code_symbol"]["filters"]["path_globs"] == ["service_impl.py"]
    assert by_category["code_symbol"]["filters"]["file_kinds"] == ["code"]
    assert by_category["code_route"]["filters"]["relationships"] == ["route"]
    assert by_category["code_test"]["filters"]["relationships"] == ["test"]
    assert by_category["code_generated_suppression"]["filters"]["include_generated"] is True
    assert by_category["code_cross_root"]["filters"]["path_globs"] == ["app/*"]
    assert by_category["code_symbol_miss"]["filters"]["file_kinds"] == ["code"]
    assert by_category["code_symbol_miss"]["filters"]["path_globs"] == ["code_fallback.py"]
    add_root_calls = [call for call in calls if call[0] == "add_root"]
    assert add_root_calls[0][1]["include_globs"] == ["*", "**/*"]
    refresh_calls = [call for call in calls if call[0] == "refresh_semantic"]
    assert refresh_calls
    assert refresh_calls[0][1]["memory_class"] == "corpus"
    assert refresh_calls[0][1]["root_name"].startswith("__retrieval_benchmark_")
    search_index_calls = [call for call in calls if call[0] == "sync_search_index"]
    assert len(search_index_calls) == 2
    assert search_index_calls[0][1]["owner_class"] == "all"
    assert search_index_calls[0][1]["root_name"].startswith("__retrieval_benchmark_")
    assert search_index_calls[0][1]["limit"] == 1000
    assert search_index_calls[1][1]["owner_class"] == "all"
    assert search_index_calls[1][1]["root_name"].startswith("__retrieval_benchmark_")
    assert search_index_calls[1][1]["limit"] == 1000
    delete_index_calls = [call for call in calls if call[0] == "delete_search_index_records"]
    assert delete_index_calls
    assert delete_index_calls[0][1]["root_name"].startswith("__retrieval_benchmark_")
    assert delete_index_calls[0][1]["statuses"] == ["deleted"]
    assert any(call[0] == "delete_root" for call in calls)
