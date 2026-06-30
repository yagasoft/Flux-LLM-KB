CREATE TABLE IF NOT EXISTS capture_job_tool_invocations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id uuid NOT NULL REFERENCES capture_jobs(id) ON DELETE CASCADE,
    command jsonb NOT NULL DEFAULT '[]'::jsonb,
    cwd text,
    status text NOT NULL DEFAULT 'running',
    return_code integer,
    stdout text NOT NULL DEFAULT '',
    stderr text NOT NULL DEFAULT '',
    exception_type text,
    exception_message text,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    duration_ms integer,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT capture_job_tool_invocations_status_allowed
        CHECK (status IN ('running', 'completed', 'failed', 'timeout', 'exception'))
);

CREATE INDEX IF NOT EXISTS idx_capture_job_tool_invocations_job_started
    ON capture_job_tool_invocations (job_id, started_at, id);

CREATE INDEX IF NOT EXISTS idx_capture_job_tool_invocations_retention
    ON capture_job_tool_invocations (job_id, updated_at)
    WHERE status IN ('completed', 'failed', 'timeout', 'exception');
