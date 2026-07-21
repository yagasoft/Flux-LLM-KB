ALTER TABLE gpu_model_residency
    ADD COLUMN IF NOT EXISTS runtime_state text NOT NULL DEFAULT 'unknown',
    ADD COLUMN IF NOT EXISTS owner_component text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_generation text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_fingerprint text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_activity_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS runtime_in_flight integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_operation_started_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_operation_completed_at timestamptz,
    ADD COLUMN IF NOT EXISTS runtime_observed_at timestamptz,
    ADD COLUMN IF NOT EXISTS runtime_failure_reason text NOT NULL DEFAULT '';

ALTER TABLE gpu_leases
    ADD COLUMN IF NOT EXISTS admission_key text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS priority_class text NOT NULL DEFAULT 'background',
    ADD COLUMN IF NOT EXISTS wait_reason text NOT NULL DEFAULT 'queue_wait',
    ADD COLUMN IF NOT EXISTS linked_eviction_id bigint REFERENCES gpu_evictions(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS shape_bucket text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reserved_peak_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS load_delta_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS working_set_vram_mb integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS reconciliation_observation_id text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS caller_attached boolean NOT NULL DEFAULT true;

CREATE UNIQUE INDEX IF NOT EXISTS idx_gpu_leases_active_admission_key
    ON gpu_leases (admission_key)
    WHERE admission_key <> '' AND status IN ('waiting', 'running');

ALTER TABLE gpu_evictions DROP CONSTRAINT IF EXISTS gpu_evictions_status_check;
ALTER TABLE gpu_evictions ADD CONSTRAINT gpu_evictions_status_check
    CHECK (status IN ('queued', 'running', 'retrying', 'succeeded', 'failed', 'skipped', 'expired'));
ALTER TABLE gpu_evictions
    ADD COLUMN IF NOT EXISTS runtime_generation text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_activity_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS claim_token text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS status_changed_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS heartbeat_at timestamptz,
    ADD COLUMN IF NOT EXISTS retry_not_before timestamptz,
    ADD COLUMN IF NOT EXISTS expires_at timestamptz,
    ADD COLUMN IF NOT EXISTS request_reason text NOT NULL DEFAULT 'demand',
    ADD COLUMN IF NOT EXISTS terminal_reason text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reconciliation_observation_id text NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS gpu_runtime_inventory (
    id bigserial PRIMARY KEY,
    observation_id text NOT NULL,
    component text NOT NULL,
    owner_component text NOT NULL DEFAULT '',
    process_generation text NOT NULL DEFAULT '',
    runtime_fingerprint text NOT NULL DEFAULT '',
    state text NOT NULL,
    allocator_capability text NOT NULL DEFAULT 'unknown',
    driver_used_mb integer,
    driver_free_mb integer,
    known_measured_mb integer NOT NULL DEFAULT 0,
    known_reported_mb integer NOT NULL DEFAULT 0,
    context_allowance_mb integer NOT NULL DEFAULT 0,
    unresolved_known_owner_mb integer NOT NULL DEFAULT 0,
    unattributed_mb integer NOT NULL DEFAULT 0,
    models jsonb NOT NULL DEFAULT '[]'::jsonb,
    allocators jsonb NOT NULL DEFAULT '[]'::jsonb,
    error_code text NOT NULL DEFAULT '',
    error_metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    observed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (observation_id, component)
);

CREATE INDEX IF NOT EXISTS idx_gpu_runtime_inventory_component_observed
    ON gpu_runtime_inventory (component, observed_at DESC);

CREATE TABLE IF NOT EXISTS gpu_model_vram_calibration (
    id bigserial PRIMARY KEY,
    task_type text NOT NULL,
    model_id text NOT NULL,
    owner_component text NOT NULL,
    device text NOT NULL,
    shape_bucket text NOT NULL,
    resident_floor_mb integer NOT NULL DEFAULT 0,
    load_delta_mb integer NOT NULL DEFAULT 0,
    working_set_mb integer NOT NULL DEFAULT 0,
    guard_margin_mb integer NOT NULL DEFAULT 0,
    sample_count integer NOT NULL DEFAULT 0,
    recent_samples jsonb NOT NULL DEFAULT '[]'::jsonb,
    calibration_source text NOT NULL DEFAULT 'configured_seed',
    observed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (task_type, model_id, owner_component, device, shape_bucket)
);
