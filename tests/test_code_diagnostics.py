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
        lambda **kwargs: [
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

    service = KnowledgeService()
    status = service.code_status(root_name="app")
    search = service.code_search("build_invoice", root_name="app", language="python", limit=5)
    symbol = service.code_symbol_lookup("OrderService.build_invoice", root_name="app", include_references=True)

    assert status["totals"]["symbol_count"] == 1
    assert search["results"][0]["symbol"] == "OrderService.build_invoice"
    assert symbol["references"][0]["relationship"] == "call"
    assert json.dumps(search).find("E:/private") == -1
