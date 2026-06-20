CREATE TABLE IF NOT EXISTS monitored_roots (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    workspace_scope_id uuid REFERENCES workspace_scopes(id) ON DELETE SET NULL,
    name text NOT NULL UNIQUE,
    root_path text NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    recursive boolean NOT NULL DEFAULT true,
    watch_enabled boolean NOT NULL DEFAULT false,
    trust_rank integer NOT NULL DEFAULT 500,
    include_globs text[] NOT NULL DEFAULT ARRAY[]::text[],
    exclude_globs text[] NOT NULL DEFAULT ARRAY[]::text[],
    max_inline_bytes integer NOT NULL DEFAULT 262144,
    heavy_threshold_bytes integer NOT NULL DEFAULT 10485760,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS source_assets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    root_id uuid NOT NULL REFERENCES monitored_roots(id) ON DELETE CASCADE,
    source_id uuid REFERENCES sources(id) ON DELETE SET NULL,
    path text NOT NULL,
    uri text NOT NULL,
    file_kind text NOT NULL,
    mime_type text,
    extension text NOT NULL DEFAULT '',
    size_bytes bigint NOT NULL DEFAULT 0,
    mtime_ns bigint NOT NULL DEFAULT 0,
    quick_hash text,
    content_hash text,
    canonical_asset_id uuid REFERENCES source_assets(id) ON DELETE SET NULL,
    extraction_status text NOT NULL DEFAULT 'pending',
    extraction_tier text NOT NULL DEFAULT 'metadata_only',
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    indexed_at timestamptz,
    deleted_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (root_id, path)
);

CREATE TABLE IF NOT EXISTS asset_chunks (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    asset_id uuid NOT NULL REFERENCES source_assets(id) ON DELETE CASCADE,
    chunk_index integer NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    modality text NOT NULL DEFAULT 'text',
    locator text,
    token_estimate integer NOT NULL DEFAULT 0,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    search_vector tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('english', coalesce(title, '')), 'A') ||
        setweight(to_tsvector('english', coalesce(body, '')), 'B')
    ) STORED,
    UNIQUE (asset_id, chunk_index)
);

CREATE TABLE IF NOT EXISTS crawl_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    root_id uuid REFERENCES monitored_roots(id) ON DELETE SET NULL,
    status text NOT NULL DEFAULT 'running',
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    files_seen integer NOT NULL DEFAULT 0,
    files_changed integer NOT NULL DEFAULT 0,
    files_deleted integer NOT NULL DEFAULT 0,
    chunks_indexed integer NOT NULL DEFAULT 0,
    jobs_queued integer NOT NULL DEFAULT 0,
    errors jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE TABLE IF NOT EXISTS watcher_state (
    root_id uuid PRIMARY KEY REFERENCES monitored_roots(id) ON DELETE CASCADE,
    status text NOT NULL DEFAULT 'stopped',
    heartbeat_at timestamptz,
    last_event_at timestamptz,
    last_error text,
    process_id integer,
    updated_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS locked_at timestamptz,
    ADD COLUMN IF NOT EXISTS locked_by text;

CREATE INDEX IF NOT EXISTS idx_monitored_roots_enabled ON monitored_roots (enabled, watch_enabled);
CREATE INDEX IF NOT EXISTS idx_monitored_roots_metadata ON monitored_roots USING GIN (metadata);
CREATE INDEX IF NOT EXISTS idx_source_assets_root_seen ON source_assets (root_id, last_seen_at DESC);
CREATE INDEX IF NOT EXISTS idx_source_assets_hash ON source_assets (content_hash) WHERE content_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_source_assets_path_trgm ON source_assets USING GIN (path gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_source_assets_metadata ON source_assets USING GIN (metadata);
CREATE INDEX IF NOT EXISTS idx_asset_chunks_search_vector ON asset_chunks USING GIN (search_vector);
CREATE INDEX IF NOT EXISTS idx_asset_chunks_title_trgm ON asset_chunks USING GIN (title gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_asset_chunks_body_trgm ON asset_chunks USING GIN (body gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_crawl_runs_root_started ON crawl_runs (root_id, started_at DESC);
CREATE INDEX IF NOT EXISTS idx_watcher_state_status ON watcher_state (status, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_capture_jobs_corpus_claim ON capture_jobs (status, next_attempt_at, created_at)
    WHERE job_type LIKE 'corpus_%';
