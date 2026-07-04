CREATE TABLE IF NOT EXISTS message_outbox (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    message_id text NOT NULL UNIQUE,
    exchange text NOT NULL,
    routing_key text NOT NULL,
    message_type text NOT NULL,
    schema_version integer NOT NULL DEFAULT 1,
    correlation_id text,
    causation_id text,
    aggregate_type text,
    aggregate_id text,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    headers jsonb NOT NULL DEFAULT '{}'::jsonb,
    status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'publishing', 'published', 'failed')),
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_at timestamptz,
    locked_by text,
    published_at timestamptz,
    broker_message_id text,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_message_outbox_pending
    ON message_outbox (status, next_attempt_at, created_at)
    WHERE status IN ('pending', 'failed');

CREATE INDEX IF NOT EXISTS idx_message_outbox_aggregate
    ON message_outbox (aggregate_type, aggregate_id, created_at DESC);

CREATE TABLE IF NOT EXISTS message_inbox (
    consumer_name text NOT NULL,
    message_id text NOT NULL,
    message_type text NOT NULL,
    status text NOT NULL DEFAULT 'processing'
        CHECK (status IN ('processing', 'handled', 'failed')),
    attempts integer NOT NULL DEFAULT 1,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    handled_at timestamptz,
    last_error text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (consumer_name, message_id)
);

CREATE INDEX IF NOT EXISTS idx_message_inbox_status
    ON message_inbox (status, last_seen_at DESC);

CREATE TABLE IF NOT EXISTS callback_deliveries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    message_id text NOT NULL UNIQUE,
    event_message_id text,
    job_id uuid,
    callback_url text NOT NULL,
    status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'running', 'delivered', 'retrying', 'failed', 'blocked')),
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_status_code integer,
    last_error text,
    idempotency_key text NOT NULL,
    headers jsonb NOT NULL DEFAULT '{}'::jsonb,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz
);

CREATE INDEX IF NOT EXISTS idx_callback_deliveries_pending
    ON callback_deliveries (status, next_attempt_at, created_at)
    WHERE status IN ('pending', 'retrying');

CREATE INDEX IF NOT EXISTS idx_callback_deliveries_job
    ON callback_deliveries (job_id, created_at DESC);

ALTER TABLE capture_jobs
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS causation_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS queued_at timestamptz,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_capture_jobs_broker_message
    ON capture_jobs (broker_message_id)
    WHERE broker_message_id IS NOT NULL;

ALTER TABLE mail_sync_runs
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS queued_at timestamptz,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;

ALTER TABLE outlook_sync_requests
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS queued_at timestamptz,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;

ALTER TABLE runtime_control_requests
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS queued_at timestamptz,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;

ALTER TABLE gpu_leases
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;
