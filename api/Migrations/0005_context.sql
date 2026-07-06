ALTER TABLE datasets ADD COLUMN IF NOT EXISTS alias VARCHAR(64);
ALTER TABLE query_logs ADD COLUMN IF NOT EXISTS dataset_ids UUID[];

-- Backfill alias from name, unique per user. Deterministic slug + row_number suffix for collisions.
WITH slugged AS (
    SELECT id, user_id,
           NULLIF(regexp_replace(lower(name), '[^a-z0-9]+', '_', 'g'), '') AS base
    FROM datasets WHERE alias IS NULL
),
numbered AS (
    SELECT id, user_id,
           COALESCE(base, 'ds') AS base,
           ROW_NUMBER() OVER (PARTITION BY user_id, COALESCE(base,'ds') ORDER BY id) AS rn
    FROM slugged
)
UPDATE datasets d
SET alias = CASE WHEN n.rn = 1 THEN n.base ELSE n.base || '_' || n.rn END
FROM numbered n WHERE d.id = n.id;

CREATE UNIQUE INDEX IF NOT EXISTS idx_datasets_user_alias ON datasets(user_id, alias) WHERE alias IS NOT NULL;
