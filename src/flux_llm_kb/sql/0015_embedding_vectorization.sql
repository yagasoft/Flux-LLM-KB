ALTER TABLE embeddings
    ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();

CREATE INDEX IF NOT EXISTS idx_embeddings_owner_model
    ON embeddings (owner_table, owner_id, model);

CREATE INDEX IF NOT EXISTS idx_embeddings_metadata
    ON embeddings USING GIN (metadata);

CREATE INDEX IF NOT EXISTS idx_embeddings_source_hash
    ON embeddings ((metadata->>'source_hash'))
    WHERE metadata ? 'source_hash';
