ALTER TABLE mail_sync_runs
    ADD COLUMN IF NOT EXISTS trigger text NOT NULL DEFAULT 'legacy',
    ADD COLUMN IF NOT EXISTS requested_by text NOT NULL DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS claimed_by text,
    ADD COLUMN IF NOT EXISTS claimed_at timestamptz,
    ADD COLUMN IF NOT EXISTS worker_id text,
    ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_error text,
    ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz,
    ADD COLUMN IF NOT EXISTS drift_seconds integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS missed_runs integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS idx_mail_sync_runs_scheduler_due
    ON mail_sync_runs (status, next_attempt_at, started_at);

CREATE INDEX IF NOT EXISTS idx_mail_sync_runs_active_profile
    ON mail_sync_runs (profile_id, status)
    WHERE status IN ('queued', 'claimed', 'running', 'backoff');

CREATE INDEX IF NOT EXISTS idx_mail_sync_runs_profile_updated
    ON mail_sync_runs (profile_id, updated_at DESC);
