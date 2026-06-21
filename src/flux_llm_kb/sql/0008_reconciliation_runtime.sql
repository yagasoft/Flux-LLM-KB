ALTER TABLE crawl_runs
    ADD COLUMN IF NOT EXISTS reason text NOT NULL DEFAULT 'manual_sync';

CREATE INDEX IF NOT EXISTS idx_crawl_runs_reason_started ON crawl_runs (reason, started_at DESC);
