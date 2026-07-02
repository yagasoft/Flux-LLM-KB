CREATE INDEX IF NOT EXISTS idx_source_assets_active_root
    ON source_assets (root_id, id)
    WHERE deleted_at IS NULL
      AND canonical_asset_id IS NULL
      AND extraction_status = 'indexed';

CREATE INDEX IF NOT EXISTS idx_asset_chunks_sidecar_ref
    ON asset_chunks (asset_id, updated_at DESC)
    WHERE metadata ? 'sidecar_ref';

CREATE INDEX IF NOT EXISTS idx_code_symbols_qualified_name_trgm
    ON code_symbols USING GIN (qualified_name gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_code_symbols_name_trgm
    ON code_symbols USING GIN (name gin_trgm_ops);
