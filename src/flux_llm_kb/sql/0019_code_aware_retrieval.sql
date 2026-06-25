CREATE TABLE IF NOT EXISTS code_symbols (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_asset_id uuid NOT NULL REFERENCES source_assets(id) ON DELETE CASCADE,
    asset_chunk_id uuid REFERENCES asset_chunks(id) ON DELETE SET NULL,
    language text NOT NULL,
    symbol_kind text NOT NULL,
    name text NOT NULL,
    qualified_name text NOT NULL,
    path text NOT NULL,
    line_start integer NOT NULL,
    line_end integer NOT NULL,
    byte_start integer,
    byte_end integer,
    parent_symbol text,
    exported boolean,
    signature text,
    parser_status text NOT NULL DEFAULT 'parsed',
    confidence double precision NOT NULL DEFAULT 1.0,
    scope_hash text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (source_asset_id, qualified_name, symbol_kind, line_start)
);

CREATE TABLE IF NOT EXISTS code_references (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source_asset_id uuid NOT NULL REFERENCES source_assets(id) ON DELETE CASCADE,
    asset_chunk_id uuid REFERENCES asset_chunks(id) ON DELETE SET NULL,
    language text NOT NULL,
    relationship_kind text NOT NULL,
    source_symbol text,
    target text NOT NULL,
    path text NOT NULL,
    line_start integer NOT NULL,
    line_end integer NOT NULL,
    parser_status text NOT NULL DEFAULT 'parsed',
    confidence double precision NOT NULL DEFAULT 0.8,
    scope_hash text,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_code_symbols_lookup
    ON code_symbols (language, symbol_kind, qualified_name, name);

CREATE INDEX IF NOT EXISTS idx_code_symbols_asset
    ON code_symbols (source_asset_id, asset_chunk_id);

CREATE INDEX IF NOT EXISTS idx_code_symbols_path
    ON code_symbols (path);

CREATE INDEX IF NOT EXISTS idx_code_symbols_metadata
    ON code_symbols USING GIN (metadata);

CREATE INDEX IF NOT EXISTS idx_code_references_target
    ON code_references (language, relationship_kind, target);

CREATE INDEX IF NOT EXISTS idx_code_references_asset
    ON code_references (source_asset_id, asset_chunk_id);

CREATE INDEX IF NOT EXISTS idx_code_references_source
    ON code_references (source_symbol);

CREATE INDEX IF NOT EXISTS idx_code_references_metadata
    ON code_references USING GIN (metadata);
