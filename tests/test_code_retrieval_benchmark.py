import json

from flux_llm_kb import database
from flux_llm_kb import service as service_module
from flux_llm_kb.crawler import CorpusPolicy, scan_path
from flux_llm_kb.service import KnowledgeService


def test_code_heavy_benchmark_fixture_covers_code_indexing_paths(tmp_path):
    root = tmp_path / "code-heavy"
    service_module._write_benchmark_fixture(root, "code-heavy", 15)

    plan = scan_path(root, CorpusPolicy(root_path=root))
    assets = {asset.relative_path: asset for asset in plan.assets}

    assert len(assets) == 15
    assert set(assets) == {
        "db/migrations/0001_create_orders.sql",
        "duplicates/orders_copy.py",
        "generated/client.py",
        "notebooks/orders.ipynb",
        "openapi.yaml",
        "pyproject.toml",
        "src/OrderService.cs",
        "src/broken.py",
        "src/lib.rs",
        "src/main.go",
        "src/orders.py",
        "src/unsupported.go",
        "tests/test_orders.py",
        "tools/orders.ps1",
        "web/routes.ts",
    }
    assert all(asset.file_kind == "code" for asset in assets.values())

    orders = assets["src/orders.py"]
    assert orders.metadata["code"]["parser_status"] == "parsed"
    assert any(symbol["qualified_name"] == "OrderService.build_invoice" for symbol in orders.metadata["code"]["symbols"])
    assert any(symbol["qualified_name"] == "get_order" and symbol["metadata"]["routes"] == ["/orders/{order_id}"] for symbol in orders.metadata["code"]["symbols"])

    routes = assets["web/routes.ts"]
    assert any(
        reference["relationship_kind"] == "call" and reference["target"] == "renderOrder" and reference["source_symbol"] == "buildOrder"
        for reference in routes.metadata["code"]["references"]
    )
    assert any(
        reference["relationship_kind"] == "route" and reference["target"] == "/api/orders/:orderId" and reference["source_symbol"] == "buildOrder"
        for reference in routes.metadata["code"]["references"]
    )

    tests = assets["tests/test_orders.py"]
    assert any(symbol["symbol_kind"] == "fixture" and symbol["qualified_name"] == "order_service" for symbol in tests.metadata["code"]["symbols"])
    assert any(symbol["symbol_kind"] == "test" and symbol["qualified_name"] == "test_build_invoice_returns_ready_status" for symbol in tests.metadata["code"]["symbols"])
    assert any(reference["relationship_kind"] == "fixture" and reference["target"] == "order_service" for reference in tests.metadata["code"]["references"])

    migration = assets["db/migrations/0001_create_orders.sql"]
    assert any(symbol["symbol_kind"] == "table" and symbol["name"] == "orders" for symbol in migration.metadata["code"]["symbols"])
    assert any(symbol["symbol_kind"] == "index" and symbol["name"] == "idx_orders_status" for symbol in migration.metadata["code"]["symbols"])

    generated = assets["generated/client.py"]
    assert generated.metadata["code"]["generated"] is True
    assert all(chunk.metadata["code"]["generated"] is True for chunk in generated.chunks)

    broken = assets["src/broken.py"]
    assert broken.metadata["code"]["parser_status"] == "fallback"
    assert "ops@example.com" not in broken.chunks[0].body
    assert "[REDACTED:email]" in broken.chunks[0].body

    unsupported = assets["src/unsupported.go"]
    assert unsupported.metadata["code"]["language"] == "go"
    assert unsupported.metadata["code"]["parser_status"] == "fallback"

    language_symbols = {
        "src/main.go": ("go", "BuildInvoice"),
        "src/lib.rs": ("rust", "build_invoice"),
        "src/OrderService.cs": ("csharp", "OrderService"),
        "tools/orders.ps1": ("powershell", "Invoke-BuildInvoice"),
    }
    for path, (language, symbol_name) in language_symbols.items():
        code = assets[path].metadata["code"]
        assert code["language"] == language
        assert code["parser_status"] == "parsed"
        assert any(symbol["name"] == symbol_name for symbol in code["symbols"])

    notebook = assets["notebooks/orders.ipynb"]
    assert [chunk.metadata["cell_type"] for chunk in notebook.chunks] == ["markdown", "code"]

    assert orders.content_hash == assets["duplicates/orders_copy.py"].content_hash
    serialized_metadata = json.dumps([asset.metadata for asset in assets.values()], sort_keys=True)
    assert str(tmp_path) not in serialized_metadata


def test_code_heavy_benchmark_runs_through_public_service_surface(monkeypatch):
    recorded = []
    monkeypatch.setattr(database, "record_benchmark_run", lambda **kwargs: recorded.append(kwargs) or {"id": "run-code", "fixture": kwargs["fixture"]})

    result = KnowledgeService().run_benchmark(fixture="code-heavy", files=15, mode="scan")

    assert result["fixture"] == "code-heavy"
    assert result["runs"][0]["fixture"] == "code-heavy"
    assert recorded[0]["fixture"] == "code-heavy"
    assert recorded[0]["file_count"] == 15
    assert recorded[0]["worker_family_breakdown"]["text"]["files"] == 15
    assert "root_path" not in recorded[0]["metadata"]
