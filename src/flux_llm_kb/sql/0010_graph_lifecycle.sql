ALTER TABLE episodes
    ADD COLUMN IF NOT EXISTS lifecycle_state text NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS contradiction_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS retention_action text NOT NULL DEFAULT 'keep',
    ADD COLUMN IF NOT EXISTS retired_at timestamptz,
    ADD COLUMN IF NOT EXISTS stale_at timestamptz;

ALTER TABLE claims
    ADD COLUMN IF NOT EXISTS lifecycle_state text NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS usage_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS reinforcement_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS contradiction_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_reinforced_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_used_at timestamptz,
    ADD COLUMN IF NOT EXISTS retention_action text NOT NULL DEFAULT 'keep',
    ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS retired_at timestamptz,
    ADD COLUMN IF NOT EXISTS stale_at timestamptz,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE claims
    DROP COLUMN IF EXISTS search_vector;

ALTER TABLE claims
    ADD COLUMN IF NOT EXISTS search_vector tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('english', coalesce(predicate, '')), 'A') ||
        setweight(to_tsvector('english', coalesce(object_text, '')), 'B')
    ) STORED;

ALTER TABLE relations
    ADD COLUMN IF NOT EXISTS lifecycle_state text NOT NULL DEFAULT 'active',
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

CREATE TABLE IF NOT EXISTS claim_lifecycle_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    claim_id uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    transition_type text NOT NULL,
    actor text NOT NULL DEFAULT 'system',
    from_state text,
    to_state text NOT NULL,
    related_claim_id uuid REFERENCES claims(id) ON DELETE SET NULL,
    confidence_delta double precision NOT NULL DEFAULT 0,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS claim_relations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    from_claim_id uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    to_claim_id uuid NOT NULL REFERENCES claims(id) ON DELETE CASCADE,
    relation_type text NOT NULL,
    confidence double precision NOT NULL DEFAULT 0.5,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (from_claim_id, to_claim_id, relation_type)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_claims_identity
    ON claims (subject_entity_id, predicate, object_text);

CREATE UNIQUE INDEX IF NOT EXISTS idx_relations_identity
    ON relations (from_entity_id, to_entity_id, relation_type);

CREATE INDEX IF NOT EXISTS idx_claims_lifecycle
    ON claims (lifecycle_state, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_claims_search_vector
    ON claims USING GIN (search_vector);

CREATE INDEX IF NOT EXISTS idx_claim_lifecycle_events_claim
    ON claim_lifecycle_events (claim_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_claim_relations_from
    ON claim_relations (from_claim_id, relation_type);

CREATE INDEX IF NOT EXISTS idx_claim_relations_to
    ON claim_relations (to_claim_id, relation_type);
