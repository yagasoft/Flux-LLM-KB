ALTER TABLE watcher_state
    ADD COLUMN IF NOT EXISTS event_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE TABLE IF NOT EXISTS watcher_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    root_id uuid NOT NULL REFERENCES monitored_roots(id) ON DELETE CASCADE,
    action text NOT NULL,
    path_hash text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_watcher_events_root_created
    ON watcher_events (root_id, created_at DESC);

CREATE TABLE IF NOT EXISTS acceleration_benchmark_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    fixture text NOT NULL,
    status text NOT NULL DEFAULT 'completed',
    file_count integer NOT NULL DEFAULT 0,
    elapsed_ms integer NOT NULL DEFAULT 0,
    throughput_files_per_second double precision NOT NULL DEFAULT 0,
    p50_ms integer,
    p95_ms integer,
    max_ms integer,
    warm_state text NOT NULL DEFAULT 'cold',
    cache_hits integer NOT NULL DEFAULT 0,
    cache_misses integer NOT NULL DEFAULT 0,
    jobs_queued integer NOT NULL DEFAULT 0,
    jobs_completed integer NOT NULL DEFAULT 0,
    jobs_blocked integer NOT NULL DEFAULT 0,
    worker_family_breakdown jsonb NOT NULL DEFAULT '{}'::jsonb,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_fixture_created
    ON acceleration_benchmark_runs (fixture, created_at DESC);

CREATE TABLE IF NOT EXISTS crawl_path_manifests (
    root_id uuid NOT NULL REFERENCES monitored_roots(id) ON DELETE CASCADE,
    path text NOT NULL,
    size_bytes bigint NOT NULL DEFAULT 0,
    mtime_ns bigint NOT NULL DEFAULT 0,
    quick_hash text,
    content_hash text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (root_id, path)
);

CREATE INDEX IF NOT EXISTS idx_crawl_path_manifests_hash
    ON crawl_path_manifests (root_id, quick_hash, content_hash);
