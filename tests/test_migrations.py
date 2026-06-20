from pathlib import Path

from flux_llm_kb.migrations import load_migrations


def test_load_migrations_returns_ordered_sql_files():
    migrations = load_migrations()

    assert migrations
    assert migrations == sorted(migrations, key=lambda item: item.version)
    assert migrations[0].name == "0001_initial"
    assert "CREATE EXTENSION IF NOT EXISTS vector" in migrations[0].sql
    assert "CREATE EXTENSION IF NOT EXISTS pgcrypto" in migrations[0].sql
    assert "CREATE TABLE IF NOT EXISTS episodes" in migrations[0].sql
    assert "USING hnsw" in migrations[1].sql
    assert all(Path(item.path).suffix == ".sql" for item in migrations)
