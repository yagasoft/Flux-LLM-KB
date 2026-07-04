CREATE TABLE IF NOT EXISTS event_journal (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    subscriber_name text NOT NULL,
    message_id text NOT NULL,
    message_type text NOT NULL,
    exchange text NOT NULL,
    routing_key text NOT NULL,
    correlation_id text,
    causation_id text,
    job_id text,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    received_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (subscriber_name, message_id)
);

CREATE INDEX IF NOT EXISTS idx_event_journal_routing
    ON event_journal (routing_key, received_at DESC);

CREATE INDEX IF NOT EXISTS idx_event_journal_correlation
    ON event_journal (correlation_id, received_at DESC)
    WHERE correlation_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_event_journal_job
    ON event_journal (job_id, received_at DESC)
    WHERE job_id IS NOT NULL;
