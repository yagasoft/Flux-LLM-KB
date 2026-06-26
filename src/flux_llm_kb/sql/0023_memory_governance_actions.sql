CREATE TABLE IF NOT EXISTS memory_governance_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    mode text NOT NULL DEFAULT 'shadow',
    trigger text NOT NULL DEFAULT 'manual',
    status text NOT NULL DEFAULT 'completed',
    actor text NOT NULL DEFAULT 'system',
    policy_snapshot jsonb NOT NULL DEFAULT '{}'::jsonb,
    gate jsonb NOT NULL DEFAULT '{}'::jsonb,
    summary jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    memory_mutated boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT memory_governance_runs_mode_allowed
        CHECK (mode IN ('shadow', 'manual', 'auto')),
    CONSTRAINT memory_governance_runs_status_allowed
        CHECK (status IN ('completed', 'blocked', 'failed'))
);

CREATE TABLE IF NOT EXISTS memory_governance_actions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id uuid REFERENCES memory_governance_runs(id) ON DELETE SET NULL,
    action text NOT NULL,
    target_type text NOT NULL,
    target_id text NOT NULL,
    memory_class text,
    risk text NOT NULL DEFAULT 'medium',
    status text NOT NULL DEFAULT 'proposed',
    source text NOT NULL DEFAULT 'governance',
    actor text NOT NULL DEFAULT 'system',
    rationale jsonb NOT NULL DEFAULT '{}'::jsonb,
    evidence jsonb NOT NULL DEFAULT '{}'::jsonb,
    before_state jsonb NOT NULL DEFAULT '{}'::jsonb,
    after_state jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    memory_mutated boolean NOT NULL DEFAULT false,
    audit_event_id uuid,
    error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    applied_at timestamptz,
    recovered_at timestamptz,
    CONSTRAINT memory_governance_actions_action_allowed
        CHECK (action IN (
            'mark_review',
            'stale_tag',
            'deprioritize',
            'retire',
            'semantic_cluster_apply',
            'canonical_cluster_promote',
            'capture_ingestion_recheck',
            'feedback_gap_escalate',
            'recover'
        )),
    CONSTRAINT memory_governance_actions_risk_allowed
        CHECK (risk IN ('low', 'medium', 'high')),
    CONSTRAINT memory_governance_actions_status_allowed
        CHECK (status IN (
            'proposed',
            'blocked',
            'skipped_duplicate',
            'skipped_conflict',
            'applied',
            'recovered',
            'failed'
        )),
    CONSTRAINT memory_governance_actions_memory_class_allowed
        CHECK (memory_class IS NULL OR memory_class IN ('claim', 'episode', 'corpus'))
);

CREATE TABLE IF NOT EXISTS memory_governance_digests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id uuid REFERENCES memory_governance_runs(id) ON DELETE SET NULL,
    actor text NOT NULL DEFAULT 'system',
    summary jsonb NOT NULL DEFAULT '{}'::jsonb,
    recommendations jsonb NOT NULL DEFAULT '[]'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    memory_mutated boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS memory_governance_policy_snapshots (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id uuid REFERENCES memory_governance_runs(id) ON DELETE SET NULL,
    policy jsonb NOT NULL DEFAULT '{}'::jsonb,
    settings_mutated boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_memory_governance_runs_created
    ON memory_governance_runs (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_memory_governance_actions_status
    ON memory_governance_actions (status, risk, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_memory_governance_actions_target
    ON memory_governance_actions (target_type, target_id, action, status);

CREATE INDEX IF NOT EXISTS idx_memory_governance_digests_created
    ON memory_governance_digests (created_at DESC);
