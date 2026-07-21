from pathlib import Path

from flux_llm_kb import database
from flux_llm_kb.migrations import Migration
from flux_llm_kb.migrations import load_migrations


def test_load_migrations_returns_ordered_sql_files():
    migrations = load_migrations()

    assert migrations
    assert migrations == sorted(migrations, key=lambda item: item.version)
    assert migrations[0].name == "0001_initial"
    assert "CREATE EXTENSION IF NOT EXISTS vector" not in migrations[0].sql
    assert "CREATE EXTENSION IF NOT EXISTS pgcrypto" in migrations[0].sql
    assert "CREATE TABLE IF NOT EXISTS episodes" in migrations[0].sql
    assert "CREATE TABLE IF NOT EXISTS embeddings" not in migrations[0].sql
    assert "USING hnsw" not in migrations[1].sql
    assert not any("idx_asset_chunks_body_trgm" in item.sql for item in migrations if item.name != "0033_legacy_retrieval_purge")
    assert any("CREATE TABLE IF NOT EXISTS monitored_roots" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS source_assets" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS asset_chunks" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS watcher_state" in item.sql for item in migrations)
    assert any("ALTER TABLE crawl_runs" in item.sql and "reason" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS runtime_settings" in item.sql for item in migrations)
    runtime_settings_mail = next(item for item in migrations if item.name == "0004_runtime_settings_mail")
    assert "CREATE TABLE IF NOT EXISTS runtime_control_requests" in runtime_settings_mail.sql
    assert "updated_at timestamptz NOT NULL DEFAULT now()" in runtime_settings_mail.sql
    assert any("CREATE TABLE IF NOT EXISTS mail_profiles" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_messages" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_states" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS mail_oauth_tokens" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS outlook_sync_requests" in item.sql for item in migrations)
    assert any("CREATE TABLE IF NOT EXISTS outlook_host_state" in item.sql for item in migrations)
    event_messaging = next(item for item in migrations if item.name == "0041_event_driven_messaging")
    assert "CREATE TABLE IF NOT EXISTS message_outbox" in event_messaging.sql
    assert "CREATE TABLE IF NOT EXISTS message_inbox" in event_messaging.sql
    assert "CREATE TABLE IF NOT EXISTS callback_deliveries" in event_messaging.sql
    assert "ADD COLUMN IF NOT EXISTS broker_message_id" in event_messaging.sql
    assert "idx_message_outbox_pending" in event_messaging.sql
    event_journal = next(item for item in migrations if item.name == "0044_event_journal")
    assert "CREATE TABLE IF NOT EXISTS event_journal" in event_journal.sql
    assert "UNIQUE (subscriber_name, message_id)" in event_journal.sql
    assert "idx_event_journal_routing" in event_journal.sql
    runtime_control_updated_at = next(item for item in migrations if item.name == "0042_runtime_control_updated_at")
    assert "ALTER TABLE runtime_control_requests" in runtime_control_updated_at.sql
    assert "ADD COLUMN IF NOT EXISTS updated_at" in runtime_control_updated_at.sql
    gpu_eviction_cas_audit_index = next(item for item in migrations if item.name == "0047_gpu_eviction_cas_audit_index")
    assert "CREATE INDEX IF NOT EXISTS idx_audit_events_gpu_eviction_cas_rejected_created" in gpu_eviction_cas_audit_index.sql
    assert "ON audit_events (created_at DESC)" in gpu_eviction_cas_audit_index.sql
    assert "WHERE event_type = 'gpu_eviction.cas_rejected'" in gpu_eviction_cas_audit_index.sql
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
    embedding_migration = next(item for item in migrations if item.name == "0015_embedding_vectorization")
    assert "Legacy embedding storage was removed" in embedding_migration.sql
    assert "ALTER TABLE embeddings" not in embedding_migration.sql
    assert "idx_embeddings_owner_model" not in embedding_migration.sql
    benchmark_migration = next(item for item in migrations if item.name == "0017_benchmark_harness")
    assert "ALTER TABLE acceleration_benchmark_runs" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS mode" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS label" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS compare_label" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS pass_index" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS hash_parallelism" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS worker_count" in benchmark_migration.sql
    assert "ADD COLUMN IF NOT EXISTS manifest_skipped_unchanged" in benchmark_migration.sql
    assert "idx_acceleration_benchmark_runs_compare" in benchmark_migration.sql
    calibration_migration = next(item for item in migrations if item.name == "0018_operational_benchmark_calibration")
    assert "ADD COLUMN IF NOT EXISTS scope_type" in calibration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS scope_hash" in calibration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS deployment_label" in calibration_migration.sql
    assert "ADD COLUMN IF NOT EXISTS model_telemetry" in calibration_migration.sql
    assert "idx_acceleration_benchmark_runs_scope_deployment" in calibration_migration.sql
    code_migration = next(item for item in migrations if item.name == "0019_code_aware_retrieval")
    assert "CREATE TABLE IF NOT EXISTS code_symbols" in code_migration.sql
    assert "CREATE TABLE IF NOT EXISTS code_references" in code_migration.sql
    assert "source_asset_id uuid NOT NULL REFERENCES source_assets" in code_migration.sql
    assert "asset_chunk_id uuid REFERENCES asset_chunks" in code_migration.sql
    assert "relationship_kind text NOT NULL" in code_migration.sql
    assert "parser_status text NOT NULL" in code_migration.sql
    assert "idx_code_symbols_lookup" in code_migration.sql
    assert "idx_code_references_target" in code_migration.sql
    reliability_migration = next(item for item in migrations if item.name == "0021_indexer_reliability_gate")
    assert "idx_acceleration_benchmark_runs_scenario_created" in reliability_migration.sql
    assert "idx_acceleration_benchmark_runs_scope_hash_created" in reliability_migration.sql
    assert "idx_acceleration_benchmark_runs_recommendation_scenario" in reliability_migration.sql
    feedback_migration = next(item for item in migrations if item.name == "0022_code_retrieval_feedback")
    assert "CREATE TABLE IF NOT EXISTS code_retrieval_feedback_events" in feedback_migration.sql
    assert "miss_category" in feedback_migration.sql
    assert "query_hash" in feedback_migration.sql
    assert "expected_symbol_hash" in feedback_migration.sql
    assert "idx_code_feedback_category_created" in feedback_migration.sql
    governance_migration = next(item for item in migrations if item.name == "0023_memory_governance_actions")
    assert "CREATE TABLE IF NOT EXISTS memory_governance_runs" in governance_migration.sql
    assert "CREATE TABLE IF NOT EXISTS memory_governance_actions" in governance_migration.sql
    assert "CREATE TABLE IF NOT EXISTS memory_governance_digests" in governance_migration.sql
    assert "CREATE TABLE IF NOT EXISTS memory_governance_policy_snapshots" in governance_migration.sql
    assert "settings_mutated boolean NOT NULL DEFAULT false" in governance_migration.sql
    assert "memory_mutated boolean NOT NULL DEFAULT false" in governance_migration.sql
    assert "idx_memory_governance_actions_status" in governance_migration.sql
    assert "idx_memory_governance_actions_target" in governance_migration.sql
    retrieval_performance_migration = next(item for item in migrations if item.name == "0026_retrieval_performance")
    assert "ADD COLUMN IF NOT EXISTS root_id uuid" not in retrieval_performance_migration.sql
    assert "UPDATE embeddings emb" not in retrieval_performance_migration.sql
    assert "idx_embeddings_asset_chunks_root_model" not in retrieval_performance_migration.sql
    assert "idx_source_assets_active_root" in retrieval_performance_migration.sql
    assert "idx_asset_chunks_sidecar_ref" in retrieval_performance_migration.sql
    assert "idx_code_symbols_qualified_name_trgm" in retrieval_performance_migration.sql
    assert "idx_code_symbols_name_trgm" in retrieval_performance_migration.sql
    assert "gin_trgm_ops" in retrieval_performance_migration.sql
    retrieval_hydration_migration = next(item for item in migrations if item.name == "0027_retrieval_hydration_performance")
    assert "idx_source_assets_canonical_asset_id" in retrieval_hydration_migration.sql
    assert "WHERE canonical_asset_id IS NOT NULL" in retrieval_hydration_migration.sql
    sync_performance_migration = next(item for item in migrations if item.name == "0028_corpus_sync_performance")
    assert "ADD COLUMN IF NOT EXISTS progress_heartbeat_at" in sync_performance_migration.sql
    assert "idx_capture_jobs_stale_running_sync" in sync_performance_migration.sql
    tool_output_migration = next(item for item in migrations if item.name == "0029_job_tool_invocations")
    assert "CREATE TABLE IF NOT EXISTS capture_job_tool_invocations" in tool_output_migration.sql
    assert "job_id uuid NOT NULL REFERENCES capture_jobs" in tool_output_migration.sql
    assert "stdout text NOT NULL DEFAULT ''" in tool_output_migration.sql
    assert "stderr text NOT NULL DEFAULT ''" in tool_output_migration.sql
    assert "idx_capture_job_tool_invocations_job_started" in tool_output_migration.sql
    assert "idx_capture_job_tool_invocations_retention" in tool_output_migration.sql
    job_retention_migration = next(item for item in migrations if item.name == "0030_capture_job_retention")
    assert "ADD COLUMN IF NOT EXISTS delete_requested_at" in job_retention_migration.sql
    assert "ADD COLUMN IF NOT EXISTS delete_requested_by" in job_retention_migration.sql
    assert "ADD COLUMN IF NOT EXISTS delete_reason" in job_retention_migration.sql
    assert "idx_capture_jobs_completed_retention" in job_retention_migration.sql
    assert "idx_capture_jobs_marked_retention" in job_retention_migration.sql
    blocked_taxonomy_migration = next(item for item in migrations if item.name == "0031_capture_job_blocked_status_taxonomy")
    assert "blocked_by_policy" in blocked_taxonomy_migration.sql
    assert "blocked_invalid_source" in blocked_taxonomy_migration.sql
    assert "inline_extraction_limit" in blocked_taxonomy_migration.sql
    assert "metadata_only_blocked" in blocked_taxonomy_migration.sql
    assert "Package not found" in blocked_taxonomy_migration.sql
    assert "BadZipFile" in blocked_taxonomy_migration.sql
    assert "invalid_package" in blocked_taxonomy_migration.sql
    assert "WHERE status = 'blocked_missing_dependency'" in blocked_taxonomy_migration.sql
    assert "status = 'blocked_by_policy'" in blocked_taxonomy_migration.sql
    assert "status = 'blocked_invalid_source'" in blocked_taxonomy_migration.sql
    assert "status <> 'blocked_missing_dependency'" not in blocked_taxonomy_migration.sql
    search_index_migration = next(item for item in migrations if item.name == "0032_vespa_search_index_records")
    assert "CREATE TABLE IF NOT EXISTS search_index_records" in search_index_migration.sql
    assert "vespa_document_id text NOT NULL" in search_index_migration.sql
    assert "embedding_model text NOT NULL" in search_index_migration.sql
    assert "embedding_dimensions integer NOT NULL" in search_index_migration.sql
    assert "index_status text NOT NULL" in search_index_migration.sql
    assert "sync_started_at timestamptz" in search_index_migration.sql
    assert "sync_completed_at timestamptz" in search_index_migration.sql
    assert "idx_search_index_records_owner" in search_index_migration.sql
    assert "idx_search_index_records_root_status" in search_index_migration.sql
    assert "idx_search_index_records_source_hash" in search_index_migration.sql
    assert "idx_capture_jobs_search_index_sync_claim" in search_index_migration.sql
    assert "WHERE job_type = 'search_index_sync'" in search_index_migration.sql
    purge_migration = next(item for item in migrations if item.name == "0033_legacy_retrieval_purge")
    assert "algorithm = 'flux-hash-v1:cosine'" in purge_migration.sql
    assert "action = 'sync_search_index'" in purge_migration.sql
    assert "CREATE OR REPLACE PROCEDURE run_legacy_retrieval_purge()" in purge_migration.sql
    obsolete_migration = next(item for item in migrations if item.name == "0034_capture_job_obsolete_status")
    assert "status = 'obsolete'" in obsolete_migration.sql
    assert "delete_requested_at IS NOT NULL" in obsolete_migration.sql
    assert "obsolete_previous_status" in obsolete_migration.sql
    assert "obsolete_previous_result_status" in obsolete_migration.sql
    assert "DROP INDEX IF EXISTS idx_capture_jobs_marked_retention" in obsolete_migration.sql
    assert "idx_capture_jobs_marked_retention" in obsolete_migration.sql
    unique_jobs_migration = next(item for item in migrations if item.name == "0035_capture_job_identity")
    assert "CREATE OR REPLACE FUNCTION capture_job_identity" in unique_jobs_migration.sql
    assert "IMMUTABLE" in unique_jobs_migration.sql
    for ignored_field in (
        "reason",
        "status",
        "requested_by",
        "requested_at",
        "paths_total",
        "path_batch_index",
        "path_batch_total",
    ):
        assert f"- '{ignored_field}'" in unique_jobs_migration.sql
    assert "ADD COLUMN IF NOT EXISTS identity_key" in unique_jobs_migration.sql
    assert "GENERATED ALWAYS AS" in unique_jobs_migration.sql
    assert "ROW_NUMBER() OVER" in unique_jobs_migration.sql
    assert "WHEN status = 'running' THEN 0" in unique_jobs_migration.sql
    assert "DELETE FROM capture_jobs" in unique_jobs_migration.sql
    assert "CREATE UNIQUE INDEX IF NOT EXISTS idx_capture_jobs_identity_key" in unique_jobs_migration.sql
    gpu_eviction_migration = next(item for item in migrations if item.name == "0037_gpu_scheduler_evictions")
    assert "CREATE TABLE IF NOT EXISTS gpu_evictions" in gpu_eviction_migration.sql
    assert "lease_id text REFERENCES gpu_leases" in gpu_eviction_migration.sql
    assert "estimated_freed_vram_mb" in gpu_eviction_migration.sql
    assert "idx_gpu_evictions_created" in gpu_eviction_migration.sql
    brokered_gpu_eviction_migration = next(item for item in migrations if item.name == "0043_brokered_gpu_evictions")
    assert "DROP CONSTRAINT IF EXISTS gpu_evictions_status_check" in brokered_gpu_eviction_migration.sql
    assert "status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'skipped')" in brokered_gpu_eviction_migration.sql
    assert "ADD COLUMN IF NOT EXISTS broker_message_id" in brokered_gpu_eviction_migration.sql
    assert "ADD COLUMN IF NOT EXISTS routing_key" in brokered_gpu_eviction_migration.sql
    assert "ADD COLUMN IF NOT EXISTS correlation_id" in brokered_gpu_eviction_migration.sql
    assert "ADD COLUMN IF NOT EXISTS broker_delivery_count" in brokered_gpu_eviction_migration.sql
    assert "idx_gpu_evictions_active" in brokered_gpu_eviction_migration.sql
    model_activity_migration = next(item for item in migrations if item.name == "0038_model_activity_events")
    assert "CREATE TABLE IF NOT EXISTS model_activity_events" in model_activity_migration.sql
    assert "status IN ('running', 'completed', 'failed', 'busy', 'stale_running', 'blocked_missing_dependency')" in model_activity_migration.sql
    assert "activity_class IN ('retrieval', 'vision_ocr', 'sidecar', 'health', 'control_plane', 'model_loading')" in model_activity_migration.sql
    assert "idx_model_activity_events_started" in model_activity_migration.sql
    assert "idx_model_activity_events_running" in model_activity_migration.sql
    control_plane_migration = next(item for item in migrations if item.name == "0039_model_activity_control_plane")
    assert "DROP CONSTRAINT IF EXISTS model_activity_events_activity_class_check" in control_plane_migration.sql
    assert "'control_plane'" in control_plane_migration.sql
    blocked_activity_migration = next(item for item in migrations if item.name == "0040_model_activity_blocked_missing_dependency")
    assert "DROP CONSTRAINT IF EXISTS model_activity_events_status_check" in blocked_activity_migration.sql
    assert "blocked_missing_dependency" in blocked_activity_migration.sql
    assert "DROP TABLE IF EXISTS embeddings" in purge_migration.sql
    assert "DROP INDEX IF EXISTS idx_asset_chunks_body_trgm" in purge_migration.sql
    assert "DROP EXTENSION IF EXISTS vector" in purge_migration.sql
    assert all(Path(item.path).suffix == ".sql" for item in migrations)


def test_gpu_runtime_reconciliation_migration_is_additive():
    migration = next(item for item in load_migrations() if item.name == "0045_gpu_runtime_reconciliation")
    sql = migration.sql
    for fragment in (
        "CREATE TABLE IF NOT EXISTS gpu_runtime_inventory",
        "CREATE TABLE IF NOT EXISTS gpu_model_vram_calibration",
        "ADD COLUMN IF NOT EXISTS admission_key",
        "ADD COLUMN IF NOT EXISTS priority_class",
        "ADD COLUMN IF NOT EXISTS wait_reason",
        "ADD COLUMN IF NOT EXISTS runtime_generation",
        "ADD COLUMN IF NOT EXISTS runtime_activity_sequence",
        "ADD COLUMN IF NOT EXISTS claim_token",
        "ADD COLUMN IF NOT EXISTS row_version",
        "ADD COLUMN IF NOT EXISTS heartbeat_at",
        "ADD COLUMN IF NOT EXISTS retry_not_before",
        "ADD COLUMN IF NOT EXISTS expires_at",
        "'expired'",
    ):
        assert fragment in sql


def test_gpu_vram_samples_migration_follows_immutable_reconciliation_migration():
    migrations = load_migrations()
    reconciliation = next(item for item in migrations if item.name == "0045_gpu_runtime_reconciliation")
    samples = next(item for item in migrations if item.name == "0046_gpu_vram_samples")

    assert reconciliation.version < samples.version
    assert "gpu_vram_samples" not in reconciliation.sql
    assert "CREATE TABLE IF NOT EXISTS gpu_vram_samples" in samples.sql
    assert "idx_gpu_vram_samples_shape_observed" in samples.sql


def test_run_migrations_applies_samples_after_reconciliation_was_already_recorded(monkeypatch):
    executed: list[tuple[str, object]] = []

    class FakeCursor:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def execute(self, sql, params=None): executed.append((sql, params))
        def fetchone(self):
            sql, params = executed[-1]
            if "SELECT 1 FROM schema_migrations" in sql:
                return (1,) if params == (45,) else None
            if "RETURNING version" in sql:
                return (46,)
            return None

    class FakeConnection:
        def __enter__(self): return self
        def __exit__(self, *_args): return None
        def cursor(self): return FakeCursor()

    monkeypatch.setattr(database, "_load_psycopg", lambda: type("Psycopg", (), {"connect": lambda *_a, **_k: FakeConnection()})())
    monkeypatch.setattr(database, "load_migrations", lambda: [
        Migration(version=45, name="0045_gpu_runtime_reconciliation", path="0045.sql", sql="SELECT 'old'"),
        Migration(version=46, name="0046_gpu_vram_samples", path="0046.sql", sql="CREATE TABLE gpu_vram_samples"),
    ])

    assert database.run_migrations("postgresql://test") == ["0046_gpu_vram_samples"]
    assert "SELECT 'old'" not in [statement for statement, _params in executed]
    assert "CREATE TABLE gpu_vram_samples" in [statement for statement, _params in executed]


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
