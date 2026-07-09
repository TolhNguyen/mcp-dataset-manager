-- api/Migrations/0008_dashboard_shares.sql
-- Dashboard share links: token+PIN hashed, per-link revoke/expiry, PIN lockout, view audit.
CREATE TABLE IF NOT EXISTS dashboard_shares (
    id UUID PRIMARY KEY,
    dashboard_id UUID NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash TEXT NOT NULL UNIQUE,
    pin_hash TEXT NOT NULL,
    created_by TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    failed_pin_count INT NOT NULL DEFAULT 0,
    locked_until TIMESTAMPTZ NULL,
    view_count INT NOT NULL DEFAULT 0,
    last_viewed_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_dashboard_shares_dashboard ON dashboard_shares(dashboard_id);
