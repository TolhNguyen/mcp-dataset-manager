using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Auth;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public record OAuthResult<T>(bool Success, T? Value, string? Error, string? ErrorDescription)
{
    public static OAuthResult<T> Ok(T value) => new(true, value, null, null);
    public static OAuthResult<T> Fail(string error, string description) => new(false, default, error, description);
}

public record RegisteredClient(string ClientId, string ClientName, string[] RedirectUris);

public class OAuthService(NpgsqlDataSource dataSource, ILogger<OAuthService> logger)
{
    private const int CodeTtlMinutes = 5;
    private const int MaxRedirectUris = 10;

    public async Task<OAuthResult<RegisteredClient>> RegisterClientAsync(
        string? clientName, string[] redirectUris, CancellationToken ct)
    {
        if (redirectUris.Length is 0 or > MaxRedirectUris)
        {
            return OAuthResult<RegisteredClient>.Fail(
                "invalid_client_metadata", $"redirect_uris must contain 1-{MaxRedirectUris} entries.");
        }

        foreach (var uri in redirectUris)
        {
            if (!RedirectUriValidator.IsAllowed(uri))
            {
                return OAuthResult<RegisteredClient>.Fail(
                    "invalid_redirect_uri", $"Redirect URI '{uri}' must be https (or http on localhost).");
            }
        }

        var clientId = "edm_mcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var name = (clientName ?? "").Trim();
        if (name.Length > 255) name = name[..255];

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO oauth_clients (client_id, client_name, redirect_uris)
            VALUES (@ClientId, @ClientName, @RedirectUris::jsonb)
            """, new { ClientId = clientId, ClientName = name, RedirectUris = JsonSerializer.Serialize(redirectUris) });

        logger.LogInformation("Registered OAuth client {ClientId} ({Name})", clientId, name);
        return OAuthResult<RegisteredClient>.Ok(new RegisteredClient(clientId, name, redirectUris));
    }

    public async Task<OAuthResult<string>> CreateAuthorizationCodeAsync(
        Guid userId, string clientId, string redirectUri, string codeChallenge, CancellationToken ct)
    {
        if (codeChallenge.Length is < 43 or > 128)
        {
            return OAuthResult<string>.Fail("invalid_request", "code_challenge must be 43-128 characters (S256).");
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var registeredUris = await conn.ExecuteScalarAsync<string?>(
            "SELECT redirect_uris::text FROM oauth_clients WHERE client_id = @ClientId",
            new { ClientId = clientId });

        if (registeredUris is null)
        {
            return OAuthResult<string>.Fail("invalid_request", "Unknown client_id.");
        }

        var uris = JsonSerializer.Deserialize<string[]>(registeredUris) ?? [];
        if (!uris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return OAuthResult<string>.Fail("invalid_request", "redirect_uri does not match a registered URI.");
        }

        var rawCode = Pkce.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await conn.ExecuteAsync("""
            INSERT INTO oauth_authorization_codes
                (code_hash, client_id, user_id, redirect_uri, code_challenge, expires_at)
            VALUES
                (@CodeHash, @ClientId, @UserId, @RedirectUri, @CodeChallenge, NOW() + make_interval(mins => @Ttl))
            """, new
        {
            CodeHash = ApiKeyAuthenticationHandler.HashKey(rawCode),
            ClientId = clientId,
            UserId = userId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            Ttl = CodeTtlMinutes
        });

        return OAuthResult<string>.Ok(rawCode);
    }

    public async Task<OAuthResult<string>> ExchangeCodeAsync(
        string clientId, string redirectUri, string code, string codeVerifier, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Dọn code hết hạn quá 1 ngày (best-effort, cùng transaction).
        await conn.ExecuteAsync(
            "DELETE FROM oauth_authorization_codes WHERE expires_at < NOW() - INTERVAL '1 day'", transaction: tx);

        var row = await conn.QuerySingleOrDefaultAsync<CodeRow>("""
            SELECT client_id AS ClientId, user_id AS UserId, redirect_uri AS RedirectUri,
                   code_challenge AS CodeChallenge, expires_at AS ExpiresAt, used_at AS UsedAt
            FROM oauth_authorization_codes
            WHERE code_hash = @CodeHash
            FOR UPDATE
            """, new { CodeHash = ApiKeyAuthenticationHandler.HashKey(code) }, tx);

        if (row is null || row.UsedAt is not null || row.ExpiresAt < DateTime.UtcNow
            || row.ClientId != clientId
            || row.RedirectUri != redirectUri
            || !Pkce.VerifyS256(codeVerifier, row.CodeChallenge))
        {
            await tx.RollbackAsync(ct);
            return OAuthResult<string>.Fail(
                "invalid_grant", "Authorization code is invalid, expired, already used, or PKCE verification failed.");
        }

        await conn.ExecuteAsync(
            "UPDATE oauth_authorization_codes SET used_at = NOW() WHERE code_hash = @CodeHash",
            new { CodeHash = ApiKeyAuthenticationHandler.HashKey(code) }, tx);

        // Access token = PAT tự động tạo; user thấy và thu hồi được trong UI quản lý token.
        var rawPat = ApiKeyAuthenticationHandler.GenerateUserKey();
        await conn.ExecuteAsync("""
            INSERT INTO user_api_keys (id, user_id, name, key_hash)
            VALUES (@Id, @UserId, @Name, @KeyHash)
            """, new
        {
            Id = Guid.NewGuid(),
            UserId = row.UserId,
            Name = $"Claude MCP (OAuth) {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
            KeyHash = ApiKeyAuthenticationHandler.HashKey(rawPat)
        }, tx);

        await tx.CommitAsync(ct);
        logger.LogInformation("OAuth code exchanged for PAT by client {ClientId}", clientId);
        return OAuthResult<string>.Ok(rawPat);
    }

    private sealed record CodeRow(
        string ClientId, Guid UserId, string RedirectUri, string CodeChallenge,
        DateTime ExpiresAt, DateTime? UsedAt);
}
