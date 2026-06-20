import os
from uuid import uuid4

import pytest

from flux_llm_kb import database
from flux_llm_kb.database import forget_episode, insert_episode, run_migrations, search_episodes
from flux_llm_kb.service import KnowledgeService


TEST_DATABASE_URL = os.environ.get("FLUX_KB_TEST_DATABASE_URL")

if not TEST_DATABASE_URL:
    pytest.skip("FLUX_KB_TEST_DATABASE_URL is not set", allow_module_level=True)


def test_postgres_hybrid_search_smoke():
    run_migrations(TEST_DATABASE_URL)
    marker = f"pgvector-smoke-{uuid4()}"
    episode_id = insert_episode(
        title=f"Hybrid retrieval {marker}",
        summary="PostgreSQL full text, pg_trgm fuzzy search, and pgvector ranking are enabled.",
        url=TEST_DATABASE_URL,
    )

    results = search_episodes(marker, limit=5, url=TEST_DATABASE_URL)

    assert any(result["id"] == episode_id for result in results)
    assert any("vector" in result["streams"] for result in results)
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
