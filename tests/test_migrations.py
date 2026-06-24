from pathlib import Path

from flux_llm_kb import database
from flux_llm_kb.migrations import Migration
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
    assert any("ALTER TABLE crawl_runs" in item.sql and "reason" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS runtime_settings" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_profiles" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_messages" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_states" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_tokens" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS outlook_sync_requests" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS outlook_host_state" in item.sql for item in migrations)
    assert any("sync_interval_seconds" in item.sql for item in migrations)
    graph_migration = next(item for item in migrations if item.name == "0010_graph_lifecycle")
    assert "CREATE TABLE IF NOT EXISTS claim_lifecycle_events" in graph_migration.sql
    assert "CREATE TABLE IF NOT EXISTS claim_relations" in graph_migration.sql
    assert "ADD COLUMN IF NOT EXISTS lifecycle_state" in graph_migration.sql
    assert "idx_claim_lifecycle_events_claim" in graph_migration.sql
    assert "idx_claim_relations_from" in graph_migration.sql
    assert "idx_claims_search_vector" in graph_migration.sql
    mail_post_process_migration = next(item for item in migrations if item.name == "0011_mail_post_process")
    assert "CREATE TABLE IF NOT EXISTS mail_post_process_events" in mail_post_process_migration.sql
    assert "ADD COLUMN IF NOT EXISTS post_process_status" in mail_post_process_migration.sql
    assert "idx_mail_post_process_events_profile" in mail_post_process_migration.sql
    retention_quality_migration = next(item for item in migrations if item.name == "0012_retention_quality")
    assert "ALTER TABLE retention_policies" in retention_quality_migration.sql
    assert "ADD COLUMN IF NOT EXISTS created_at" in retention_quality_migration.sql
    assert "ADD COLUMN IF NOT EXISTS updated_by" in retention_quality_migration.sql
    assert "retention_policies_half_life_positive" in retention_quality_migration.sql
    assert "retention_policies_min_confidence_range" in retention_quality_migration.sql
    assert "retention_policies_action_allowed" in retention_quality_migration.sql
    assert "VALUES ('episode'" in retention_quality_migration.sql
    assert "VALUES ('claim'" in retention_quality_migration.sql
    assert "VALUES ('corpus'" in retention_quality_migration.sql
    semantic_duplicate_migration = next(item for item in migrations if item.name == "0013_semantic_duplicate_clusters")
    assert "CREATE TABLE IF NOT EXISTS semantic_duplicate_clusters" in semantic_duplicate_migration.sql
    assert "CREATE TABLE IF NOT EXISTS semantic_duplicate_members" in semantic_duplicate_migration.sql
    assert "memory_class IN ('corpus', 'episode', 'claim')" in semantic_duplicate_migration.sql
    assert "owner_table IN ('asset_chunks', 'episodes', 'claims')" in semantic_duplicate_migration.sql
    assert "member_role IN ('canonical', 'duplicate')" in semantic_duplicate_migration.sql
    assert "status IN ('active', 'retired')" in semantic_duplicate_migration.sql
    assert "idx_semantic_duplicate_clusters_scope" in semantic_duplicate_migration.sql
    assert "idx_semantic_duplicate_members_owner" in semantic_duplicate_migration.sql
    acceleration_migration = next(item for item in migrations if item.name == "0014_acceleration_foundation")
    assert "ADD COLUMN IF NOT EXISTS job_family" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS resource_class" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS priority" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS time_budget_seconds" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS started_at" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS completed_at" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS last_duration_ms" in acceleration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS telemetry" in acceleration_migration.sql
    assert "idx_capture_jobs_family_claim" in acceleration_migration.sql
    assert "idx_capture_jobs_family_status" in acceleration_migration.sql
    assert "corpus_extract_video" in acceleration_migration.sql
    assert "media" in acceleration_migration.sql
    assert all(Path(item.path).suffix == ".sql" for item in migrations)


def test_run_migrations_uses_advisory_lock_and_idempotent_insert(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def execute(self, sql, params=None):
            executed.append((sql, params))

        def fetchone(self):
            sql = executed[-1][0]
            if "SELECT 1 FROM schema_migrations" in sql:
                return None
            if "RETURNING version" in sql:
                return (1,)
            return None

    class FakeConnection:
        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def cursor(self):
            return FakeCursor()

    class FakePsycopg:
        def connect(self, *_args, **_kwargs):
            return FakeConnection()

    monkeypatch.setattr(database, "_load_psycopg", lambda: FakePsycopg())
    monkeypatch.setattr(
        database,
        "load_migrations",
        lambda: [Migration(version=1, name="0001_initial", path="0001_initial.sql", sql="SELECT 1")],
    )

    assert database.run_migrations("postgresql://test") == ["0001_initial"]

    statements = [item[0] for item in executed]
    assert "SELECT pg_advisory_lock(%s)" in statements[0]
    assert any("ON CONFLICT (version) DO NOTHING" in statement for statement in statements)
    assert statements[-1] == "SELECT pg_advisory_unlock(%s)"
