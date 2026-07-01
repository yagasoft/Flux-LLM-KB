-- Split legacy corpus blocker statuses where stored evidence is unambiguous.

UPDATE capture_jobs
SET status = 'blocked_by_policy',
    telemetry = COALESCE(telemetry, '{}'::jsonb) || jsonb_build_object('result_status', 'blocked_by_policy'),
    updated_at = now()
WHERE status = 'blocked_missing_dependency'
  AND job_type LIKE 'corpus_%'
  AND (
      last_error = 'text file exceeds inline extraction limit'
      OR last_error LIKE 'Strict indexing requires full content extraction;%'
      OR telemetry->>'reason' = 'inline_extraction_limit'
      OR telemetry->>'readiness_status' = 'blocked_by_policy'
      OR (
          telemetry->>'metadata_only_blocked' = 'true'
          AND telemetry->>'readiness_status' = 'blocked_missing_dependency'
      )
  );

UPDATE capture_jobs
SET status = 'blocked_invalid_source',
    telemetry = COALESCE(telemetry, '{}'::jsonb) || jsonb_build_object('result_status', 'blocked_invalid_source'),
    updated_at = now()
WHERE status = 'blocked_missing_dependency'
  AND job_type LIKE 'corpus_%'
  AND (
      telemetry->>'reason' = 'invalid_package'
      OR last_error ILIKE '%Package not found%'
      OR last_error ILIKE '%BadZipFile%'
      OR last_error ILIKE '%File is not a zip file%'
  );

UPDATE source_assets
SET extraction_status = 'blocked_by_policy',
    metadata = COALESCE(metadata, '{}'::jsonb)
        || jsonb_build_object('readiness_status', 'blocked_by_policy'),
    updated_at = now()
WHERE extraction_status = 'blocked_missing_dependency'
  AND (
      metadata->>'reason' = 'inline_extraction_limit'
      OR metadata->>'readiness_status' = 'blocked_by_policy'
      OR metadata->>'readiness_reason' LIKE 'Strict indexing requires full content extraction;%'
      OR metadata->>'metadata_only_blocked' = 'true'
  );

UPDATE source_assets
SET extraction_status = 'blocked_invalid_source',
    metadata = COALESCE(metadata, '{}'::jsonb)
        || jsonb_build_object('readiness_status', 'blocked_invalid_source'),
    updated_at = now()
WHERE extraction_status = 'blocked_missing_dependency'
  AND (
      metadata->>'reason' = 'invalid_package'
      OR metadata->>'error' ILIKE '%Package not found%'
      OR metadata->>'error' ILIKE '%BadZipFile%'
      OR metadata->>'error' ILIKE '%File is not a zip file%'
  );
