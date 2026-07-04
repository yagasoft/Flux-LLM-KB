ALTER TABLE runtime_control_requests
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
