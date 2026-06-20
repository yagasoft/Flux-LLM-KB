CREATE TABLE IF NOT EXISTS mail_oauth_states (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid NOT NULL REFERENCES mail_profiles(id) ON DELETE CASCADE,
    provider text NOT NULL,
    state text NOT NULL UNIQUE,
    code_verifier text NOT NULL,
    redirect_uri text NOT NULL,
    client_config jsonb NOT NULL DEFAULT '{}'::jsonb,
    client_config_path text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL DEFAULT now() + interval '10 minutes',
    consumed_at timestamptz
);

CREATE TABLE IF NOT EXISTS mail_oauth_tokens (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    profile_id uuid NOT NULL REFERENCES mail_profiles(id) ON DELETE CASCADE,
    provider text NOT NULL,
    refresh_token text NOT NULL,
    scope text NOT NULL,
    token_type text NOT NULL DEFAULT 'Bearer',
    status text NOT NULL DEFAULT 'configured',
    last_error text,
    client_config jsonb NOT NULL DEFAULT '{}'::jsonb,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    refreshed_at timestamptz,
    expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (profile_id, provider)
);

CREATE INDEX IF NOT EXISTS idx_mail_oauth_states_profile_created
    ON mail_oauth_states (profile_id, provider, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_mail_oauth_tokens_profile_status
    ON mail_oauth_tokens (profile_id, provider, status);
