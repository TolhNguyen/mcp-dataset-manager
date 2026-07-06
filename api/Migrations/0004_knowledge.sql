CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS dataset_knowledge_entries (
    id UUID PRIMARY KEY,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    kind VARCHAR(30) NOT NULL,
    title VARCHAR(255) NOT NULL,
    content TEXT NOT NULL,
    source VARCHAR(10) NOT NULL,
    created_by TEXT NOT NULL,
    pinned BOOLEAN NOT NULL DEFAULT FALSE,
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_knowledge_dataset ON dataset_knowledge_entries(dataset_id) WHERE archived_at IS NULL;
-- NOTE: unaccent() is NOT IMMUTABLE, so it cannot appear in an expression index
-- ("functions in index expression must be marked IMMUTABLE", SQLSTATE 42P17 — confirmed at e2e).
-- We index on lower()-only trigrams (lower IS immutable); search queries still apply unaccent()
-- at query time for accent-insensitive matching. The real per-dataset selectivity comes from the
-- idx_knowledge_dataset partial index above (≤200 active rows/dataset), so a lowercased-trigram
-- prefilter is sufficient here.
CREATE INDEX IF NOT EXISTS idx_knowledge_search ON dataset_knowledge_entries
    USING gin (lower(title || ' ' || content) gin_trgm_ops);

CREATE TABLE IF NOT EXISTS dataset_knowledge_revisions (
    id UUID PRIMARY KEY,
    entry_id UUID NOT NULL REFERENCES dataset_knowledge_entries(id) ON DELETE CASCADE,
    action VARCHAR(10) NOT NULL,
    previous_content TEXT,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE dataset_api_keys ADD COLUMN IF NOT EXISTS can_write BOOLEAN NOT NULL DEFAULT FALSE;

-- Retire business_knowledge: backfill non-empty values into a pinned 'note' entry, then drop.
INSERT INTO dataset_knowledge_entries (id, dataset_id, kind, title, content, source, created_by, pinned, created_at)
SELECT gen_random_uuid(), d.id, 'note', 'Ghi chú nghiệp vụ (migrated)',
       d.business_knowledge, 'user', 'migration', TRUE, NOW()
FROM datasets d
WHERE COALESCE(TRIM(d.business_knowledge), '') <> '';

ALTER TABLE datasets DROP COLUMN IF EXISTS business_knowledge;
ALTER TABLE datasets DROP COLUMN IF EXISTS business_knowledge_updated_at;
