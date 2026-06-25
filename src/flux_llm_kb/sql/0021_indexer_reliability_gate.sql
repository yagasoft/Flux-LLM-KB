CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_scenario_created
    ON acceleration_benchmark_runs ((recommendation_metadata->>'scenario'), created_at DESC);

CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_scope_hash_created
    ON acceleration_benchmark_runs (scope_type, scope_hash, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_recommendation_scenario
    ON acceleration_benchmark_runs USING GIN (recommendation_metadata);
