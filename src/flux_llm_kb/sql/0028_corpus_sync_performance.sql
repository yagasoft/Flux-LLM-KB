ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS progress_heartbeat_at timestamptz;

CREATE INDEX IF NOT EXISTS idx_capture_jobs_stale_running_sync
    ON capture_jobs (job_type, status, progress_heartbeat_at, locked_at, started_at)
    WHERE job_type = 'corpus_sync_root'
      AND status = 'running';
