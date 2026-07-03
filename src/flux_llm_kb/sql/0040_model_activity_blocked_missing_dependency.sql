ALTER TABLE model_activity_events
    DROP CONSTRAINT IF EXISTS model_activity_events_status_check;

ALTER TABLE model_activity_events
    ADD CONSTRAINT model_activity_events_status_check
    CHECK (status IN ('running', 'completed', 'failed', 'busy', 'stale_running', 'blocked_missing_dependency'));
