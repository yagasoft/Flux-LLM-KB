CREATE TABLE IF NOT EXISTS code_retrieval_feedback_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    root_name text,
    scope_hash text,
    query_hash text NOT NULL,
    result_count integer NOT NULL DEFAULT 0,
    surface text NOT NULL DEFAULT 'unknown',
    miss_category text NOT NULL,
    expected_symbol_hash text,
    path_leaf text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT code_feedback_miss_category_allowed CHECK (
        miss_category IN (
            'missing_symbol',
            'wrong_root',
            'wrong_relationship',
            'parser_fallback',
            'ranking_order',
            'stale_generated',
            'other'
        )
    )
);

CREATE INDEX IF NOT EXISTS idx_code_feedback_category_created
    ON code_retrieval_feedback_events (miss_category, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_code_feedback_root_created
    ON code_retrieval_feedback_events (root_name, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_code_feedback_scope_created
    ON code_retrieval_feedback_events (scope_hash, created_at DESC);
