CREATE TABLE IF NOT EXISTS db_connections (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    provider VARCHAR(20) NOT NULL,
    encrypted_config TEXT NOT NULL,
    last_test_status VARCHAR(20),
    last_test_at TIMESTAMPTZ,
    last_test_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_db_connections_user ON db_connections(user_id);

ALTER TABLE datasets
    ADD COLUMN IF NOT EXISTS source_kind VARCHAR(20) NOT NULL DEFAULT 'file',
    ADD COLUMN IF NOT EXISTS connection_id UUID REFERENCES db_connections(id),
    ADD COLUMN IF NOT EXISTS external_tables JSONB,
    ADD COLUMN IF NOT EXISTS include_samples BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS schema_refreshed_at TIMESTAMPTZ;

ALTER TABLE dataset_tables
    ADD COLUMN IF NOT EXISTS sample_rows JSONB;

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS max_datasets INT NOT NULL DEFAULT 10;
