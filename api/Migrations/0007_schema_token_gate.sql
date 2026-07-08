-- Schema-token gate: schema fingerprint + AI knowledge-write toggle; remove dataset-scoped API keys.
ALTER TABLE datasets ADD COLUMN IF NOT EXISTS schema_hash TEXT NULL;
ALTER TABLE datasets ADD COLUMN IF NOT EXISTS ai_can_write_knowledge BOOLEAN NOT NULL DEFAULT TRUE;

DROP TABLE IF EXISTS dataset_api_keys;
