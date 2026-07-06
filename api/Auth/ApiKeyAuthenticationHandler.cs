using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ExcelDatasetManager.Api.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
}

/// <summary>
/// Authenticates requests bearing an <c>X-API-Key</c> header.
/// Two key flavors are supported, distinguished by their prefix:
///
///   "edm_pat_..."  — user-scoped Personal Access Token.
///                    Stored in <c>user_api_keys</c>. Carries only the user_id claim,
///                    so it can access every endpoint the user themselves could.
///                    Used by the MCP server and other long-lived integrations.
///
///   "edm_..."      — dataset-scoped API key.
///                    Stored in <c>dataset_api_keys</c>. Carries user_id + dataset_id
///                    claims. The query endpoint enforces dataset_id matches the URL.
///
/// On success, the principal carries an <c>auth_method</c> claim ("user_api_key" or
/// "dataset_api_key") and, only for dataset-scoped keys, a <c>dataset_id</c> claim plus
/// a <c>can_write</c> claim ("true"/"false", see <c>dataset_api_keys.can_write</c>) and a
/// <c>key_name</c> claim. PATs and JWT sessions are implicitly full-write and never carry
/// a <c>can_write</c> claim.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    private const string HeaderName = "X-API-Key";
    public const string DatasetKeyPrefix = "edm_";
    public const string UserKeyPrefix = "edm_pat_";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = headerValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith(DatasetKeyPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Invalid API key format.");
        }

        var hash = HashKey(rawKey);
        await using var conn = await dataSource.OpenConnectionAsync(Context.RequestAborted);

        // Check the user-scoped PAT table first because its prefix is a strict superset of the dataset key prefix.
        if (rawKey.StartsWith(UserKeyPrefix, StringComparison.Ordinal))
        {
            var userRow = await conn.QuerySingleOrDefaultAsync<UserKeyRow>(
                """
                SELECT id AS Id, user_id AS UserId, revoked_at AS RevokedAt
                FROM user_api_keys
                WHERE key_hash = @KeyHash
                """, new { KeyHash = hash });

            if (userRow is null || userRow.RevokedAt is not null)
            {
                return AuthenticateResult.Fail("Personal access token is invalid or revoked.");
            }

            TryTouch(conn, "user_api_keys", userRow.Id);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userRow.UserId.ToString()),
                new Claim("sub", userRow.UserId.ToString()),
                new Claim("auth_method", "user_api_key")
            };

            return AuthSuccess(claims);
        }

        var datasetRow = await conn.QuerySingleOrDefaultAsync<DatasetKeyRow>(
            """
            SELECT id AS Id, dataset_id AS DatasetId, user_id AS UserId, revoked_at AS RevokedAt,
                   can_write AS CanWrite, name AS Name
            FROM dataset_api_keys
            WHERE key_hash = @KeyHash
            """, new { KeyHash = hash });

        if (datasetRow is null || datasetRow.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("API key is invalid or revoked.");
        }

        TryTouch(conn, "dataset_api_keys", datasetRow.Id);

        var dsClaims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, datasetRow.UserId.ToString()),
            new Claim("sub", datasetRow.UserId.ToString()),
            new Claim(ClaimsPrincipalExtensions.DatasetIdClaim, datasetRow.DatasetId.ToString()),
            new Claim("auth_method", "dataset_api_key"),
            new Claim(ClaimsPrincipalExtensions.CanWriteClaim, datasetRow.CanWrite ? "true" : "false"),
            new Claim(ClaimsPrincipalExtensions.KeyNameClaim, datasetRow.Name)
        };

        return AuthSuccess(dsClaims);
    }

    private AuthenticateResult AuthSuccess(Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName));
    }

    private static void TryTouch(NpgsqlConnection conn, string tableName, Guid id)
    {
        try
        {
            conn.Execute($"UPDATE {tableName} SET last_used_at = NOW() WHERE id = @Id", new { Id = id });
        }
        catch
        {
            // Touching last_used_at is non-critical; never fail authentication because of it.
        }
    }

    public static string HashKey(string rawKey)
    {
        // SHA256 is appropriate: API keys are 256-bit random, no need to slow legitimate requests with bcrypt.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }

    public static string GenerateDatasetKey() => DatasetKeyPrefix + RandomSegment();
    public static string GenerateUserKey() => UserKeyPrefix + RandomSegment();

    // Kept for backward compatibility with the original handler API.
    public static string GenerateKey() => GenerateDatasetKey();

    public const string KeyPrefix = DatasetKeyPrefix;

    private static string RandomSegment()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private sealed record UserKeyRow(Guid Id, Guid UserId, DateTime? RevokedAt);
    private sealed record DatasetKeyRow(Guid Id, Guid DatasetId, Guid UserId, DateTime? RevokedAt, bool CanWrite, string Name);
}
