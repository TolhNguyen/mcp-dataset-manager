using Dapper;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        const string sql = """
            CREATE TABLE IF NOT EXISTS users (
                id UUID PRIMARY KEY,
                email VARCHAR(255) NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS datasets (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id),
                name VARCHAR(255) NOT NULL,
                original_file_name VARCHAR(255) NOT NULL,
                file_type VARCHAR(20) NOT NULL,
                stored_file_name VARCHAR(255) NOT NULL,
                file_size_bytes BIGINT NOT NULL,
                manifest_file_name VARCHAR(255),
                status VARCHAR(50) NOT NULL,
                table_count INT NOT NULL DEFAULT 0,
                total_rows BIGINT NOT NULL DEFAULT 0,
                error_message TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                processed_at TIMESTAMPTZ
            );

            CREATE INDEX IF NOT EXISTS idx_datasets_user_id_created_at
            ON datasets(user_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS dataset_tables (
                id UUID PRIMARY KEY,
                dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
                table_name VARCHAR(255) NOT NULL,
                source_name VARCHAR(255) NOT NULL,
                source_type VARCHAR(50) NOT NULL,
                data_file_name TEXT NOT NULL,
                row_count BIGINT NOT NULL DEFAULT 0,
                column_count INT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_dataset_tables_dataset_id
            ON dataset_tables(dataset_id);

            CREATE TABLE IF NOT EXISTS dataset_columns (
                id UUID PRIMARY KEY,
                dataset_table_id UUID NOT NULL REFERENCES dataset_tables(id) ON DELETE CASCADE,
                ordinal_position INT NOT NULL,
                original_header TEXT,
                normalized_name TEXT NOT NULL,
                display_name TEXT,
                aliases TEXT[],
                inferred_type VARCHAR(100),
                semantic_type VARCHAR(100),
                null_count BIGINT,
                distinct_count BIGINT,
                sample_values JSONB
            );

            CREATE INDEX IF NOT EXISTS idx_dataset_columns_table
            ON dataset_columns(dataset_table_id);

            CREATE TABLE IF NOT EXISTS query_logs (
                id UUID PRIMARY KEY,
                dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
                user_id UUID NOT NULL REFERENCES users(id),
                sql_submitted TEXT NOT NULL,
                sql_executed TEXT,
                status VARCHAR(50) NOT NULL,
                elapsed_ms INT,
                row_count INT,
                error_code VARCHAR(64),
                error_message TEXT,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_query_logs_dataset_created
            ON query_logs(dataset_id, created_at DESC);

            -- Dataset-scoped API keys (for MCP / external agents).
            -- One key grants read-only query access to exactly one dataset.
            CREATE TABLE IF NOT EXISTS dataset_api_keys (
                id UUID PRIMARY KEY,
                dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
                user_id UUID NOT NULL REFERENCES users(id),
                name VARCHAR(255) NOT NULL,
                key_hash TEXT NOT NULL UNIQUE,
                last_used_at TIMESTAMPTZ,
                revoked_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_dataset_api_keys_dataset
            ON dataset_api_keys(dataset_id);

            -- User-scoped personal access tokens (for MCP and other long-lived integrations).
            -- One token grants the same access as a JWT, but is long-lived and revocable.
            -- Distinguished from dataset API keys by the "edm_pat_" prefix on the raw key.
            CREATE TABLE IF NOT EXISTS user_api_keys (
                id UUID PRIMARY KEY,
                user_id UUID NOT NULL REFERENCES users(id),
                name VARCHAR(255) NOT NULL,
                key_hash TEXT NOT NULL UNIQUE,
                last_used_at TIMESTAMPTZ,
                revoked_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_user_api_keys_user_id
            ON user_api_keys(user_id);
            """;

        await conn.ExecuteAsync(sql);
        logger.LogInformation("Database schema initialized.");
    }
}
