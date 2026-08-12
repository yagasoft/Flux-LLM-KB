from __future__ import annotations

import json

import anyio
import pytest

from flux_llm_kb import cli, database, mcp_server
from flux_llm_kb.code_diagnostics import sanitize_code_result
from flux_llm_kb.crawler import AssetChunk
from flux_llm_kb.extractors import ExtractionResult
from flux_llm_kb.service import KnowledgeService


def _local_code_row(*, symbol: str = "OrderService.build_invoice", signature: str = "build_invoice(order_id: str) -> Invoice", diagnostic: str = "parsed without errors"):
    return {
        "source_path": "E:/Private/App/src/orders.py",
        "content_hash": "sha256:orders",
        "symbols": [
            {
                "name": symbol.rsplit(".", 1)[-1],
                "qualified_name": symbol,
                "signature": signature,
                "symbol_kind": "method",
            }
        ],
        "relationships": [
            {
                "source_symbol": symbol,
                "target": "InvoiceRepository.save",
                "relationship": "call",
            }
        ],
        "parser_diagnostics": [{"code": "CS0000", "message": diagnostic}],
        "excerpt": "public Invoice build_invoice(string orderId) => repository.save(orderId);",
    }


def test_named_local_service_projections_preserve_raw_code_facts_and_withhold_each_secret_fact(monkeypatch):
    monkeypatch.setattr(
        database,
        "get_local_source_detail",
        lambda asset_id: {
            "id": asset_id,
            "source_path": "E:/Private/App/src/orders.py",
            "content_hash": "sha256:orders",
            "parser_diagnostics": [{"code": "CS0000", "message": "parsed without errors"}],
            "excerpt": "public sealed class OrderService {}",
        },
    )
    monkeypatch.setattr(
        database,
        "get_local_corpus_detail",
        lambda chunk_id: {
            "id": chunk_id,
            "source_path": "E:/Private/App/src/orders.py",
            "content_hash": "sha256:orders",
            "parser_diagnostics": [{"code": "CS0000", "message": "parsed without errors"}],
            "excerpt": "public sealed class OrderService {}",
        },
    )
    monkeypatch.setattr(database, "search_local_code_details", lambda **_kwargs: [_local_code_row()])

    service = KnowledgeService()
    source = service.local_source_detail("asset-1")
    corpus = service.local_corpus_detail("chunk-1")
    code = service.local_code_search("build_invoice", root_name="app")

    assert source["source_path"] == "E:/Private/App/src/orders.py"
    assert source["content_hash"] == "sha256:orders"
    assert corpus["excerpt"] == "public sealed class OrderService {}"
    assert code["results"][0]["source_path"] == "E:/Private/App/src/orders.py"
    assert code["results"][0]["symbols"][0]["signature"] == "build_invoice(order_id: str) -> Invoice"
    assert code["results"][0]["relationships"][0]["target"] == "InvoiceRepository.save"
    assert code["results"][0]["parser_diagnostics"][0]["message"] == "parsed without errors"

    monkeypatch.setattr(
        database,
        "search_local_code_details",
        lambda **_kwargs: [
            _local_code_row(
                symbol="secret-content-sentinel",
                signature="api_key=secret-content-sentinel",
                diagnostic="secret-content-sentinel",
            )
        ],
    )
    withheld = service.local_code_search("secret")

    assert withheld["results"][0]["symbols"] == []
    assert withheld["results"][0]["parser_diagnostics"] == [{"code": "CS0000", "reason_code": "secret-content-withheld"}]
    assert withheld["results"][0]["excerpt"] is not None
    assert "secret-content-sentinel" not in json.dumps(withheld)

    secret_reference = _local_code_row()
    secret_reference["relationships"] = [{"target": "secret-content-sentinel", "relationship": "call"}]
    monkeypatch.setattr(database, "search_local_code_details", lambda **_kwargs: [secret_reference])
    assert service.local_code_search("build_invoice")["results"][0]["relationships"] == []

    secret_excerpt = {"id": "asset-1", "source_path": "E:/Private/App/src/orders.py", "content_hash": "sha256:orders", "excerpt": "api_key=secret-content-sentinel"}
    monkeypatch.setattr(database, "get_local_source_detail", lambda _asset_id: secret_excerpt)
    assert service.local_source_detail("asset-1")["excerpt"] is None
    assert service.local_source_detail("asset-1")["reason_code"] == "secret-content-withheld"


@pytest.mark.parametrize(
    "synthetic_value",
    [
        "-----BEGIN RSA PRIVATE KEY-----\nsynthetic-key-material\n-----END RSA PRIVATE KEY-----",
        "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-material\n-----END OPENSSH PRIVATE KEY-----",
        "postgresql://synthetic-user:synthetic-password@127.0.0.1/synthetic",
    ],
)
def test_local_and_public_projections_never_emit_private_key_or_credential_uri_values(monkeypatch, synthetic_value):
    from flux_llm_kb.local_detail_projections import project_public_audit_events

    row = _local_code_row(signature=synthetic_value, diagnostic=synthetic_value)
    row["excerpt"] = synthetic_value
    monkeypatch.setattr(database, "search_local_code_details", lambda **_kwargs: [row])

    local = KnowledgeService().local_code_search("synthetic")
    public = project_public_audit_events([{"event_type": "parser.failed", "summary": synthetic_value}])
    local_json = json.dumps(local)
    public_json = json.dumps(public)

    assert local["results"][0]["symbols"] == []
    assert local["results"][0]["parser_diagnostics"] == [
        {"code": "CS0000", "reason_code": "secret-content-withheld"}
    ]
    assert local["results"][0]["excerpt"] is None
    assert local["results"][0]["reason_code"] == "secret-content-withheld"
    assert synthetic_value not in local_json
    assert synthetic_value not in public_json
    assert "secret-content-withheld" in public_json


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_local_detail_adapters_return_the_same_projection_over_rest_cli_and_legacy_mcp(monkeypatch, capsys):
    from fastapi.testclient import TestClient
    from flux_llm_kb.rest_api import create_app

    projection = {"query": "build_invoice", "results": [_local_code_row()]}

    class FakeService:
        def local_source_detail(self, asset_id):
            return {"id": asset_id, **_local_code_row()}

        def local_corpus_detail(self, chunk_id):
            return {"id": chunk_id, **_local_code_row()}

        def local_code_search(self, query, **_kwargs):
            return {**projection, "query": query}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", FakeService)
    app = create_app()
    rest = TestClient(app, client=("127.0.0.1", 50100)).get("/api/local/code/search", params={"query": "build_invoice"})
    source_rest = TestClient(app, client=("127.0.0.1", 50100)).get("/api/local/sources/asset-1")
    corpus_rest = TestClient(app, client=("127.0.0.1", 50100)).get("/api/local/corpus/chunks/chunk-1")
    remote = TestClient(app, client=("192.0.2.20", 50100)).get("/api/local/code/search", params={"query": "build_invoice"})
    forwarded = TestClient(app, client=("127.0.0.1", 50100)).get(
        "/api/local/code/search",
        params={"query": "build_invoice"},
        headers={"X-Forwarded-For": "192.0.2.20"},
    )

    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", FakeService)
    assert cli.main(["local-detail", "code", "build_invoice"]) == 0
    cli_payload = json.loads(capsys.readouterr().out)

    server = mcp_server.create_server(service_factory=FakeService, retry_sleep=lambda _seconds: None)

    async def call_mcp():
        return await server.call_tool("kb.local_code_search", {"query": "build_invoice"})

    mcp_payload = json.loads(anyio.run(call_mcp)[0].text)

    assert rest.status_code == 200
    assert source_rest.json()["source_path"] == "E:/Private/App/src/orders.py"
    assert corpus_rest.json()["content_hash"] == "sha256:orders"
    assert remote.status_code == 403
    assert forwarded.status_code == 403
    assert rest.json()["results"][0] == projection["results"][0]
    assert cli_payload["results"][0] == projection["results"][0]
    assert mcp_payload["results"][0] == projection["results"][0]


def test_shared_code_reader_stays_sanitised_and_has_no_local_detail_switch():
    shared = sanitize_code_result(
        {
            "symbol": "OrderService.build_invoice",
            "path": "E:/Private/App/src/orders.py",
            "signature": "build_invoice(order_id: str) -> Invoice",
        }
    )

    assert shared["path"] == "orders.py"
    assert "signature" not in shared
    assert "local_detail" not in KnowledgeService.code_search.__code__.co_varnames


def test_code_fact_persistence_withholds_secret_facts_before_any_durable_write(monkeypatch):
    """The source and chunk persistence records must never contain a detected code secret."""
    sentinel = "secret-content-sentinel"
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT a.id::text, a.canonical_asset_id" in sql:
                return ("asset-1", None, "root-1", "file:///synthetic.py", 1)
            if "UPDATE source_assets" in sql:
                return ("asset-1",)
            if "SELECT a.path" in sql and "JOIN monitored_roots" in sql:
                return None
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
            return None

    result = ExtractionResult(
        status="indexed",
        metadata={
            "code": {
                "symbols": [{"name": sentinel, "qualified_name": sentinel, "signature": "safe()"}],
                "references": [{"target": sentinel, "relationship_kind": "call"}],
                "parser_diagnostics": {"error_type": "SyntaxError", "message": sentinel},
            }
        },
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="synthetic.py",
                body="def safe(): pass",
                modality="code",
                metadata={
                    "code_symbols": [
                        {"name": sentinel, "qualified_name": sentinel, "signature": "safe()"},
                        {"name": "safe", "qualified_name": "safe", "signature": f"api_key={sentinel}"},
                    ],
                    "code_references": [{"target": sentinel, "relationship_kind": "call"}],
                    "code": {"parser_diagnostics": {"error_type": "SyntaxError", "message": sentinel}},
                },
            ),
        ),
    )

    database._apply_extraction_result_with_cursor(
        FakeCursor(), root_name="synthetic", relative_path="synthetic.py", result=result
    )

    persisted = "\n".join(str(params) for _sql, params in executed)
    assert sentinel not in persisted
    assert not any("INSERT INTO code_symbols" in sql for sql, _params in executed)
    assert not any("INSERT INTO code_references" in sql for sql, _params in executed)


def test_local_detail_readers_use_source_extraction_metadata_for_fallback_diagnostics(monkeypatch):
    """Syntax-fallback diagnostics live in source metadata, even without a symbol row."""
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            return (
                "detail-1",
                "E:/Synthetic",
                "broken.py",
                "sha256:synthetic",
                "def broken(:\n",
                {"code": {"parser_diagnostics": {"error_type": "SyntaxError", "line": 1}}},
            )

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return False

        def cursor(self):
            class CursorContext:
                def __enter__(self):
                    return FakeCursor()

                def __exit__(self, *_args):
                    return False

            return CursorContext()

    monkeypatch.setattr(database, "_load_psycopg", lambda: type("Psycopg", (), {"connect": staticmethod(lambda *_args, **_kwargs: FakeConnection())})())

    source = database.get_local_source_detail("asset-1")
    corpus = database.get_local_corpus_detail("chunk-1")

    assert source["parser_diagnostics"] == [{"error_type": "SyntaxError", "line": 1}]
    assert corpus["parser_diagnostics"] == [{"error_type": "SyntaxError", "line": 1}]
    assert all("a.metadata" in sql for sql, _params in executed)


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_local_code_rest_forwards_dashboard_path_and_generated_filters(monkeypatch):
    from fastapi.testclient import TestClient
    from flux_llm_kb.rest_api import create_app

    calls = []

    class FakeService:
        def local_code_search(self, query, **kwargs):
            calls.append({"query": query, **kwargs})
            return {"query": query, "results": []}

    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", FakeService)
    response = TestClient(create_app(), client=("127.0.0.1", 50100)).get(
        "/api/local/code/search",
        params={"query": "build_invoice", "path_glob": "src/*.py", "include_generated": "true"},
    )

    assert response.status_code == 200
    assert calls == [
        {
            "query": "build_invoice",
            "root_name": None,
            "cwd": None,
            "language": None,
            "relationship": None,
            "path_glob": "src/*.py",
            "include_generated": True,
            "limit": 20,
        }
    ]


def test_code_metadata_records_bounded_withholding_evidence_for_each_fact_kind():
    """A withheld fact is absent, but its durable count and fixed reason remain."""
    sentinel = "secret-content-sentinel"
    safe = database._sanitize_code_metadata_for_persistence(
        {
            "code_symbols": [
                {"name": sentinel, "qualified_name": sentinel, "signature": "safe()"},
                {"name": "safe", "qualified_name": "safe", "signature": "safe()"},
            ],
            "code_references": [
                {"source_symbol": "safe", "target": sentinel, "relationship_kind": "call"},
                {"source_symbol": "safe", "target": "kept", "relationship_kind": "call"},
            ],
            "code": {
                "symbols": [{"name": "signature_only", "qualified_name": "signature_only", "signature": f"api_key={sentinel}"}],
                "references": [{"source_symbol": "safe", "target": sentinel, "relationship_kind": "call"}],
                "parser_diagnostics": [{"code": "CS1002", "message": sentinel}],
            },
        }
    )

    assert safe["code_symbols"] == [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}]
    assert safe["code_references"] == [{"source_symbol": "safe", "target": "kept", "relationship_kind": "call"}]
    assert safe["code"]["symbols"] == []
    assert safe["code"]["references"] == []
    assert safe["code"]["parser_diagnostics"] == [{"code": "CS1002", "reason_code": "secret-content-withheld"}]
    assert safe["code"]["withheld"] == {
        "reason_code": "secret-content-withheld",
        "symbol_count": 2,
        "reference_count": 2,
        "diagnostic_count": 1,
    }

    chunk_safe = database._sanitize_code_metadata_for_persistence(
        {"code_symbols": [{"name": sentinel, "qualified_name": sentinel, "signature": "safe()"}]}
    )
    assert chunk_safe == {
        "code_symbols": [],
        "code": {
            "withheld": {
                "reason_code": "secret-content-withheld",
                "symbol_count": 1,
                "reference_count": 0,
                "diagnostic_count": 0,
            }
        },
    }


def test_code_metadata_blocks_oversized_unscannable_fact_without_retaining_partial_facts():
    """A scan-bound failure is an explicit completion block, never a quiet omission."""
    with pytest.raises(ValueError, match="code-fact-scan-failed"):
        database._sanitize_code_metadata_for_persistence(
            {
                "code": {
                    "symbols": [
                        {"name": "safe", "qualified_name": "safe", "signature": "safe()"},
                        {"name": "too-large", "qualified_name": "too-large", "signature": "x" * ((16 * 1024) + 1)},
                    ]
                }
            }
        )


def test_container_child_source_asset_write_scans_code_metadata_before_json_parameterisation():
    """Container child metadata follows the same durable boundary as ordinary assets."""
    sentinel = "secret-content-sentinel"
    executed = []

    class FakeCursor:
        def execute(self, sql, params=()):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT id::text\n            FROM source_assets" in sql:
                return None
            if "INSERT INTO source_assets" in sql:
                return ("child-1",)
            if "SELECT a.path, r.name, r.root_path" in sql:
                return None
            if "INSERT INTO asset_chunks" in sql:
                return ("chunk-1",)
            return None

    child = type("Child", (), {
        "member_path": "child.py",
        "file_kind": "code",
        "mime_type": "text/x-python",
        "extension": ".py",
        "size_bytes": 12,
        "quick_hash": "quick",
        "content_hash": "hash",
        "extraction_tier": "inline",
        "extraction_status": "indexed",
        "metadata": {"code": {"symbols": [{"name": sentinel, "qualified_name": sentinel, "signature": "safe()"}]}},
        "chunks": (),
    })()

    database._replace_container_child_assets(
        FakeCursor(),
        root_id="root-1",
        parent_asset_id="parent-1",
        parent_relative_path="bundle.zip",
        parent_uri="file:///bundle.zip",
        parent_mtime_ns=1,
        child_assets=(child,),
    )

    source_asset_parameters = [params for sql, params in executed if "INSERT INTO source_assets" in sql]
    assert len(source_asset_parameters) == 1
    assert sentinel not in str(source_asset_parameters[0])
    assert "secret-content-withheld" in str(source_asset_parameters[0])
