ALTER TABLE retention_policies
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_by text NOT NULL DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'retention_policies_half_life_positive'
    ) THEN
        ALTER TABLE retention_policies
            ADD CONSTRAINT retention_policies_half_life_positive CHECK (half_life_days > 0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'retention_policies_min_confidence_range'
    ) THEN
        ALTER TABLE retention_policies
            ADD CONSTRAINT retention_policies_min_confidence_range CHECK (min_confidence >= 0.0 AND min_confidence <= 1.0);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'retention_policies_action_allowed'
    ) THEN
        ALTER TABLE retention_policies
            ADD CONSTRAINT retention_policies_action_allowed CHECK (action IN ('review', 'deprioritize', 'retire'));
    END IF;
END
$$;

INSERT INTO retention_policies (memory_class, half_life_days, min_confidence, action, updated_by, metadata)
VALUES ('episode', 180, 0.25, 'review', 'migration', '{"seeded_by":"0012_retention_quality"}'::jsonb)
ON CONFLICT (memory_class) DO NOTHING;

INSERT INTO retention_policies (memory_class, half_life_days, min_confidence, action, updated_by, metadata)
VALUES ('claim', 120, 0.35, 'review', 'migration', '{"seeded_by":"0012_retention_quality"}'::jsonb)
ON CONFLICT (memory_class) DO NOTHING;

INSERT INTO retention_policies (memory_class, half_life_days, min_confidence, action, updated_by, metadata)
VALUES ('corpus', 365, 0.20, 'deprioritize', 'migration', '{"seeded_by":"0012_retention_quality"}'::jsonb)
ON CONFLICT (memory_class) DO NOTHING;
