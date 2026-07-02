UPDATE capture_jobs
SET job_type = 'search_index_sync',
    payload = (payload - 'stale_only') || jsonb_build_object(
        'owner_class', COALESCE(NULLIF(payload->>'owner_class', ''), 'all'),
        'root_name', NULLIF(payload->>'root_name', ''),
        'limit', COALESCE(NULLIF(payload->>'limit', '')::integer, 100)
    ),
    job_family = 'embedding',
    resource_class = 'gpu',
    priority = 35,
    time_budget_seconds = 300,
    updated_at = now()
WHERE job_type = 'corpus_embed'
  AND status IN ('pending', 'retrying', 'retrying_locked', 'failed');

DELETE FROM runtime_settings
WHERE key IN (
    'embedding.model',
    'embedding.dimensions',
    'operator.automation.auto_refresh_embeddings'
);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'operator_automation_actions_action_allowed'
    ) THEN
        ALTER TABLE operator_automation_actions
            DROP CONSTRAINT operator_automation_actions_action_allowed;
    END IF;
END $$;

UPDATE operator_automation_actions
SET action = 'sync_search_index',
    source = CASE WHEN source = 'embeddings' THEN 'search_index' ELSE source END,
    updated_at = now()
WHERE action = 'enqueue_embedding_refresh';

ALTER TABLE operator_automation_actions
    ADD CONSTRAINT operator_automation_actions_action_allowed
    CHECK (action IN (
        'refresh_retrieval_evidence',
        'ingest_approved_capture',
        'safe_diagnostic_recovery',
        'sync_search_index',
        'run_governance_shadow'
    ));

CREATE OR REPLACE PROCEDURE run_legacy_retrieval_purge()
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE semantic_duplicate_clusters
    SET status = 'retired',
        updated_at = now(),
        metadata = metadata || jsonb_build_object('retired_reason', 'legacy_hash_vector_purge')
    WHERE status = 'active'
      AND algorithm = 'flux-hash-v1:cosine';

    DROP INDEX IF EXISTS idx_embeddings_vector_hnsw;
    DROP INDEX IF EXISTS idx_embeddings_owner_model;
    DROP INDEX IF EXISTS idx_embeddings_metadata;
    DROP INDEX IF EXISTS idx_embeddings_source_hash;
    DROP INDEX IF EXISTS idx_embeddings_asset_chunks_root_model;
    DROP INDEX IF EXISTS idx_asset_chunks_body_trgm;

    DROP TABLE IF EXISTS embeddings;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute attr
        JOIN pg_type typ ON typ.oid = attr.atttypid
        JOIN pg_class rel ON rel.oid = attr.attrelid
        JOIN pg_namespace ns ON ns.oid = rel.relnamespace
        WHERE typ.typname = 'vector'
          AND NOT attr.attisdropped
          AND ns.nspname NOT IN ('pg_catalog', 'information_schema')
    ) THEN
        DROP EXTENSION IF EXISTS vector;
    END IF;
END;
$$;
