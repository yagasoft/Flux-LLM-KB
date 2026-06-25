CREATE TABLE IF NOT EXISTS retrieval_benchmark_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    suite text NOT NULL,
    label text,
    compare_label text,
    status text NOT NULL DEFAULT 'completed',
    query_count integer NOT NULL DEFAULT 0,
    passed_count integer NOT NULL DEFAULT 0,
    failed_count integer NOT NULL DEFAULT 0,
    metrics jsonb NOT NULL DEFAULT '{}'::jsonb,
    case_results jsonb NOT NULL DEFAULT '[]'::jsonb,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    recommendation_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_retrieval_benchmark_runs_suite_created
    ON retrieval_benchmark_runs (suite, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_retrieval_benchmark_runs_compare
    ON retrieval_benchmark_runs (suite, label, created_at DESC);
