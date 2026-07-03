CREATE TABLE IF NOT EXISTS model_activity_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    service text NOT NULL CHECK (length(service) > 0),
    endpoint text NOT NULL DEFAULT '',
    action text NOT NULL DEFAULT '',
    activity_class text NOT NULL CHECK (activity_class IN ('retrieval', 'vision_ocr', 'sidecar', 'health', 'model_loading')),
    caller_surface text NOT NULL DEFAULT '',
    model text NOT NULL DEFAULT '',
    status text NOT NULL CHECK (status IN ('running', 'completed', 'failed', 'busy', 'stale_running')),
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    duration_ms integer CHECK (duration_ms IS NULL OR duration_ms >= 0),
    error_class text,
    error_message text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_model_activity_events_started
    ON model_activity_events (started_at DESC);

CREATE INDEX IF NOT EXISTS idx_model_activity_events_running
    ON model_activity_events (started_at DESC)
    WHERE status = 'running';

CREATE INDEX IF NOT EXISTS idx_model_activity_events_service_started
    ON model_activity_events (service, started_at DESC);

CREATE INDEX IF NOT EXISTS idx_model_activity_events_class_started
    ON model_activity_events (activity_class, started_at DESC);
