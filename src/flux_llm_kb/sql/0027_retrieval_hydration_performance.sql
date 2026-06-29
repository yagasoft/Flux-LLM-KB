CREATE INDEX IF NOT EXISTS idx_source_assets_canonical_asset_id
    ON source_assets (canonical_asset_id)
    WHERE canonical_asset_id IS NOT NULL;
