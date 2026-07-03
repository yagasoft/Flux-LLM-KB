CREATE TABLE IF NOT EXISTS gpu_leases (
    id text PRIMARY KEY,
    task_type text NOT NULL,
    model_id text NOT NULL DEFAULT '',
    status text NOT NULL CHECK (status IN ('waiting', 'running', 'released', 'timed_out', 'recovered', 'rejected')),
    estimated_vram_mb integer NOT NULL DEFAULT 0 CHECK (estimated_vram_mb >= 0),
    exclusive boolean NOT NULL DEFAULT true,
    share_group text NOT NULL DEFAULT '',
    priority integer NOT NULL DEFAULT 0,
    component text NOT NULL DEFAULT '',
    request_id text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    granted_at timestamptz,
    heartbeat_at timestamptz,
    expires_at timestamptz,
    released_at timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS idx_gpu_leases_status_queue
    ON gpu_leases (status, priority DESC, created_at ASC);

CREATE INDEX IF NOT EXISTS idx_gpu_leases_running_expiry
    ON gpu_leases (expires_at)
    WHERE status = 'running';

CREATE INDEX IF NOT EXISTS idx_gpu_leases_recent
    ON gpu_leases (created_at DESC);

CREATE TABLE IF NOT EXISTS gpu_model_residency (
    model_id text NOT NULL,
    task_type text NOT NULL,
    estimated_vram_mb integer NOT NULL DEFAULT 0 CHECK (estimated_vram_mb >= 0),
    resident boolean NOT NULL DEFAULT true,
    last_used_at timestamptz NOT NULL DEFAULT now(),
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (model_id, task_type)
);

CREATE INDEX IF NOT EXISTS idx_gpu_model_residency_resident
    ON gpu_model_residency (resident, last_used_at DESC);
