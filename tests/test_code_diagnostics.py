from __future__ import annotations

import json

from flux_llm_kb import database
from flux_llm_kb.code_diagnostics import build_code_status_report
from flux_llm_kb.service import KnowledgeService


def test_code_status_report_summarizes_coverage_without_private_paths():
    report = build_code_status_report(
        roots=[
            {
                "root_name": "app",
                "asset_count": 5,
                "chunk_count": 8,
                "symbol_count": 6,
                "reference_count": 9,
                "languages": {"python": 3, "typescript": 2},
                "parser_statuses": {"parsed": 4, "fallback": 1},
                "generated_count": 1,
                "fallback_count": 1,
                "slow_files": [{"path": "E:/private/repo/src/broken.py", "duration_ms": 1200}],
            }
        ],
        totals={"asset_count": 5, "chunk_count": 8, "symbol_count": 6, "reference_count": 9},
    )

    assert report["settings_mutated"] is False
    assert report["totals"]["symbol_count"] == 6
    assert report["roots"][0]["root_name"] == "app"
    assert report["roots"][0]["parser_statuses"]["fallback"] == 1
    assert report["roots"][0]["health"] == "partial"
    serialized = json.dumps(report).lower()
    assert "e:/private" not in serialized
    assert "broken.py" in serialized


def test_service_code_search_and_symbol_lookup_use_database_helpers(monkeypatch):
    search_calls = []
    monkeypatch.setattr(
        database,
        "code_index_status",
        lambda **kwargs: {
            "roots": [{"root_name": "app", "asset_count": 1, "symbol_count": 1, "reference_count": 1}],
            "totals": {"asset_count": 1, "symbol_count": 1, "reference_count": 1},
        },
    )
    monkeypatch.setattr(
        database,
        "search_code_symbols",
        lambda **kwargs: search_calls.append(kwargs)
        or [
            {
                "symbol": "OrderService.build_invoice",
                "symbol_kind": "method",
                "language": "python",
                "path": "src/orders.py",
                "line_start": 5,
                "line_end": 7,
                "relationship": "definition",
                "parser_status": "parsed",
                "is_generated": False,
                "target_symbol": "OrderService.build_invoice",
            }
        ],
    )
    monkeypatch.setattr(
        database,
        "lookup_code_symbol",
        lambda **kwargs: {
            "query": kwargs["symbol"],
            "matches": [
                {
                    "symbol": "OrderService.build_invoice",
                    "symbol_kind": "method",
                    "language": "python",
                    "path": "src/orders.py",
                    "line_start": 5,
                    "line_end": 7,
                    "relationship": "definition",
                    "parser_status": "parsed",
                }
            ],
            "references": [
                {
                    "target": "OrderService.build_invoice",
                    "relationship": "call",
                    "language": "python",
                    "path": "tests/test_orders.py",
                    "line_start": 4,
                    "line_end": 4,
                    "parser_status": "parsed",
                }
            ],
        },
    )
    monkeypatch.setattr(
        database,
        "code_feedback_summary",
        lambda **kwargs: {
            "settings_mutated": False,
            "rows": [{"miss_category": "missing_symbol", "root_name": "app", "event_count": 2}],
            "totals": {"event_count": 2},
        },
    )
    monkeypatch.setattr(
        database,
        "list_retrieval_benchmark_runs",
        lambda **kwargs: [
            {
                "id": "retrieval-run-1",
                "suite": "standard",
                "failed_count": 2,
                "case_results": [
                    {
                        "case_id": "code-route",
                        "category": "code_route",
                        "status": "failed",
                        "reasons": ["top1_miss", "recall_miss"],
                    },
                    {
                        "case_id": "code-generated",
                        "category": "code_generated_suppression",
                        "status": "failed",
                        "reasons": ["top1_miss"],
                    },
                    {
                        "case_id": "current-only",
                        "category": "current_only",
                        "status": "failed",
                        "reasons": ["scope_miss"],
                    },
                ],
            }
        ],
    )
    feedback_calls = []
    monkeypatch.setattr(database, "record_code_feedback_event", lambda **kwargs: feedback_calls.append(kwargs) or {"id": "feedback-1", "miss_category": kwargs["miss_category"]})

    service = KnowledgeService()
    status = service.code_status(root_name="app")
    search = service.code_search("build_invoice", root_name="app", language="python", relationship="call", path_glob="src/*.py", include_generated=True, limit=5)
    symbol = service.code_symbol_lookup("OrderService.build_invoice", root_name="app", include_references=True)
    feedback = service.record_code_feedback(
        query="build invoice",
        root_name="app",
        result_count=0,
        surface="cli",
        miss_category="missing_symbol",
        expected_symbol="OrderService.build_invoice",
        path="E:/private/app/src/orders.py",
        metadata={"note": "safe"},
    )
    summary = service.code_feedback_summary(root_name="app")

    assert status["totals"]["symbol_count"] == 1
    assert status["feedback_summary"]["totals"]["event_count"] == 2
    assert status["gaps"][0]["category"] == "missing_symbol"
    assert status["retrieval_benchmark_summary"]["failed_count"] == 2
    assert any(gap["category"] == "benchmark_code_route" and gap["count"] == 1 for gap in status["gaps"])
    assert any(gap["category"] == "benchmark_code_generated_suppression" and gap["count"] == 1 for gap in status["gaps"])
    assert search["results"][0]["symbol"] == "OrderService.build_invoice"
    assert search["results"][0]["is_generated"] is False
    assert search_calls[0]["path_glob"] == "src/*.py"
    assert search_calls[0]["include_generated"] is True
    assert search_calls[0]["relationship"] == "call"
    assert symbol["references"][0]["relationship"] == "call"
    assert feedback["id"] == "feedback-1"
    assert feedback_calls[0]["miss_category"] == "missing_symbol"
    assert summary["rows"][0]["miss_category"] == "missing_symbol"
    assert json.dumps(search).find("E:/private") == -1
