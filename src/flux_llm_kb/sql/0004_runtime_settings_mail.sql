CREATE TABLE IF NOT EXISTS runtime_settings (
    key text PRIMARY KEY,
    value jsonb NOT NULL,
    updated_by text NOT NULL DEFAULT 'system',
    reason text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS runtime_setting_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    setting_key text NOT NULL,
    old_value jsonb,
    new_value jsonb,
    actor text NOT NULL DEFAULT 'system',
    reason text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS runtime_components (
    name text PRIMARY KEY,
    status text NOT NULL DEFAULT 'unknown',
    heartbeat_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS runtime_control_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    setting_key text NOT NULL,
    action text NOT NULL,
    affected_components text[] NOT NULL DEFAULT ARRAY[]::text[],
    status text NOT NULL DEFAULT 'pending',
    actor text NOT NULL DEFAULT 'system',
    requested_at timestamptz NOT NULL DEFAULT now(),
    acknowledged_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS mail_profiles (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE,
    source_type text NOT NULL,
    account text,
    server text,
    folder_paths text[] NOT NULL DEFAULT ARRAY[]::text[],
    spool_path text NOT NULL,
    post_process_policy text NOT NULL DEFAULT 'move_to_processed',
    enabled boolean NOT NULL DEFAULT true,
    trust_rank integer NOT NULL DEFAULT 450,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS mail_messages (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid NOT NULL REFERENCES mail_profiles(id) ON DELETE CASCADE,
    source_message_id text NOT NULL,
    source_folder text NOT NULL,
    uid bigint,
    uidvalidity bigint,
    outlook_entry_id text,
    outlook_store_id text,
    internet_message_id text,
    content_hash text,
    export_id text,
    export_state text NOT NULL DEFAULT 'pending',
    error text,
    received_at timestamptz,
    exported_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (profile_id, source_folder, source_message_id)
);

CREATE TABLE IF NOT EXISTS mail_sync_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid REFERENCES mail_profiles(id) ON DELETE SET NULL,
    status text NOT NULL DEFAULT 'running',
    started_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz,
    messages_seen integer NOT NULL DEFAULT 0,
    messages_exported integer NOT NULL DEFAULT 0,
    last_cursor jsonb NOT NULL DEFAULT '{}'::jsonb,
    errors jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_runtime_setting_events_key ON runtime_setting_events (setting_key, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_runtime_control_requests_status ON runtime_control_requests (status, requested_at DESC);
CREATE INDEX IF NOT EXISTS idx_mail_profiles_enabled ON mail_profiles (enabled, source_type);
CREATE INDEX IF NOT EXISTS idx_mail_messages_profile_state ON mail_messages (profile_id, export_state, updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_mail_messages_hash ON mail_messages (content_hash) WHERE content_hash IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_mail_sync_runs_profile_started ON mail_sync_runs (profile_id, started_at DESC);
