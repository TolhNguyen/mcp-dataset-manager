using Dapper;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class DatasetApiKeyService(NpgsqlDataSource dataSource)
{
    public async Task<ApiResult<object>> CreateAsync(Guid userId, Guid datasetId, string name, bool canWrite, CancellationToken ct)
    {
        var trimmedName = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "Key name is required.");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Ensure the dataset belongs to the user.
        var owns = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE id = @DatasetId AND user_id = @UserId",
            new { DatasetId = datasetId, UserId = userId });

        if (owns == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        var raw = ApiKeyAuthenticationHandler.GenerateKey();
        var hash = ApiKeyAuthenticationHandler.HashKey(raw);
        var id = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO dataset_api_keys(id, dataset_id, user_id, name, key_hash, can_write)
            VALUES(@Id, @DatasetId, @UserId, @Name, @KeyHash, @CanWrite)
            """, new { Id = id, DatasetId = datasetId, UserId = userId, Name = trimmedName, KeyHash = hash, CanWrite = canWrite });

        return ApiResult<object>.Ok(new
        {
            api_key_id = id,
            dataset_id = datasetId,
            name = trimmedName,
            can_write = canWrite,
            // The raw key is shown ONCE — store it client-side.
            api_key = raw,
            usage = "Send via the X-API-Key header to authorize query requests to this dataset."
        });
    }

    public async Task<ApiResult<object>> ListAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var owns = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE id = @DatasetId AND user_id = @UserId",
            new { DatasetId = datasetId, UserId = userId });

        if (owns == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        var rows = await conn.QueryAsync("""
            SELECT id AS api_key_id, name, created_at, last_used_at, revoked_at, can_write
            FROM dataset_api_keys
            WHERE dataset_id = @DatasetId
            ORDER BY created_at DESC
            """, new { DatasetId = datasetId });

        return ApiResult<object>.Ok(new { api_keys = rows });
    }

    public async Task<ApiResult<object>> RevokeAsync(Guid userId, Guid datasetId, Guid apiKeyId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var affected = await conn.ExecuteAsync("""
            UPDATE dataset_api_keys
            SET revoked_at = NOW()
            WHERE id = @Id AND dataset_id = @DatasetId AND user_id = @UserId AND revoked_at IS NULL
            """, new { Id = apiKeyId, DatasetId = datasetId, UserId = userId });

        return affected == 0
            ? ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "API key not found or already revoked.")
            : ApiResult<object>.Ok(new { revoked = true, api_key_id = apiKeyId });
    }
}
