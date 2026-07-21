CREATE INDEX IF NOT EXISTS idx_audit_events_gpu_eviction_cas_rejected_created
    ON audit_events (created_at DESC)
    WHERE event_type = 'gpu_eviction.cas_rejected';
