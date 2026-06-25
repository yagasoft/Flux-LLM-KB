import json

from flux_llm_kb import database
from flux_llm_kb.retrieval_benchmark import evaluate_retrieval_cases
from flux_llm_kb.service import KnowledgeService


def test_retrieval_benchmark_metrics_cover_search_brief_scope_and_suppression():
    cases = [
        {
            "id": "case-top",
            "query": "alpha query",
            "expected_ids": ["chunk-alpha"],
            "expected_brief_ids": ["chunk-alpha"],
            "expected_scope": "local",
            "expect_suppression": True,
        },
        {
            "id": "case-miss",
            "query": "beta query",
            "expected_ids": ["chunk-beta"],
            "expected_brief_ids": ["chunk-beta"],
            "expected_scope": "local",
            "expect_suppression": False,
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
    assert report["passed_count"] == 1
    assert report["failed_count"] == 1
    assert report["case_results"][0]["query_hash"].startswith("sha256:")
    assert "alpha query" not in json.dumps(report)
    assert "beta query" not in json.dumps(report)


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

    result = FakeService().run_retrieval_benchmark(suite="standard", label="nightly", compare_label="baseline")

    assert result["suite"] == "standard"
    assert result["label"] == "nightly"
    assert result["compare_label"] == "baseline"
    assert result["status"] == "completed"
    assert result["recommendations"]["settings_mutated"] is False
    assert result["metrics"]["top1_accuracy"] == 1.0
    assert recorded[0]["suite"] == "standard"
    assert recorded[0]["label"] == "nightly"
    assert recorded[0]["query_count"] == result["query_count"]
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
