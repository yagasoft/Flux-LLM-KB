ALTER TABLE mail_messages
    ADD COLUMN IF NOT EXISTS post_process_policy text,
    ADD COLUMN IF NOT EXISTS post_process_status text,
    ADD COLUMN IF NOT EXISTS post_process_action text,
    ADD COLUMN IF NOT EXISTS post_process_error text,
    ADD COLUMN IF NOT EXISTS post_process_dry_run boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS post_processed_at timestamptz,
    ADD COLUMN IF NOT EXISTS post_process_metadata jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE TABLE IF NOT EXISTS mail_post_process_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid REFERENCES mail_profiles(id) ON DELETE SET NULL,
    sync_run_id uuid REFERENCES mail_sync_runs(id) ON DELETE SET NULL,
    mail_message_id uuid REFERENCES mail_messages(id) ON DELETE SET NULL,
    provider text NOT NULL,
    policy text NOT NULL,
    action text NOT NULL,
    status text NOT NULL,
    dry_run boolean NOT NULL DEFAULT false,
    commands jsonb NOT NULL DEFAULT '[]'::jsonb,
    error text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_mail_post_process_events_profile
    ON mail_post_process_events (profile_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_post_process_events_message
    ON mail_post_process_events (mail_message_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_post_process_events_status
    ON mail_post_process_events (status, created_at DESC);
