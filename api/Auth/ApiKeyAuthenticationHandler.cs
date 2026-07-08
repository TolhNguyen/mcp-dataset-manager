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
/// Authenticates requests bearing an X-API-Key header. Only user-scoped Personal Access Tokens
/// are supported; dataset-scoped API keys have been removed.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    private const string HeaderName = "X-API-Key";
    public const string UserKeyPrefix = "edm_pat_";
    public const string KeyPrefix = UserKeyPrefix;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawKey = headerValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith(UserKeyPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Invalid API key format.");
        }

        var hash = HashKey(rawKey);
        await using var conn = await dataSource.OpenConnectionAsync(Context.RequestAborted);

        var userRow = await conn.QuerySingleOrDefaultAsync<UserKeyRow>(
            """
            SELECT id AS Id, user_id AS UserId, revoked_at AS RevokedAt
            FROM user_api_keys
            WHERE key_hash = @KeyHash
            """, new { KeyHash = hash });

        if (userRow is null || userRow.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("API key is invalid or revoked.");
        }

        TryTouch(conn, "user_api_keys", userRow.Id);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userRow.UserId.ToString()),
            new Claim("sub", userRow.UserId.ToString()),
            new Claim(ClaimsPrincipalExtensions.AuthMethodClaim, "user_api_key")
        };

        return AuthSuccess(claims);
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

    public static string GenerateUserKey() => UserKeyPrefix + RandomSegment();
    public static string GenerateKey() => GenerateUserKey();

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
}
