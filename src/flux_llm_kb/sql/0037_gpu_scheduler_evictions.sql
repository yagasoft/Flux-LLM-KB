CREATE TABLE IF NOT EXISTS gpu_evictions (
    id bigserial PRIMARY KEY,
    lease_id text REFERENCES gpu_leases (id) ON DELETE SET NULL,
    task_type text NOT NULL,
    model_id text NOT NULL,
    component text NOT NULL DEFAULT '',
    status text NOT NULL CHECK (status IN ('succeeded', 'failed', 'skipped')),
    estimated_freed_vram_mb integer NOT NULL DEFAULT 0 CHECK (estimated_freed_vram_mb >= 0),
    error text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_created
    ON gpu_evictions (created_at DESC);

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_status_created
    ON gpu_evictions (status, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_gpu_evictions_lease
    ON gpu_evictions (lease_id);
