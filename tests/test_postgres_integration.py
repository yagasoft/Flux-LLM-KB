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


def test_postgres_corpus_search_includes_vector_stream(tmp_path, monkeypatch):
    monkeypatch.setenv("FLUX_KB_DATABASE_URL", TEST_DATABASE_URL)
    run_migrations(TEST_DATABASE_URL)
    root = tmp_path / "vector"
    root.mkdir()
    marker = f"vector-{uuid4()}"
    (root / "note.md").write_text(f"{marker} corpus semantic retrieval", encoding="utf-8")
    name = f"vector-{uuid4()}"
    database.add_monitored_root(name=name, root_path=root, url=TEST_DATABASE_URL)

    try:
        KnowledgeService().sync_corpus(root_name=name)
        results = database.search_corpus_chunks(marker, limit=5, url=TEST_DATABASE_URL)

        assert any("corpus_vector" in result["streams"] for result in results)
    finally:
        psycopg = database._load_psycopg()
        with psycopg.connect(TEST_DATABASE_URL) as conn:
            with conn.cursor() as cur:
                cur.execute("DELETE FROM monitored_roots WHERE name = %s", (name,))
