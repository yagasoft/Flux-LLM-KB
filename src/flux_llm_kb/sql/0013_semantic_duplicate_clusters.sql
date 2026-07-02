CREATE TABLE IF NOT EXISTS semantic_duplicate_clusters (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    memory_class text NOT NULL,
    status text NOT NULL DEFAULT 'active',
    algorithm text NOT NULL DEFAULT 'snowflake-vespa-cosine-v1',
    threshold double precision NOT NULL,
    workspace_key text NOT NULL DEFAULT '',
    root_name text,
    canonical_owner_table text NOT NULL,
    canonical_owner_id uuid NOT NULL,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT semantic_duplicate_clusters_memory_class_allowed
        CHECK (memory_class IN ('corpus', 'episode', 'claim')),
    CONSTRAINT semantic_duplicate_clusters_status_allowed
        CHECK (status IN ('active', 'retired')),
    CONSTRAINT semantic_duplicate_clusters_owner_table_allowed
        CHECK (canonical_owner_table IN ('asset_chunks', 'episodes', 'claims')),
    CONSTRAINT semantic_duplicate_clusters_threshold_range
        CHECK (threshold >= 0.0 AND threshold <= 1.0)
);

CREATE TABLE IF NOT EXISTS semantic_duplicate_members (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    cluster_id uuid NOT NULL REFERENCES semantic_duplicate_clusters(id) ON DELETE CASCADE,
    memory_class text NOT NULL,
    owner_table text NOT NULL,
    owner_id uuid NOT NULL,
    member_role text NOT NULL,
    similarity double precision NOT NULL DEFAULT 1.0,
    evidence jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT semantic_duplicate_members_memory_class_allowed
        CHECK (memory_class IN ('corpus', 'episode', 'claim')),
    CONSTRAINT semantic_duplicate_members_owner_table_allowed
        CHECK (owner_table IN ('asset_chunks', 'episodes', 'claims')),
    CONSTRAINT semantic_duplicate_members_role_allowed
        CHECK (member_role IN ('canonical', 'duplicate')),
    CONSTRAINT semantic_duplicate_members_similarity_range
        CHECK (similarity >= 0.0 AND similarity <= 1.0),
    UNIQUE (cluster_id, owner_table, owner_id)
);

CREATE INDEX IF NOT EXISTS idx_semantic_duplicate_clusters_scope
    ON semantic_duplicate_clusters (memory_class, status, root_name, workspace_key, updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_semantic_duplicate_clusters_canonical
    ON semantic_duplicate_clusters (canonical_owner_table, canonical_owner_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS idx_semantic_duplicate_members_owner
    ON semantic_duplicate_members (owner_table, owner_id, member_role);

CREATE INDEX IF NOT EXISTS idx_semantic_duplicate_members_cluster
    ON semantic_duplicate_members (cluster_id, member_role, similarity DESC);
