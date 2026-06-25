ALTER TABLE acceleration_benchmark_runs
    ADD COLUMN IF NOT EXISTS scope_type text NOT NULL DEFAULT 'synthetic',
    ADD COLUMN IF NOT EXISTS scope_hash text,
    ADD COLUMN IF NOT EXISTS deployment_label text,
    ADD COLUMN IF NOT EXISTS build_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS settings_snapshot jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS model_telemetry jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS recommendation_metadata jsonb NOT NULL DEFAULT '{}'::jsonb;

CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_scope_deployment
    ON acceleration_benchmark_runs (scope_type, deployment_label, created_at DESC);
