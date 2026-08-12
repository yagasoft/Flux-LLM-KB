import json
import os
from types import SimpleNamespace
from uuid import uuid4

import anyio
import pytest

from flux_llm_kb import cli, database, gpu_scheduler, mail_ingestion, mcp_server
from flux_llm_kb.crawler import AssetChunk, CrawlPlan, DiscoveredAsset
from flux_llm_kb.database import forget_episode, insert_episode, run_migrations, search_episodes
from flux_llm_kb.embeddings import EmbeddingResult
from flux_llm_kb.extractors import ContainerChildAsset, ExtractionResult
from flux_llm_kb.local_detail_projections import project_local_source_detail
from flux_llm_kb.service import KnowledgeService


TEST_DATABASE_URL = os.environ.get("FLUX_KB_TEST_DATABASE_URL")

if not TEST_DATABASE_URL:
    pytest.skip("FLUX_KB_TEST_DATABASE_URL is not set", allow_module_level=True)


def _stale_code_fact_metadata(marker: str) -> dict[str, object]:
    """Return safe historical facts that a later blocked transition must retract."""
    return {
        "preserved_transition_marker": marker,
        "code_symbols": [
            {
                "name": f"stale_{marker}_symbol",
                "qualified_name": f"stale_{marker}_symbol",
                "signature": f"stale_{marker}_symbol()",
            }
        ],
        "code_references": [
            {
                "source_symbol": f"stale_{marker}_symbol",
                "target": f"stale_{marker}_target",
                "relationship_kind": "call",
            }
        ],
        "code": {
            "symbols": [{"name": f"stale_{marker}_nested_symbol"}],
            "references": [{"target": f"stale_{marker}_nested_target"}],
            "parser_diagnostics": [{"code": "STALE0001", "message": f"stale_{marker}_diagnostic"}],
        },
    }


def _assert_blocked_code_fact_metadata(metadata: dict[str, object], marker: str) -> None:
    assert metadata["preserved_transition_marker"] == marker
    assert "code_symbols" not in metadata
    assert "code_references" not in metadata
    assert metadata["code"] == {
        "scan_status": "blocked",
        "reason_code": "code-fact-scan-failed",
    }
    assert f"stale_{marker}" not in str(metadata)


def test_postgres_hybrid_search_smoke():
    run_migrations(TEST_DATABASE_URL)
    marker = f"postgres-search-smoke-{uuid4()}"
    episode_id = insert_episode(
        title=f"Hybrid retrieval {marker}",
        summary="PostgreSQL full text and pg_trgm fuzzy search are enabled for degraded retrieval.",
        url=TEST_DATABASE_URL,
    )

    results = search_episodes(marker, limit=5, url=TEST_DATABASE_URL)

    assert any(result["id"] == episode_id for result in results)
    assert any({"lexical", "fuzzy"} & set(result["streams"]) for result in results)
    assert forget_episode(episode_id, url=TEST_DATABASE_URL) is True


def test_postgres_corpus_sync_search_and_watch_state(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    marker = f"corpus-smoke-{uuid4()}"
    root = tmp_path / "corpus"
    root.mkdir()
    (root / "decision.md").write_text(f"{marker} says dashboard health is unified.", encoding="utf-8")
    name = f"corpus-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, watch_enabled=True, url=TEST_DATABASE_URL)

    try:
        sync_result = KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=5, url=TEST_DATABASE_URL)
        status = database.crawl_status(url=TEST_DATABASE_URL)

        assert sync_result["files_seen"] == 1
        assert sync_result["chunks_indexed"] == 1
        assert any(result["source_path"] == "decision.md" for result in results)
        assert status["active_watch_roots"] >= 1
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_code_fact_secret_boundary_withholds_every_durable_shape(tmp_path):
    """Synthetic code facts are scanned before source metadata, chunk metadata, and fact rows persist."""
    run_migrations(TEST_DATABASE_URL)
    sentinel = "secret-content-sentinel"
    name = f"code-secret-boundary-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    asset = DiscoveredAsset(
        path=tmp_path / "synthetic.py",
        relative_path="synthetic.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-synthetic",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="inline",
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="synthetic.py",
                body="def safe(): pass",
                modality="code",
                metadata={
                    "code_symbols": [
                        {"name": sentinel, "qualified_name": sentinel, "signature": "safe()"},
                        {"name": "leaking_symbol", "qualified_name": "leaking_symbol", "signature": f"api_key={sentinel}"},
                        {"name": "retained_symbol", "qualified_name": "retained_symbol", "signature": "retained_symbol()"},
                    ],
                    "code_references": [
                        {"source_symbol": "retained_symbol", "target": sentinel, "relationship_kind": "call"},
                        {"source_symbol": "retained_symbol", "target": "safe_target", "relationship_kind": "call"},
                    ],
                    "code": {"parser_diagnostics": {"error_type": "SyntaxError", "message": sentinel}},
                },
            ),
        ),
        metadata={
            "code": {
                "symbols": [
                    {"name": sentinel, "qualified_name": sentinel, "signature": "safe()"},
                    {"name": "leaking_symbol", "qualified_name": "leaking_symbol", "signature": f"api_key={sentinel}"},
                    {"name": "retained_symbol", "qualified_name": "retained_symbol", "signature": "retained_symbol()"},
                ],
                "references": [
                    {"source_symbol": "retained_symbol", "target": sentinel, "relationship_kind": "call"},
                    {"source_symbol": "retained_symbol", "target": "safe_target", "relationship_kind": "call"},
                ],
                "parser_diagnostics": {"error_type": "SyntaxError", "message": sentinel},
            }
        },
    )
    try:
        database.persist_crawl_plan(
            root_name=name,
            plan=CrawlPlan(root_path=tmp_path, assets=[asset]),
            url=TEST_DATABASE_URL,
        )
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT a.id::text, a.metadata::text, c.metadata::text
                    FROM source_assets a
                    JOIN monitored_roots r ON r.id = a.root_id
                    JOIN asset_chunks c ON c.asset_id = a.id
                    WHERE r.name = %s AND a.path = 'synthetic.py'
                    """,
                    (name,),
                )
                asset_id, source_metadata, chunk_metadata = cur.fetchone()
                cur.execute(
                    """
                    SELECT coalesce(string_agg(concat_ws('|', name, qualified_name, signature, metadata::text), E'\n'), '')
                    FROM code_symbols
                    WHERE source_asset_id = %s
                    """,
                    (asset_id,),
                )
                symbols_text = cur.fetchone()[0]
                cur.execute(
                    """
                    SELECT coalesce(string_agg(concat_ws('|', source_symbol, target, metadata::text), E'\n'), '')
                    FROM code_references
                    WHERE source_asset_id = %s
                    """,
                    (asset_id,),
                )
                references_text = cur.fetchone()[0]

        assert sentinel not in "\n".join((source_metadata, chunk_metadata, symbols_text, references_text))
        assert "leaking_symbol" not in "\n".join((source_metadata, chunk_metadata, symbols_text))
        assert "retained_symbol" in source_metadata
        assert "retained_symbol" in chunk_metadata
        assert "retained_symbol" in symbols_text
        assert "safe_target" in references_text
        detail = project_local_source_detail(database.get_local_source_detail(asset_id, url=TEST_DATABASE_URL)).as_dict()
        assert detail["parser_diagnostics"] == [{"code": "SyntaxError", "reason_code": "secret-content-withheld"}]
        assert '"symbol_count": 2' in source_metadata
        assert '"reference_count": 1' in source_metadata
        assert '"diagnostic_count": 1' in source_metadata
        assert '"symbol_count": 2' in chunk_metadata
        assert '"reference_count": 1' in chunk_metadata
        assert '"diagnostic_count": 1' in chunk_metadata
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_container_child_and_staged_writes_persist_only_withheld_code_fact_evidence(tmp_path):
    """Real source-asset writes cover ordinary, child-container, and staged persistence paths."""
    run_migrations(TEST_DATABASE_URL)
    sentinel = "secret-content-sentinel"
    name = f"code-child-staged-boundary-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    parent = DiscoveredAsset(
        path=tmp_path / "bundle.zip",
        relative_path="bundle.zip",
        file_kind="archive",
        mime_type="application/zip",
        extension=".zip",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-parent",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="metadata_only",
    )
    staged = DiscoveredAsset(
        path=tmp_path / "staged.py",
        relative_path="staged.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-staged",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="deferred",
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[parent, staged]), url=TEST_DATABASE_URL)
        child = ContainerChildAsset(
            member_path="child.py",
            file_kind="code",
            mime_type="text/x-python",
            extension=".py",
            size_bytes=12,
            quick_hash="quick-child",
            content_hash=f"sha256:{uuid4().hex}",
            extraction_tier="inline",
            extraction_status="indexed",
            metadata={
                "code": {
                    "symbols": [{"name": sentinel, "qualified_name": sentinel, "signature": "safe()"}],
                    "references": [{"source_symbol": "safe", "target": sentinel, "relationship_kind": "call"}],
                    "parser_diagnostics": [{"code": "CS1002", "message": sentinel}],
                }
            },
        )
        database.apply_extraction_result(
            root_name=name,
            relative_path="bundle.zip",
            result=ExtractionResult(status="metadata_only", metadata={"extractor": "container"}, child_assets=(child,)),
            url=TEST_DATABASE_URL,
        )
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT id::text FROM capture_jobs WHERE job_type = 'corpus_extract_code' AND payload->>'path' = 'staged.py'"
                )
                job_id = cur.fetchone()[0]
                cur.execute("UPDATE capture_jobs SET status = 'running' WHERE id = %s", (job_id,))
        applied = database.apply_staged_extraction_piece_for_job(
            job_id=job_id,
            root_name=name,
            relative_path="staged.py",
            result=SimpleNamespace(
                status="indexed",
                metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": f"api_key={sentinel}"}]}},
                chunks=(),
            ),
            url=TEST_DATABASE_URL,
        )
        assert applied is True
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.path, a.metadata::text FROM source_assets a JOIN monitored_roots r ON r.id = a.root_id WHERE r.name = %s ORDER BY a.path",
                    (name,),
                )
                rows = cur.fetchall()
        stored = "\n".join(metadata for _path, metadata in rows)
        assert sentinel not in stored
        assert all('"reason_code": "secret-content-withheld"' in metadata for _path, metadata in rows if _path != "bundle.zip")
        assert '"symbol_count": 1' in stored
        assert '"reference_count": 1' in stored
        assert '"diagnostic_count": 1' in stored
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_oversized_code_fact_blocks_completion_without_partial_fact_write(tmp_path):
    """The generated store records the fixed blocked outcome and no code fact or chunk."""
    run_migrations(TEST_DATABASE_URL)
    name = f"code-scan-failure-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    asset = DiscoveredAsset(
        path=tmp_path / "oversized.py",
        relative_path="oversized.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-oversized",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="metadata_only",
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[asset]), url=TEST_DATABASE_URL)
        database.apply_extraction_result(
            root_name=name,
            relative_path="oversized.py",
            result=ExtractionResult(
                status="indexed",
                metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
                chunks=(AssetChunk(chunk_index=0, title="oversized.py", body="safe", modality="code"),),
            ),
            url=TEST_DATABASE_URL,
        )
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.id::text, a.extraction_status, a.metadata::text, count(c.id)::integer FROM source_assets a JOIN monitored_roots r ON r.id = a.root_id LEFT JOIN asset_chunks c ON c.asset_id = a.id WHERE r.name = %s GROUP BY a.id, a.extraction_status, a.metadata",
                    (name,),
                )
                asset_id, status, metadata, chunk_count = cur.fetchone()
                cur.execute("SELECT count(*) FROM code_symbols WHERE source_asset_id = %s", (asset_id,))
                symbol_count = cur.fetchone()[0]
        assert status == "blocked_by_policy"
        assert '"reason_code": "code-fact-scan-failed"' in metadata
        assert "x" * 128 not in metadata
        assert chunk_count == 0
        assert symbol_count == 0
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


@pytest.mark.parametrize(
    "apply_stage",
    [database.apply_staged_extraction_plan_for_job, database.apply_staged_extraction_piece_for_job],
    ids=["plan", "piece"],
)
def test_postgres_staged_scan_failure_retracts_prior_piece_facts_before_blocking(tmp_path, apply_stage):
    """A later scan-bound failure retracts every fact persisted by an earlier staged piece."""
    run_migrations(TEST_DATABASE_URL)
    name = f"staged-code-fact-retraction-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    asset = DiscoveredAsset(
        path=tmp_path / "staged.py",
        relative_path="staged.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-staged-retraction",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="deferred",
        metadata=_stale_code_fact_metadata("staged"),
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[asset]), url=TEST_DATABASE_URL)
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT id::text FROM capture_jobs WHERE job_type = 'corpus_extract_code' AND payload->>'path' = 'staged.py'"
                )
                job_id = cur.fetchone()[0]
                cur.execute("UPDATE capture_jobs SET status = 'running' WHERE id = %s", (job_id,))

        assert apply_stage(
            job_id=job_id,
            root_name=name,
            relative_path="staged.py",
            result=SimpleNamespace(
                status="staged",
                metadata={},
                chunks=(
                    AssetChunk(
                        chunk_index=0,
                        title="staged.py",
                        body="def safe(): pass",
                        modality="code",
                        metadata={
                            "code_symbols": [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}],
                            "code_references": [{"source_symbol": "safe", "target": "target", "relationship_kind": "call"}],
                        },
                    ),
                ),
            ),
            url=TEST_DATABASE_URL,
        )

        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.id::text, count(c.id)::integer, count(s.id)::integer, count(r.id)::integer "
                    "FROM source_assets a "
                    "LEFT JOIN asset_chunks c ON c.asset_id = a.id "
                    "LEFT JOIN code_symbols s ON s.source_asset_id = a.id "
                    "LEFT JOIN code_references r ON r.source_asset_id = a.id "
                    "JOIN monitored_roots root ON root.id = a.root_id "
                    "WHERE root.name = %s AND a.path = 'staged.py' "
                    "GROUP BY a.id",
                    (name,),
                )
                _asset_id, chunks_before, symbols_before, references_before = cur.fetchone()
        assert chunks_before == symbols_before == references_before == 1

        assert apply_stage(
            job_id=job_id,
            root_name=name,
            relative_path="staged.py",
            result=SimpleNamespace(
                status="indexed",
                metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
                chunks=(),
            ),
            url=TEST_DATABASE_URL,
        )

        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.extraction_status, a.metadata, manifest.metadata, count(c.id)::integer, count(s.id)::integer, count(r.id)::integer "
                    "FROM source_assets a "
                    "LEFT JOIN asset_chunks c ON c.asset_id = a.id "
                    "LEFT JOIN code_symbols s ON s.source_asset_id = a.id "
                    "LEFT JOIN code_references r ON r.source_asset_id = a.id "
                    "JOIN monitored_roots root ON root.id = a.root_id "
                    "JOIN crawl_path_manifests manifest ON manifest.root_id = a.root_id AND manifest.path = a.path "
                    "WHERE root.name = %s AND a.path = 'staged.py' "
                    "GROUP BY a.extraction_status, a.metadata, manifest.metadata",
                    (name,),
                )
                status, metadata, manifest_metadata, chunks, symbols, references = cur.fetchone()
        assert status == "blocked_by_policy"
        _assert_blocked_code_fact_metadata(metadata, "staged")
        _assert_blocked_code_fact_metadata(manifest_metadata, "staged")
        assert chunks == symbols == references == 0
    finally:
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_direct_scan_failure_retracts_prior_indexed_facts(tmp_path):
    """A direct completion clears a formerly indexed fact set when its replacement is unscannable."""
    run_migrations(TEST_DATABASE_URL)
    name = f"direct-code-fact-retraction-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    asset = DiscoveredAsset(
        path=tmp_path / "direct.py",
        relative_path="direct.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-direct-retraction",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="metadata_only",
        metadata=_stale_code_fact_metadata("direct"),
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[asset]), url=TEST_DATABASE_URL)
        database.apply_extraction_result(
            root_name=name,
            relative_path="direct.py",
            result=ExtractionResult(
                status="indexed",
                chunks=(
                    AssetChunk(
                        chunk_index=0,
                        title="direct.py",
                        body="def safe(): pass",
                        modality="code",
                        metadata={
                            "code_symbols": [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}],
                            "code_references": [{"source_symbol": "safe", "target": "target", "relationship_kind": "call"}],
                        },
                    ),
                ),
            ),
            url=TEST_DATABASE_URL,
        )
        database.apply_extraction_result(
            root_name=name,
            relative_path="direct.py",
            result=ExtractionResult(
                status="indexed",
                metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
            ),
            url=TEST_DATABASE_URL,
        )
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.extraction_status, a.indexed_at, a.metadata, manifest.metadata, count(c.id)::integer, count(s.id)::integer, count(r.id)::integer "
                    "FROM source_assets a "
                    "LEFT JOIN asset_chunks c ON c.asset_id = a.id "
                    "LEFT JOIN code_symbols s ON s.source_asset_id = a.id "
                    "LEFT JOIN code_references r ON r.source_asset_id = a.id "
                    "JOIN monitored_roots root ON root.id = a.root_id "
                    "JOIN crawl_path_manifests manifest ON manifest.root_id = a.root_id AND manifest.path = a.path "
                    "WHERE root.name = %s AND a.path = 'direct.py' "
                    "GROUP BY a.extraction_status, a.indexed_at, a.metadata, manifest.metadata",
                    (name,),
                )
                status, indexed_at, metadata, manifest_metadata, chunks, symbols, references = cur.fetchone()
        assert status == "blocked_by_policy"
        assert indexed_at is None
        _assert_blocked_code_fact_metadata(metadata, "direct")
        _assert_blocked_code_fact_metadata(manifest_metadata, "direct")
        assert chunks == symbols == references == 0
    finally:
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_unchanged_rescan_scan_failure_retracts_prior_indexed_facts(tmp_path):
    """An unchanged asset cannot retain an indexed fact set after its new metadata is unscannable."""
    run_migrations(TEST_DATABASE_URL)
    name = f"unchanged-code-fact-retraction-{uuid4()}"
    content_hash = f"sha256:{uuid4().hex}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    safe_asset = DiscoveredAsset(
        path=tmp_path / "unchanged.py",
        relative_path="unchanged.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-unchanged-retraction",
        content_hash=content_hash,
        extraction_tier="inline",
        metadata=_stale_code_fact_metadata("unchanged"),
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="unchanged.py",
                body="def safe(): pass",
                modality="code",
                metadata={
                    "code_symbols": [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}],
                    "code_references": [{"source_symbol": "safe", "target": "target", "relationship_kind": "call"}],
                },
            ),
        ),
    )
    blocked_asset = DiscoveredAsset(
        path=tmp_path / "unchanged.py",
        relative_path="unchanged.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-unchanged-retraction",
        content_hash=content_hash,
        extraction_tier="inline",
        metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[safe_asset]), url=TEST_DATABASE_URL)
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[blocked_asset]), url=TEST_DATABASE_URL)
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.extraction_status, a.metadata, manifest.metadata, count(c.id)::integer, count(s.id)::integer, count(r.id)::integer "
                    "FROM source_assets a "
                    "LEFT JOIN asset_chunks c ON c.asset_id = a.id "
                    "LEFT JOIN code_symbols s ON s.source_asset_id = a.id "
                    "LEFT JOIN code_references r ON r.source_asset_id = a.id "
                    "JOIN monitored_roots root ON root.id = a.root_id "
                    "JOIN crawl_path_manifests manifest ON manifest.root_id = a.root_id AND manifest.path = a.path "
                    "WHERE root.name = %s AND a.path = 'unchanged.py' "
                    "GROUP BY a.extraction_status, a.metadata, manifest.metadata",
                    (name,),
                )
                status, metadata, manifest_metadata, chunks, symbols, references = cur.fetchone()
        assert status == "blocked_by_policy"
        _assert_blocked_code_fact_metadata(metadata, "unchanged")
        _assert_blocked_code_fact_metadata(manifest_metadata, "unchanged")
        assert chunks == symbols == references == 0
    finally:
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_changed_crawl_scan_failure_retracts_prior_indexed_fact_metadata(tmp_path):
    """A changed crawl asset retracts stale source and manifest facts when the new facts cannot be scanned."""
    run_migrations(TEST_DATABASE_URL)
    name = f"changed-code-fact-retraction-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    safe_asset = DiscoveredAsset(
        path=tmp_path / "changed.py",
        relative_path="changed.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-changed-safe",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="inline",
        metadata=_stale_code_fact_metadata("changed"),
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="changed.py",
                body="def safe(): pass",
                modality="code",
                metadata={
                    "code_symbols": [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}],
                    "code_references": [{"source_symbol": "safe", "target": "target", "relationship_kind": "call"}],
                },
            ),
        ),
    )
    blocked_asset = DiscoveredAsset(
        path=tmp_path / "changed.py",
        relative_path="changed.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=17,
        mtime_ns=2,
        quick_hash="quick-changed-blocked",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="inline",
        metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[safe_asset]), url=TEST_DATABASE_URL)
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[blocked_asset]), url=TEST_DATABASE_URL)
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT a.extraction_status, a.metadata, manifest.metadata, count(c.id)::integer, "
                    "count(s.id)::integer, count(r.id)::integer "
                    "FROM source_assets a "
                    "LEFT JOIN asset_chunks c ON c.asset_id = a.id "
                    "LEFT JOIN code_symbols s ON s.source_asset_id = a.id "
                    "LEFT JOIN code_references r ON r.source_asset_id = a.id "
                    "JOIN monitored_roots root ON root.id = a.root_id "
                    "JOIN crawl_path_manifests manifest ON manifest.root_id = a.root_id AND manifest.path = a.path "
                    "WHERE root.name = %s AND a.path = 'changed.py' "
                    "GROUP BY a.extraction_status, a.metadata, manifest.metadata",
                    (name,),
                )
                status, metadata, manifest_metadata, chunks, symbols, references = cur.fetchone()
        assert status == "blocked_by_policy"
        _assert_blocked_code_fact_metadata(metadata, "changed")
        _assert_blocked_code_fact_metadata(manifest_metadata, "changed")
        assert chunks == symbols == references == 0
    finally:
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_container_child_scan_failure_retracts_prior_aggregate_facts(tmp_path):
    """A blocked child prevents every prior member fact from surviving the container completion."""
    run_migrations(TEST_DATABASE_URL)
    name = f"container-code-fact-retraction-{uuid4()}"
    database.add_monitored_root(name=name, root_path=tmp_path, url=TEST_DATABASE_URL)
    parent = DiscoveredAsset(
        path=tmp_path / "bundle.zip",
        relative_path="bundle.zip",
        file_kind="archive",
        mime_type="application/zip",
        extension=".zip",
        size_bytes=16,
        mtime_ns=1,
        quick_hash="quick-container-retraction",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="metadata_only",
        metadata=_stale_code_fact_metadata("container_parent"),
    )
    safe_child = ContainerChildAsset(
        member_path="child.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=12,
        quick_hash="quick-child-safe",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="inline",
        extraction_status="indexed",
        metadata=_stale_code_fact_metadata("container_child"),
        chunks=(
            AssetChunk(
                chunk_index=0,
                title="child.py",
                body="def safe(): pass",
                modality="code",
                metadata={
                    "code_symbols": [{"name": "safe", "qualified_name": "safe", "signature": "safe()"}],
                    "code_references": [{"source_symbol": "safe", "target": "target", "relationship_kind": "call"}],
                },
            ),
        ),
    )
    blocked_child = ContainerChildAsset(
        member_path="child.py",
        file_kind="code",
        mime_type="text/x-python",
        extension=".py",
        size_bytes=12,
        quick_hash="quick-child-blocked",
        content_hash=f"sha256:{uuid4().hex}",
        extraction_tier="inline",
        extraction_status="indexed",
        metadata={"code": {"symbols": [{"name": "safe", "qualified_name": "safe", "signature": "x" * ((16 * 1024) + 1)}]}},
    )
    try:
        database.persist_crawl_plan(root_name=name, plan=CrawlPlan(root_path=tmp_path, assets=[parent]), url=TEST_DATABASE_URL)
        database.apply_extraction_result(
            root_name=name,
            relative_path="bundle.zip",
            result=ExtractionResult(status="metadata_only", metadata={"extractor": "container"}, child_assets=(safe_child,)),
            url=TEST_DATABASE_URL,
        )
        database.upsert_scan_manifest(
            root_name=name,
            path="bundle.zip/child.py",
            size_bytes=12,
            mtime_ns=1,
            quick_hash="quick-child-safe",
            content_hash=safe_child.content_hash,
            metadata=_stale_code_fact_metadata("container_child"),
            url=TEST_DATABASE_URL,
        )
        database.apply_extraction_result(
            root_name=name,
            relative_path="bundle.zip",
            result=ExtractionResult(status="metadata_only", metadata={"extractor": "container"}, child_assets=(blocked_child,)),
            url=TEST_DATABASE_URL,
        )
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    "SELECT parent.extraction_status, child.extraction_status, parent.metadata, child.metadata, "
                    "parent_manifest.metadata, child_manifest.metadata, "
                    "count(chunk.id)::integer, count(symbol.id)::integer, count(reference.id)::integer "
                    "FROM source_assets parent "
                    "JOIN monitored_roots root ON root.id = parent.root_id "
                    "JOIN source_assets child ON child.metadata->>'container_asset_id' = parent.id::text "
                    "JOIN crawl_path_manifests parent_manifest ON parent_manifest.root_id = parent.root_id AND parent_manifest.path = parent.path "
                    "JOIN crawl_path_manifests child_manifest ON child_manifest.root_id = child.root_id AND child_manifest.path = child.path "
                    "LEFT JOIN asset_chunks chunk ON chunk.asset_id = child.id "
                    "LEFT JOIN code_symbols symbol ON symbol.source_asset_id = child.id "
                    "LEFT JOIN code_references reference ON reference.source_asset_id = child.id "
                    "WHERE root.name = %s AND parent.path = 'bundle.zip' "
                    "GROUP BY parent.extraction_status, child.extraction_status, parent.metadata, child.metadata, "
                    "parent_manifest.metadata, child_manifest.metadata",
                    (name,),
                )
                (
                    parent_status,
                    child_status,
                    parent_metadata,
                    child_metadata,
                    parent_manifest_metadata,
                    child_manifest_metadata,
                    chunks,
                    symbols,
                    references,
                ) = cur.fetchone()
        assert parent_status == child_status == "blocked_by_policy"
        _assert_blocked_code_fact_metadata(parent_metadata, "container_parent")
        _assert_blocked_code_fact_metadata(child_metadata, "container_child")
        _assert_blocked_code_fact_metadata(parent_manifest_metadata, "container_parent")
        _assert_blocked_code_fact_metadata(child_manifest_metadata, "container_child")
        assert chunks == symbols == references == 0
    finally:
        with database._load_psycopg().connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_targeted_sync_does_not_update_outside_target(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "targeted"
    root.mkdir()
    sub = root / "sub"
    sub.mkdir()
    outside_marker = f"outside-{uuid4()}"
    target_marker = f"target-{uuid4()}"
    changed_outside_marker = f"mutatedmarker{uuid4().hex}"
    changed_target_marker = f"changed-target-{uuid4()}"
    (root / "outside.md").write_text(outside_marker, encoding="utf-8")
    (sub / "inside.md").write_text(target_marker, encoding="utf-8")
    name = f"targeted-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        service = KnowledgeService()
        service.sync_corpus(root_name=name)
        (root / "outside.md").write_text(changed_outside_marker, encoding="utf-8")
        (sub / "inside.md").write_text(changed_target_marker, encoding="utf-8")

        result = service.sync_corpus(path=sub / "inside.md")

        assert result["files_seen"] == 1
        assert database.search_corpus_chunks(changed_outside_marker, limit=5, url=TEST_DATABASE_URL) == []
        assert database.search_corpus_chunks(changed_target_marker, limit=5, url=TEST_DATABASE_URL)
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_duplicate_assets_preserve_paths_but_return_one_canonical_hit(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "dupes"
    root.mkdir()
    marker = f"duplicate-{uuid4()}"
    (root / "a.md").write_text(f"{marker} same body", encoding="utf-8")
    (root / "b.md").write_text(f"{marker} same body", encoding="utf-8")
    name = f"dupes-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=10, url=TEST_DATABASE_URL)
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    SELECT count(*), count(*) FILTER (WHERE canonical_asset_id IS NOT NULL)
                    FROM source_assets a
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE r.name = %s
                    """,
                    (name,),
                )
                asset_count, duplicate_count = cur.fetchone()

        assert asset_count == 2
        assert duplicate_count == 1
        assert len(results) == 1
        assert results[0]["duplicate_count"] == 1
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_corpus_search_includes_lexical_stream(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "lexical"
    root.mkdir()
    marker = f"lexical-{uuid4()}"
    (root / "note.md").write_text(f"{marker} corpus semantic retrieval", encoding="utf-8")
    name = f"lexical-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=5, url=TEST_DATABASE_URL)

        assert any("corpus_lexical" in result["streams"] for result in results)
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))


def test_postgres_semantic_duplicate_refresh_suppresses_corpus_and_episode_results(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)

    class FakeSnowflakeEmbeddingProvider:
        def __init__(self, *, model, dimensions):
            self.model = model
            self.dimensions = dimensions

        def embed_batch(self, inputs):
            return [
                EmbeddingResult(
                    owner_table=item.owner_table,
                    owner_id=item.owner_id,
                    model=item.model,
                    dimensions=item.dimensions,
                    vector=[1.0, 0.0],
                )
                for item in inputs
            ]

    monkeypatch.setattr("flux_llm_kb.embeddings.SnowflakeEmbeddingProvider", FakeSnowflakeEmbeddingProvider)

    marker = f"semantic-dupe-{uuid4()}"
    root = tmp_path / "semantic-dupes"
    root.mkdir()
    (root / "alpha.md").write_text(
        f"{marker} semantic duplicate architecture Vespa retrieval local ranking canonical",
        encoding="utf-8",
    )
    (root / "bravo.md").write_text(
        f"{marker} semantic duplicate architecture Vespa retrieval local ranking duplicate extra",
        encoding="utf-8",
    )
    name = f"semantic-dupes-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, trust_rank=900, url=TEST_DATABASE_URL)
    episode_a = insert_episode(
        title=f"{marker} operating decision alpha",
        summary="Semantic duplicate memories should be clustered and retrieved once.",
        metadata={"root_name": name, "workspace_key": f"root:{name}"},
        url=TEST_DATABASE_URL,
    )
    episode_b = insert_episode(
        title=f"{marker} operating decision bravo",
        summary="Semantic duplicate memories should be clustered and retrieved once with extra words.",
        metadata={"root_name": name, "workspace_key": f"root:{name}"},
        url=TEST_DATABASE_URL,
    )

    try:
        service = KnowledgeService()
        service.sync_corpus(root_name=name)
        refresh = database.refresh_semantic_duplicate_clusters(
            memory_class="all",
            root_name=name,
            threshold=0.7,
            url=TEST_DATABASE_URL,
        )
        corpus_results = database.search_corpus_chunks(marker, limit=10, root_name=name, url=TEST_DATABASE_URL)
        episode_results = search_episodes(
            marker,
            limit=10,
            workspace_key=f"root:{name}",
            url=TEST_DATABASE_URL,
        )
        explain = service.explain(
            marker,
            root_name=name,
            filters={"include_suppressed": True},
        )

        assert refresh["created_clusters"] >= 2
        assert len(corpus_results) == 1
        assert corpus_results[0]["semantic_duplicate_cluster"]["suppressed_count"] == 1
        assert len(episode_results) == 1
        assert episode_results[0]["semantic_duplicate_cluster"]["suppressed_count"] == 1
        assert explain["suppression"]["semantic_duplicates"]
    finally:
        forget_episode(episode_a, url=TEST_DATABASE_URL)
        forget_episode(episode_b, url=TEST_DATABASE_URL)
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))
                cur.execute("DELETE FROM semantic_duplicate_clusters WHERE root_name = %s", (name,))


def test_postgres_retrieval_benchmark_seeds_searches_persists_and_cleans_up(monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    label = f"retrieval-benchmark-{uuid4()}"

    result = KnowledgeService().run_retrieval_benchmark(suite="standard", label=label, limit_per_query=5)
    history = database.list_retrieval_benchmark_runs(suite="standard", label=label, url=TEST_DATABASE_URL)
    psycopg = database._load_psycopg()
    with psycopg.connect(TEST_DATABASE_URL) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM monitored_roots WHERE name LIKE '__retrieval_benchmark_%'")
            synthetic_roots = cur.fetchone()[0]

    assert result["status"] == "completed"
    assert result["query_count"] >= 4
    assert result["metrics"]["top1_accuracy"] >= 0.5
    assert result["metrics"]["brief_dilution"] >= 0.0
    categories = {str(case.get("category") or "") for case in result["case_results"]}
    assert {
        "mail_filter",
        "current_only",
        "semantic_duplicate",
        "semantic_guardrail",
        "code_symbol_miss",
    }.issubset(categories)
    assert result["calibration_summary"]["semantic_thresholds"]
    assert result["recommendations"]["settings_mutated"] is False
    assert result["recommendations"]["candidates"]
    assert history
    assert history[0]["label"] == label
    assert history[0]["calibration_summary"]["confidence_bands"]
    assert "metric_deltas" in history[0]
    assert synthetic_roots == 0


def test_postgres_claim_lifecycle_and_graph_traversal_are_migration_backed():
    run_migrations(TEST_DATABASE_URL)
    marker = f"graph-lifecycle-{uuid4()}"

    try:
        claim = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} PostgreSQL lifecycle graph",
            confidence=0.82,
            url=TEST_DATABASE_URL,
        )
        replacement = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} PostgreSQL plus lifecycle graph scoring",
            confidence=0.9,
            url=TEST_DATABASE_URL,
        )
        contradicted = database.transition_claim(
            claim_id=claim["id"],
            transition="contradict",
            related_claim_id=replacement["id"],
            reason="newer evidence",
            url=TEST_DATABASE_URL,
        )
        database.upsert_entity_relation(
            from_entity_id=claim["subject_entity_id"],
            to_entity_id=replacement["subject_entity_id"],
            relation_type="depends_on",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )

        traversal = database.traverse_entity_graph(
            entity_id=claim["subject_entity_id"],
            relation_types=["depends_on"],
            max_depth=2,
            url=TEST_DATABASE_URL,
        )
        fetched = database.get_claim(claim["id"], url=TEST_DATABASE_URL)

        assert contradicted["lifecycle_state"] == "contradicted"
        assert fetched["lifecycle"]["audit_events"]
        assert fetched["lifecycle"]["related_claims"][0]["relation_type"] == "contradicts"
        assert traversal["edges"][0]["relation_type"] == "depends_on"
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM entities WHERE name LIKE %s", (f"{marker}%",))


def test_postgres_claim_review_list_counts_and_graph_work_together():
    run_migrations(TEST_DATABASE_URL)
    marker = f"claim-review-{uuid4()}"

    try:
        active = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} active PostgreSQL",
            confidence=0.8,
            url=TEST_DATABASE_URL,
        )
        stale = database.upsert_claim(
            subject_type="project",
            subject_name=f"{marker}-flux",
            predicate="uses",
            object_text=f"{marker} stale PostgreSQL",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )
        replacement = database.upsert_claim(
            subject_type="system",
            subject_name=f"{marker}-graph",
            predicate="supports",
            object_text=f"{marker} graph review traversal",
            confidence=0.9,
            url=TEST_DATABASE_URL,
        )
        database.transition_claim(
            claim_id=stale["id"],
            transition="deprioritize",
            reason="review queue",
            url=TEST_DATABASE_URL,
        )
        database.upsert_entity_relation(
            from_entity_id=active["subject_entity_id"],
            to_entity_id=replacement["subject_entity_id"],
            relation_type="depends_on",
            confidence=0.7,
            url=TEST_DATABASE_URL,
        )

        claims = database.list_claims(review="needs_review", q=marker, limit=10, url=TEST_DATABASE_URL)
        counts = database.claim_review_counts(url=TEST_DATABASE_URL)
        graph = database.traverse_entity_graph(
            entity_id=active["subject_entity_id"],
            relation_types=["depends_on"],
            direction="both",
            max_depth=2,
            url=TEST_DATABASE_URL,
        )

        assert [claim["id"] for claim in claims] == [stale["id"]]
        assert claims[0]["review_reasons"] == ["stale", "retention:deprioritize"]
        assert counts["needs_review"] >= 1
        assert graph["edges"][0]["relation_type"] == "depends_on"
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM entities WHERE name LIKE %s", (f"{marker}%",))


@pytest.mark.filterwarnings(
    "ignore:Using `httpx` with `starlette.testclient` is deprecated:starlette.exceptions.StarletteDeprecationWarning"
)
def test_postgres_producer_diagnostics_are_raw_only_across_named_local_service_rest_cli_and_mcp(
    monkeypatch,
    capsys,
):
    """Catch a shared-output leak from real durable mail/GPU producer transitions."""
    from fastapi.testclient import TestClient

    from flux_llm_kb.rest_api import create_app

    marker = uuid4().hex
    profile_name = f"task3-private-{marker}"
    retry_model = f"retry-{marker}"
    complete_model = f"complete-{marker}"
    queued_model = f"queued-{marker}"
    private_folder = f"Inbox/Private/{marker}"
    private_generation = f"generation-private-{marker}"
    private_fingerprint = f"sha256:runtime-private-{marker}"
    private_observation = f"observation-private-{marker}"

    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    database.insert_mail_profile(
        name=profile_name,
        source_type="imap",
        account="synthetic@example.invalid",
        server="127.0.0.1",
        folder_paths=[private_folder],
        spool_path=f"private/mail-spool/{profile_name}",
        post_process_policy="move_to_processed",
        trust_rank=100,
        metadata={"provider": "imap", "processed_folder": "Processed"},
        url=TEST_DATABASE_URL,
    )
    mail_result = mail_ingestion.dry_run_mail_post_process(profile_name=profile_name, limit=1)
    assert mail_result["events"][0]["metadata"] == {"folder": private_folder, "sample": True, "uid": 0}

    queued = database.enqueue_gpu_eviction_request(
        lease_id=None,
        request_profile={"task_type": "rerank", "model_id": f"request-{marker}"},
        candidate={"task_type": "embedding", "model_id": queued_model, "component": "model-runner"},
        runtime_generation=private_generation,
        runtime_activity_sequence=33,
        reconciliation_observation_id=private_observation,
        request_reason="idle",
        url=TEST_DATABASE_URL,
    )
    retry_enqueued = database.enqueue_gpu_eviction_request(
        lease_id=None,
        request_profile={"task_type": "rerank", "model_id": f"request-{marker}"},
        candidate={"task_type": "embedding", "model_id": retry_model, "component": "model-runner"},
        runtime_generation=private_generation,
        runtime_activity_sequence=33,
        reconciliation_observation_id=private_observation,
        request_reason="idle",
        url=TEST_DATABASE_URL,
    )
    retry_claim = database.claim_gpu_eviction_request(
        eviction_id=retry_enqueued["eviction_id"],
        worker_id="synthetic-worker",
        url=TEST_DATABASE_URL,
    )
    retried = database.retry_gpu_eviction_request(
        eviction_id=retry_enqueued["eviction_id"],
        error="synthetic unload retry pending",
        metadata={"owner_component": "model-runner", "runtime_fingerprint": private_fingerprint},
        claim_token=retry_claim["claim_token"],
        row_version=retry_claim["row_version"],
        broker_delivery_count=retry_claim["broker_delivery_count"],
        url=TEST_DATABASE_URL,
    )
    complete_enqueued = database.enqueue_gpu_eviction_request(
        lease_id=None,
        request_profile={"task_type": "rerank", "model_id": f"request-{marker}"},
        candidate={"task_type": "embedding", "model_id": complete_model, "component": "model-runner"},
        runtime_generation=private_generation,
        runtime_activity_sequence=34,
        reconciliation_observation_id=private_observation,
        request_reason="idle",
        url=TEST_DATABASE_URL,
    )
    complete_claim = database.claim_gpu_eviction_request(
        eviction_id=complete_enqueued["eviction_id"],
        worker_id="synthetic-worker",
        url=TEST_DATABASE_URL,
    )
    completed = database.complete_gpu_eviction_request(
        eviction_id=complete_enqueued["eviction_id"],
        status="succeeded",
        metadata={
            "owner_component": "model-runner",
            "runtime_fingerprint": private_fingerprint,
            "terminal_reason": "verified_unload",
        },
        claim_token=complete_claim["claim_token"],
        row_version=complete_claim["row_version"],
        url=TEST_DATABASE_URL,
    )

    assert queued["status"] == "queued"
    assert retried["status"] == "retrying"
    assert completed["status"] == "succeeded"
    local_gpu_rows = database.list_local_gpu_eviction_diagnostics(limit=20, url=TEST_DATABASE_URL)
    producer_rows = [row for row in local_gpu_rows if marker in str(row.get("model_id") or "")]
    assert {row["status"] for row in producer_rows} == {"queued", "retrying", "succeeded"}
    producer_by_model = {row["model_id"]: row for row in producer_rows}
    for model_id, expected_sequence in (
        (queued_model, 33),
        (retry_model, 33),
        (complete_model, 34),
    ):
        assert producer_by_model[model_id]["runtime_generation"] == private_generation
        assert producer_by_model[model_id]["runtime_activity_sequence"] == expected_sequence
        assert producer_by_model[model_id]["reconciliation_observation_id"] == private_observation
    assert producer_by_model[retry_model]["metadata"]["runtime_fingerprint"] == private_fingerprint
    assert producer_by_model[complete_model]["metadata"]["runtime_fingerprint"] == private_fingerprint
    assert producer_by_model[complete_model]["metadata"]["terminal_reason"] == "verified_unload"

    class ProducerBackedScheduler:
        def status(self):
            return {
                "evictions": gpu_scheduler._eviction_status(producer_rows),
                "runtime_reconciliation": None,
            }

    monkeypatch.setattr(gpu_scheduler, "get_gpu_scheduler", lambda: ProducerBackedScheduler())
    service = KnowledgeService()
    monkeypatch.setattr("flux_llm_kb.rest_api.KnowledgeService", lambda: service)
    rest_client = TestClient(create_app(), client=("127.0.0.1", 50100))
    monkeypatch.setattr("flux_llm_kb.service.KnowledgeService", lambda: service)
    mcp = mcp_server.create_server(service_factory=lambda: service, retry_sleep=lambda _seconds: None)

    async def read_mcp(section: str):
        public_blocks = await mcp.call_tool("kb.operational_diagnostics", {"section": section, "limit": 25})
        local_blocks = await mcp.call_tool("kb.local_operational_diagnostics", {"section": section, "limit": 25})
        return json.loads(public_blocks[0].text), json.loads(local_blocks[0].text)

    def read_surfaces(section: str):
        service_pair = (
            service.operational_diagnostics(section=section, limit=25),
            service.local_operational_diagnostics(section=section, limit=25),
        )
        rest_pair = (
            rest_client.get(f"/api/diagnostics/{section}").json(),
            rest_client.get(f"/api/local/diagnostics/{section}").json(),
        )
        assert cli.main(["diagnostics", section, "--limit", "25"]) == 0
        public_cli = json.loads(capsys.readouterr().out)
        assert cli.main(["local-detail", "diagnostics", section, "--limit", "25"]) == 0
        local_cli = json.loads(capsys.readouterr().out)
        return [service_pair, rest_pair, (public_cli, local_cli), anyio.run(read_mcp, section)]

    try:
        for public, local in read_surfaces("mail"):
            public_json = json.dumps(public, sort_keys=True)
            local_json = json.dumps(local, sort_keys=True)
            assert private_folder not in public_json
            assert '"uid": 0' not in public_json
            assert private_folder in local_json
            assert '"uid": 0' in local_json

        for public, local in read_surfaces("workers"):
            public_json = json.dumps(public, sort_keys=True)
            local_json = json.dumps(local, sort_keys=True)
            assert private_generation not in public_json
            assert private_fingerprint not in public_json
            assert private_observation not in public_json
            assert "owner_component" not in public_json
            assert "runtime_activity_sequence" not in public_json
            assert private_generation in local_json
            assert private_fingerprint in local_json
            assert private_observation in local_json
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM mail_profiles WHERE name = %s", (profile_name,))
                cur.execute("DELETE FROM gpu_evictions WHERE model_id LIKE %s", (f"%{marker}%",))
