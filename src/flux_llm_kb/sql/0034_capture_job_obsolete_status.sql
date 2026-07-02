UPDATE capture_jobs
SET status = 'obsolete',
    telemetry = jsonb_strip_nulls(
        COALESCE(telemetry, '{}'::jsonb) || jsonb_build_object(
            'obsolete_previous_status', status,
            'obsolete_previous_result_status', telemetry->>'result_status',
            'result_status', 'obsolete'
        )
    ),
    locked_at = NULL,
    locked_by = NULL,
    updated_at = now()
WHERE job_type LIKE 'corpus_%'
  AND delete_requested_at IS NOT NULL
  AND status <> 'obsolete';

DROP INDEX IF EXISTS idx_capture_jobs_marked_retention;

CREATE INDEX IF NOT EXISTS idx_capture_jobs_marked_retention
    ON capture_jobs (COALESCE(completed_at, updated_at))
    WHERE job_type LIKE 'corpus_%'
      AND delete_requested_at IS NOT NULL
      AND status = 'obsolete';
