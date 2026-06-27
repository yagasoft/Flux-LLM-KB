ALTER TABLE outlook_sync_requests
    ADD COLUMN IF NOT EXISTS cancelled_by text,
    ADD COLUMN IF NOT EXISTS cancelled_at timestamptz;

