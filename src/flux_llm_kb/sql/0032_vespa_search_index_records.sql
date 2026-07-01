CREATE TABLE IF NOT EXISTS search_index_records (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    vespa_document_id text NOT NULL UNIQUE,
    owner_table text NOT NULL,
    owner_id uuid NOT NULL,
    root_id uuid,
    root_name text,
    source_hash text,
    embedding_model text NOT NULL,
    embedding_dimensions integer NOT NULL,
    model_generation text NOT NULL DEFAULT 'snowflake-qwen-paddleocr-v1',
    index_status text NOT NULL DEFAULT 'pending',
    last_error text,
    sync_started_at timestamptz,
    sync_completed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT search_index_records_owner_allowed
        CHECK (owner_table IN ('asset_chunks', 'episodes', 'claims')),
    CONSTRAINT search_index_records_dimensions_positive
        CHECK (embedding_dimensions > 0),
    CONSTRAINT search_index_records_status_allowed
        CHECK (index_status IN ('pending', 'syncing', 'indexed', 'deleted', 'failed', 'skipped'))
);

CREATE INDEX IF NOT EXISTS idx_search_index_records_owner
    ON search_index_records (owner_table, owner_id);

CREATE INDEX IF NOT EXISTS idx_search_index_records_root_status
    ON search_index_records (root_id, root_name, index_status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_search_index_records_source_hash
    ON search_index_records (source_hash)
    WHERE source_hash IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_search_index_records_status_updated
    ON search_index_records (index_status, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_capture_jobs_search_index_sync_claim
    ON capture_jobs (status, next_attempt_at, priority DESC, created_at)
    WHERE job_type = 'search_index_sync';
