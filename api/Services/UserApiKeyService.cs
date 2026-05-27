using Dapper;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Manages user-scoped Personal Access Tokens (PATs). Used by the MCP server
/// and any long-lived integration that needs the same permissions as a user JWT.
/// </summary>
public class UserApiKeyService(NpgsqlDataSource dataSource)
{
    public async Task<ApiResult<object>> CreateAsync(Guid userId, string name, CancellationToken ct)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "Token name is required.");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var raw = ApiKeyAuthenticationHandler.GenerateUserKey();
        var hash = ApiKeyAuthenticationHandler.HashKey(raw);
        var id = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO user_api_keys(id, user_id, name, key_hash)
            VALUES(@Id, @UserId, @Name, @KeyHash)
            """, new { Id = id, UserId = userId, Name = trimmed, KeyHash = hash });

        return ApiResult<object>.Ok(new
        {
            token_id = id,
            name = trimmed,
            token = raw,
            usage = "Send via the X-API-Key header. This token grants the same access as a JWT. Treat it like a password."
        });
    }

    public async Task<ApiResult<object>> ListAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync("""
            SELECT id AS token_id, name, created_at, last_used_at, revoked_at
            FROM user_api_keys
            WHERE user_id = @UserId
            ORDER BY created_at DESC
            """, new { UserId = userId });

        return ApiResult<object>.Ok(new { tokens = rows });
    }

    public async Task<ApiResult<object>> RevokeAsync(Guid userId, Guid tokenId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync("""
            UPDATE user_api_keys
            SET revoked_at = NOW()
            WHERE id = @Id AND user_id = @UserId AND revoked_at IS NULL
            """, new { Id = tokenId, UserId = userId });

        return affected == 0
            ? ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Token not found or already revoked.")
            : ApiResult<object>.Ok(new { revoked = true, token_id = tokenId });
    }
}
