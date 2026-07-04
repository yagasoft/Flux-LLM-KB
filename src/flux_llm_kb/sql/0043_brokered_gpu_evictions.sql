ALTER TABLE gpu_evictions
    DROP CONSTRAINT IF EXISTS gpu_evictions_status_check;

ALTER TABLE gpu_evictions
    ADD CONSTRAINT gpu_evictions_status_check
    CHECK (status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'skipped'));

ALTER TABLE gpu_evictions
    ADD COLUMN IF NOT EXISTS broker_message_id text,
    ADD COLUMN IF NOT EXISTS routing_key text,
    ADD COLUMN IF NOT EXISTS correlation_id text,
    ADD COLUMN IF NOT EXISTS causation_id text,
    ADD COLUMN IF NOT EXISTS queued_at timestamptz,
    ADD COLUMN IF NOT EXISTS started_at timestamptz,
    ADD COLUMN IF NOT EXISTS broker_delivery_count integer NOT NULL DEFAULT 0;

UPDATE gpu_evictions
   SET queued_at = COALESCE(queued_at, created_at)
 WHERE queued_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_active
    ON gpu_evictions (status, queued_at ASC, id ASC)
    WHERE status IN ('queued', 'running', 'retrying');

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_broker_message
    ON gpu_evictions (broker_message_id)
    WHERE broker_message_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_correlation
    ON gpu_evictions (correlation_id)
    WHERE correlation_id IS NOT NULL;
