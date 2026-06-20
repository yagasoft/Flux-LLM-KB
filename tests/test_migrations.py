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
    assert any("CREATE TABLE IF NOT EXISTS monitored_roots" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS source_assets" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS asset_chunks" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS watcher_state" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS runtime_settings" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_profiles" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_messages" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_states" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_tokens" in item.sql for item in migrations)
    assert all(Path(item.path).suffix == ".sql" for item in migrations)
