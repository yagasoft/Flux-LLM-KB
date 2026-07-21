CREATE TABLE IF NOT EXISTS gpu_vram_samples (
    id bigserial PRIMARY KEY,
    task_type text NOT NULL,
    model_id text NOT NULL,
    shape_bucket text NOT NULL,
    load_delta_mb integer NOT NULL,
    working_set_mb integer NOT NULL,
    allocator_capability text NOT NULL DEFAULT 'measured',
    tracker_overlapped boolean NOT NULL DEFAULT false,
    observed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_gpu_vram_samples_shape_observed
    ON gpu_vram_samples (task_type, model_id, shape_bucket, observed_at DESC, id DESC);
