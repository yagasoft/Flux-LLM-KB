CREATE TABLE IF NOT EXISTS operator_automation_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    mode text NOT NULL DEFAULT 'guarded',
    trigger text NOT NULL DEFAULT 'manual',
    status text NOT NULL DEFAULT 'completed',
    actor text NOT NULL DEFAULT 'system',
    policy_snapshot jsonb NOT NULL DEFAULT '{}'::jsonb,
    summary jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    memory_mutated boolean NOT NULL DEFAULT false,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT operator_automation_runs_mode_allowed
        CHECK (mode IN ('guarded', 'suggest_only')),
    CONSTRAINT operator_automation_runs_status_allowed
        CHECK (status IN ('running', 'completed', 'blocked', 'failed'))
);

CREATE TABLE IF NOT EXISTS operator_automation_actions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id uuid REFERENCES operator_automation_runs(id) ON DELETE SET NULL,
    action text NOT NULL,
    target_type text,
    target_id text,
    risk text NOT NULL DEFAULT 'low',
    status text NOT NULL DEFAULT 'proposed',
    source text NOT NULL DEFAULT 'automation',
    actor text NOT NULL DEFAULT 'system',
    rationale jsonb NOT NULL DEFAULT '{}'::jsonb,
    evidence jsonb NOT NULL DEFAULT '{}'::jsonb,
    result jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    memory_mutated boolean NOT NULL DEFAULT false,
    audit_event_id uuid,
    error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT operator_automation_actions_action_allowed
        CHECK (action IN (
            'refresh_retrieval_evidence',
            'ingest_approved_capture',
            'safe_diagnostic_recovery',
            'sync_search_index',
            'run_governance_shadow'
        )),
    CONSTRAINT operator_automation_actions_risk_allowed
        CHECK (risk IN ('low', 'medium', 'high')),
    CONSTRAINT operator_automation_actions_status_allowed
        CHECK (status IN ('proposed', 'applied', 'skipped', 'blocked', 'failed'))
);

CREATE INDEX IF NOT EXISTS idx_operator_automation_runs_created
    ON operator_automation_runs (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_operator_automation_actions_run
    ON operator_automation_actions (run_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_operator_automation_actions_status
    ON operator_automation_actions (status, risk, created_at DESC);
