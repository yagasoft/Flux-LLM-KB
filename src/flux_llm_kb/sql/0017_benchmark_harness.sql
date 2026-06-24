ALTER TABLE acceleration_benchmark_runs
    ADD COLUMN IF NOT EXISTS mode text NOT NULL DEFAULT 'scan',
    ADD COLUMN IF NOT EXISTS label text,
    ADD COLUMN IF NOT EXISTS compare_label text,
    ADD COLUMN IF NOT EXISTS pass_index integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS hash_parallelism integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS worker_count integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS manifest_skipped_unchanged integer NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_acceleration_benchmark_runs_compare
    ON acceleration_benchmark_runs (fixture, mode, file_count, warm_state, label, created_at DESC);
