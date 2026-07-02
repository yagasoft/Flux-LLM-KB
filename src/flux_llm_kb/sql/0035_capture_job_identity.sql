CREATE OR REPLACE FUNCTION capture_job_identity(job_type text, payload jsonb)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT encode(
        digest(
            COALESCE(job_type, '') || ':' ||
            (
                jsonb_strip_nulls(
                    COALESCE(payload, '{}'::jsonb)
                    - 'reason'
                    - 'status'
                    - 'requested_by'
                    - 'requested_at'
                    - 'paths_total'
                    - 'path_batch_index'
                    - 'path_batch_total'
                )
            )::text,
            'sha256'
        ),
        'hex'
    );
$$;

ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS identity_key text
    GENERATED ALWAYS AS (capture_job_identity(job_type, payload)) STORED;

WITH ranked AS (
    SELECT id,
           ROW_NUMBER() OVER (
               PARTITION BY identity_key
               ORDER BY
                   CASE
                       WHEN status = 'running' THEN 0
                       WHEN status IN ('pending', 'retrying_locked', 'retrying_vss_failed') THEN 1
                       ELSE 2
                   END,
                   updated_at DESC NULLS LAST,
                   created_at DESC NULLS LAST,
                   id DESC
           ) AS row_rank
    FROM capture_jobs
),
deleted_jobs AS (
    DELETE FROM capture_jobs job
    USING ranked
    WHERE job.id = ranked.id
      AND ranked.row_rank > 1
    RETURNING job.id
)
INSERT INTO audit_events (event_type, target_table, details)
SELECT 'capture_job.identity_duplicates_deleted',
       'capture_jobs',
       jsonb_build_object('deleted', count(*))
FROM deleted_jobs
HAVING count(*) > 0;

CREATE UNIQUE INDEX IF NOT EXISTS idx_capture_jobs_identity_key
    ON capture_jobs (identity_key);
