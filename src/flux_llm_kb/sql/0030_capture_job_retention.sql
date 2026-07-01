ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS delete_requested_at timestamptz,
    ADD COLUMN IF NOT EXISTS delete_requested_by text,
    ADD COLUMN IF NOT EXISTS delete_reason text;

CREATE INDEX IF NOT EXISTS idx_capture_jobs_completed_retention
    ON capture_jobs (COALESCE(completed_at, updated_at))
    WHERE job_type LIKE 'corpus_%'
      AND status = 'completed';

CREATE INDEX IF NOT EXISTS idx_capture_jobs_marked_retention
    ON capture_jobs (COALESCE(completed_at, updated_at))
    WHERE job_type LIKE 'corpus_%'
      AND delete_requested_at IS NOT NULL
      AND (
          status = 'failed'
          OR status LIKE 'blocked_%'
          OR status LIKE 'cancelled_%'
      );
