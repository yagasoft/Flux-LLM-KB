ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS job_family text NOT NULL DEFAULT 'general',
    ADD COLUMN IF NOT EXISTS resource_class text NOT NULL DEFAULT 'cpu',
    ADD COLUMN IF NOT EXISTS priority integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS time_budget_seconds integer,
    ADD COLUMN IF NOT EXISTS started_at timestamptz,
    ADD COLUMN IF NOT EXISTS completed_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_duration_ms integer,
    ADD COLUMN IF NOT EXISTS telemetry jsonb NOT NULL DEFAULT '{}'::jsonb;

UPDATE capture_jobs
SET job_family = CASE job_type
        WHEN 'corpus_extract_text' THEN 'text'
        WHEN 'corpus_extract_code' THEN 'text'
        WHEN 'corpus_extract_document' THEN 'office'
        WHEN 'corpus_extract_pdf' THEN 'office'
        WHEN 'corpus_extract_spreadsheet' THEN 'office'
        WHEN 'corpus_extract_presentation' THEN 'office'
        WHEN 'corpus_extract_image' THEN 'image'
        WHEN 'corpus_extract_diagram' THEN 'diagram'
        WHEN 'corpus_extract_archive' THEN 'archive'
        WHEN 'corpus_extract_container' THEN 'archive'
        WHEN 'corpus_extract_audio' THEN 'media'
        WHEN 'corpus_extract_video' THEN 'media'
        WHEN 'search_index_sync' THEN 'embedding'
        WHEN 'corpus_preview' THEN 'preview'
        ELSE 'general'
    END,
    resource_class = CASE job_type
        WHEN 'corpus_extract_image' THEN 'gpu'
        WHEN 'corpus_extract_audio' THEN 'gpu'
        WHEN 'corpus_extract_video' THEN 'gpu'
        WHEN 'search_index_sync' THEN 'gpu'
        WHEN 'corpus_extract_archive' THEN 'io'
        WHEN 'corpus_extract_container' THEN 'io'
        ELSE 'cpu'
    END,
    priority = CASE job_type
        WHEN 'corpus_extract_text' THEN 80
        WHEN 'corpus_extract_code' THEN 80
        WHEN 'corpus_extract_document' THEN 70
        WHEN 'corpus_extract_pdf' THEN 70
        WHEN 'corpus_extract_spreadsheet' THEN 70
        WHEN 'corpus_extract_presentation' THEN 70
        WHEN 'corpus_extract_diagram' THEN 65
        WHEN 'corpus_extract_archive' THEN 55
        WHEN 'corpus_extract_container' THEN 55
        WHEN 'corpus_extract_image' THEN 45
        WHEN 'corpus_extract_audio' THEN 40
        WHEN 'corpus_extract_video' THEN 40
        WHEN 'search_index_sync' THEN 35
        WHEN 'corpus_preview' THEN 25
        ELSE 10
    END,
    time_budget_seconds = CASE job_type
        WHEN 'corpus_extract_text' THEN 120
        WHEN 'corpus_extract_code' THEN 120
        WHEN 'corpus_extract_document' THEN 300
        WHEN 'corpus_extract_pdf' THEN 300
        WHEN 'corpus_extract_spreadsheet' THEN 300
        WHEN 'corpus_extract_presentation' THEN 300
        WHEN 'corpus_extract_diagram' THEN 180
        WHEN 'corpus_extract_archive' THEN 300
        WHEN 'corpus_extract_container' THEN 300
        WHEN 'corpus_extract_image' THEN 600
        WHEN 'corpus_extract_audio' THEN 900
        WHEN 'corpus_extract_video' THEN 900
        WHEN 'search_index_sync' THEN 300
        WHEN 'corpus_preview' THEN 180
        ELSE 180
    END
WHERE job_type LIKE 'corpus_%';

DO $$
BEGIN
    ALTER TABLE capture_jobs
        ADD CONSTRAINT capture_jobs_job_family_allowed
        CHECK (job_family IN ('text', 'office', 'image', 'diagram', 'archive', 'media', 'embedding', 'preview', 'general'));
EXCEPTION WHEN duplicate_object THEN
    NULL;
END $$;

DO $$
BEGIN
    ALTER TABLE capture_jobs
        ADD CONSTRAINT capture_jobs_resource_class_allowed
        CHECK (resource_class IN ('cpu', 'gpu', 'io'));
EXCEPTION WHEN duplicate_object THEN
    NULL;
END $$;

CREATE INDEX IF NOT EXISTS idx_capture_jobs_family_claim
    ON capture_jobs (job_family, status, priority DESC, next_attempt_at, created_at)
    WHERE job_type LIKE 'corpus_%';

CREATE INDEX IF NOT EXISTS idx_capture_jobs_family_status
    ON capture_jobs (job_family, status, updated_at DESC)
    WHERE job_type LIKE 'corpus_%';
