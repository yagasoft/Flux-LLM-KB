ALTER TABLE mail_profiles
    ADD COLUMN IF NOT EXISTS sync_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS sync_interval_seconds integer NOT NULL DEFAULT 900,
    ADD COLUMN IF NOT EXISTS sync_window_days integer NOT NULL DEFAULT 30,
    ADD COLUMN IF NOT EXISTS max_messages_per_run integer NOT NULL DEFAULT 200,
    ADD COLUMN IF NOT EXISTS last_sync_at timestamptz,
    ADD COLUMN IF NOT EXISTS next_sync_at timestamptz;

CREATE TABLE IF NOT EXISTS outlook_host_state (
    host_id text PRIMARY KEY,
    status text NOT NULL,
    process_id integer,
    heartbeat_at timestamptz NOT NULL DEFAULT now(),
    last_error text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS outlook_sync_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid NOT NULL REFERENCES mail_profiles(id) ON DELETE CASCADE,
    requested_by text NOT NULL DEFAULT 'system',
    status text NOT NULL DEFAULT 'pending',
    claimed_by text,
    claimed_at timestamptz,
    completed_at timestamptz,
    error text,
    result jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_mail_profiles_sync_due
    ON mail_profiles (source_type, enabled, sync_enabled, next_sync_at);

CREATE INDEX IF NOT EXISTS idx_outlook_sync_requests_pending
    ON outlook_sync_requests (status, created_at);

CREATE INDEX IF NOT EXISTS idx_outlook_host_state_status
    ON outlook_host_state (status, heartbeat_at DESC);
