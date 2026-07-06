# Phase 0b — OAuth Authorization cho MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude.ai kết nối MCP của EDM qua luồng OAuth 2.1 (bấm "Cho phép" trên trang authorize) thay vì bắt user dán PAT thủ công. Access token phát ra chính là PAT `edm_pat_...` tự động tạo.

**Architecture:** API (.NET 8) đóng vai Authorization Server: metadata RFC 8414 + Protected Resource Metadata RFC 9728, Dynamic Client Registration RFC 7591, authorize (HTML page + cookie login) → code (PKCE S256, single-use, TTL 5 phút) → token endpoint đổi code lấy PAT. Bridge chỉ thêm header `WWW-Authenticate` vào response 401 để Claude tự khám phá. Không refresh token — PAT không hết hạn, thu hồi trong UI hiện có.

**Tech Stack:** .NET 8 + Dapper (KHÔNG EF), vanilla JS cho trang authorize, TypeScript cho bridge.

**Spec:** `docs/superpowers/specs/2026-07-06-big-update-design.md`, mục "Phase 0b".

**Base branch:** `phase-0-foundation` (cần code Phase 0: MigrationRunner, PAT bearer trên bridge, Endpoints/*).

## Global Constraints

- TargetFramework `net8.0`; không thêm NuGet package mới (dùng `System.Security.Cryptography` có sẵn).
- JSON API snake_case (đã cấu hình). Riêng response OAuth dùng đúng tên chuẩn: `access_token`, `token_type`, `error`, `error_description` — viết property anonymous object đúng chữ thường có gạch dưới như code hiện có.
- Endpoint token/register nhận `application/x-www-form-urlencoded` (token) và JSON (register) — làm đúng như từng bước ghi.
- Sau MỖI task: `dotnet build api/ExcelDatasetManager.Api.csproj` và `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` pass rồi mới commit.
- KHÔNG sửa các file Phase A/B/C/D. KHÔNG đổi hành vi PAT hiện có.

---

### Task 1: Migration 0002 + helper PKCE / redirect-uri (pure, có test)

**Files:**
- Create: `api/Migrations/0002_oauth.sql`
- Create: `api/Auth/Pkce.cs`
- Create: `api/Auth/RedirectUriValidator.cs`
- Test: `tests/ExcelDatasetManager.Tests/PkceTests.cs`, `tests/ExcelDatasetManager.Tests/RedirectUriValidatorTests.cs`

**Interfaces:**
- Produces: `Pkce.VerifyS256(string codeVerifier, string codeChallenge) -> bool`; `Pkce.Base64UrlEncode(byte[]) -> string`; `RedirectUriValidator.IsAllowed(string uri) -> bool`. Task 2 dùng cả ba.

- [ ] **Step 1: Tạo `api/Migrations/0002_oauth.sql`**

```sql
CREATE TABLE IF NOT EXISTS oauth_clients (
    client_id TEXT PRIMARY KEY,
    client_name TEXT NOT NULL DEFAULT '',
    redirect_uris JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS oauth_authorization_codes (
    code_hash TEXT PRIMARY KEY,
    client_id TEXT NOT NULL REFERENCES oauth_clients(client_id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    redirect_uri TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_oauth_codes_expires
ON oauth_authorization_codes(expires_at);
```

- [ ] **Step 2: Viết test — `tests/ExcelDatasetManager.Tests/PkceTests.cs`**

Dùng test vector chuẩn từ RFC 7636 Appendix B để không phụ thuộc chính code mình test:

```csharp
using ExcelDatasetManager.Api.Auth;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class PkceTests
{
    // RFC 7636 Appendix B test vector
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Accepts_rfc7636_test_vector()
    {
        Assert.True(Pkce.VerifyS256(Verifier, Challenge));
    }

    [Theory]
    [InlineData("wrong-verifier-wrong-verifier-wrong-verifier", Challenge)]
    [InlineData(Verifier, "wrong-challenge")]
    [InlineData("", Challenge)]
    [InlineData(Verifier, "")]
    public void Rejects_mismatches_and_empty(string verifier, string challenge)
    {
        Assert.False(Pkce.VerifyS256(verifier, challenge));
    }

    [Fact]
    public void Base64UrlEncode_has_no_padding_or_url_unsafe_chars()
    {
        var encoded = Pkce.Base64UrlEncode(new byte[] { 0xfb, 0xff, 0x3e, 0x00, 0x01 });
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
    }
}
```

- [ ] **Step 3: Viết test — `tests/ExcelDatasetManager.Tests/RedirectUriValidatorTests.cs`**

```csharp
using ExcelDatasetManager.Api.Auth;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class RedirectUriValidatorTests
{
    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://example.com/cb?x=1")]
    [InlineData("http://localhost:3000/callback")]
    [InlineData("http://127.0.0.1:8080/cb")]
    public void Allows_https_and_local_http(string uri)
    {
        Assert.True(RedirectUriValidator.IsAllowed(uri));
    }

    [Theory]
    [InlineData("http://evil.com/cb")]           // http không phải localhost
    [InlineData("javascript:alert(1)")]
    [InlineData("custom-scheme://cb")]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData("/relative/path")]
    public void Rejects_everything_else(string uri)
    {
        Assert.False(RedirectUriValidator.IsAllowed(uri));
    }
}
```

- [ ] **Step 4: Chạy test để thấy fail**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter "PkceTests|RedirectUriValidatorTests"`
Expected: FAIL — compile error, 2 class chưa tồn tại.

- [ ] **Step 5: Tạo `api/Auth/Pkce.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Auth;

public static class Pkce
{
    /// <summary>RFC 7636: challenge = BASE64URL(SHA256(ASCII(verifier))), method S256 only.</summary>
    public static bool VerifyS256(string codeVerifier, string codeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
        {
            return false;
        }

        var computed = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(codeChallenge));
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 6: Tạo `api/Auth/RedirectUriValidator.cs`**

```csharp
namespace ExcelDatasetManager.Api.Auth;

public static class RedirectUriValidator
{
    /// <summary>Chỉ nhận https tuyệt đối; http chỉ cho localhost/127.0.0.1 (dev).</summary>
    public static bool IsAllowed(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps) return true;

        return parsed.Scheme == Uri.UriSchemeHttp
               && (parsed.Host == "localhost" || parsed.Host == "127.0.0.1");
    }
}
```

- [ ] **Step 7: Chạy test → PASS. Build → OK.**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`

- [ ] **Step 8: Commit**

```bash
git add api/Migrations/0002_oauth.sql api/Auth/Pkce.cs api/Auth/RedirectUriValidator.cs tests/
git commit -m "feat: add oauth tables migration and PKCE/redirect-uri helpers"
```

---

### Task 2: OAuthService

**Files:**
- Create: `api/Services/OAuthService.cs`

**Interfaces:**
- Consumes: `Pkce`, `RedirectUriValidator` (Task 1); `ApiKeyAuthenticationHandler.GenerateUserKey()/HashKey()`; bảng migration 0002.
- Produces (Task 3 dùng):
  - `RegisterClientAsync(string? clientName, string[] redirectUris, CancellationToken) -> Task<OAuthResult<RegisteredClient>>`
  - `CreateAuthorizationCodeAsync(Guid userId, string clientId, string redirectUri, string codeChallenge, CancellationToken) -> Task<OAuthResult<string>>` (trả raw code)
  - `ExchangeCodeAsync(string clientId, string redirectUri, string code, string codeVerifier, CancellationToken) -> Task<OAuthResult<string>>` (trả PAT `edm_pat_...`)
  - `record OAuthResult<T>(bool Success, T? Value, string? Error, string? ErrorDescription)`
  - `record RegisteredClient(string ClientId, string ClientName, string[] RedirectUris)`

- [ ] **Step 1: Tạo `api/Services/OAuthService.cs`**

```csharp
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
```

- [ ] **Step 2: Đăng ký DI trong `api/Program.cs`** (khối Services): `builder.Services.AddScoped<OAuthService>();`

- [ ] **Step 3: Build + test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: PASS (service này test hành vi qua smoke end-to-end ở Task 6 vì cần Postgres).

- [ ] **Step 4: Commit**

```bash
git add api/Services/OAuthService.cs api/Program.cs
git commit -m "feat: add OAuthService (DCR, PKCE code flow, code-to-PAT exchange)"
```

---

### Task 3: OAuth endpoints + metadata

**Files:**
- Create: `api/Endpoints/OAuthEndpoints.cs`
- Modify: `api/Models/Contracts.cs` (thêm 2 DTO)
- Modify: `api/Program.cs` (map endpoints)
- Modify: `api/appsettings.json`, `api/appsettings.Development.json` (Oauth:PublicUrl)
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: `OAuthService` (Task 2), policy `JwtOnly`, rate limit policy `auth`, `ClaimsPrincipalExtensions.GetUserId()`.
- Produces routes: `GET /.well-known/oauth-authorization-server`, `GET /.well-known/oauth-protected-resource` (+`/mcp` variant), `POST /api/oauth/register`, `GET /oauth/authorize` (HTML — Task 4 tạo file), `POST /api/oauth/authorize/approve`, `POST /api/oauth/token`.

- [ ] **Step 1: Thêm DTO vào `api/Models/Contracts.cs`** (cạnh các record request khác):

```csharp
public record OAuthRegisterRequest(string[]? RedirectUris, string? ClientName);
public record OAuthApproveRequest(
    string? ClientId, string? RedirectUri, string? CodeChallenge, string? CodeChallengeMethod, string? State);
```

- [ ] **Step 2: Tạo `api/Endpoints/OAuthEndpoints.cs`**

```csharp
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        var publicUrl = (app.Configuration["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');

        // ---- Discovery metadata (RFC 8414 + RFC 9728), anonymous ----

        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Ok(new
        {
            issuer = publicUrl,
            authorization_endpoint = $"{publicUrl}/oauth/authorize",
            token_endpoint = $"{publicUrl}/api/oauth/token",
            registration_endpoint = $"{publicUrl}/api/oauth/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }
        }));

        var prm = () => Results.Ok(new
        {
            resource = $"{publicUrl}/mcp",
            authorization_servers = new[] { publicUrl },
            bearer_methods_supported = new[] { "header" }
        });
        app.MapGet("/.well-known/oauth-protected-resource", prm);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", prm);

        // ---- Dynamic Client Registration (RFC 7591), anonymous + rate limited ----

        app.MapPost("/api/oauth/register",
            async (OAuthRegisterRequest req, OAuthService svc, CancellationToken ct) =>
        {
            var result = await svc.RegisterClientAsync(req.ClientName, req.RedirectUris ?? [], ct);
            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            var client = result.Value!;
            return Results.Created($"/api/oauth/register/{client.ClientId}", new
            {
                client_id = client.ClientId,
                client_name = client.ClientName,
                redirect_uris = client.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code" },
                response_types = new[] { "code" }
            });
        }).RequireRateLimiting("auth");

        // ---- Authorize page (HTML; JS trong trang tự xử lý login + approve) ----

        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
            Results.File(
                Path.Combine(app.Environment.WebRootPath, "oauth-authorize.html"),
                "text/html; charset=utf-8"));

        // ---- Approve (đăng nhập bằng JWT/cookie; PAT không được mint token mới) ----

        app.MapPost("/api/oauth/authorize/approve",
            async (OAuthApproveRequest req, ClaimsPrincipal principal, OAuthService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.ClientId)
                || string.IsNullOrWhiteSpace(req.RedirectUri)
                || string.IsNullOrWhiteSpace(req.CodeChallenge))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "client_id, redirect_uri and code_challenge are required."
                });
            }

            if (!string.Equals(req.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "Only code_challenge_method=S256 is supported."
                });
            }

            var result = await svc.CreateAuthorizationCodeAsync(
                userId.Value, req.ClientId, req.RedirectUri, req.CodeChallenge, ct);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            var separator = req.RedirectUri.Contains('?') ? '&' : '?';
            var redirectTo = $"{req.RedirectUri}{separator}code={Uri.EscapeDataString(result.Value!)}";
            if (!string.IsNullOrEmpty(req.State))
            {
                redirectTo += $"&state={Uri.EscapeDataString(req.State)}";
            }

            return Results.Ok(new { redirect_to = redirectTo });
        }).RequireAuthorization("JwtOnly").RequireRateLimiting("auth");

        // ---- Token endpoint (form-urlencoded per OAuth spec), anonymous + rate limited ----

        app.MapPost("/api/oauth/token", async (HttpContext ctx, OAuthService svc, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "Content-Type must be application/x-www-form-urlencoded."
                });
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            var grantType = form["grant_type"].ToString();
            if (grantType != "authorization_code")
            {
                return Results.BadRequest(new
                {
                    error = "unsupported_grant_type",
                    error_description = "Only authorization_code is supported."
                });
            }

            var result = await svc.ExchangeCodeAsync(
                clientId: form["client_id"].ToString(),
                redirectUri: form["redirect_uri"].ToString(),
                code: form["code"].ToString(),
                codeVerifier: form["code_verifier"].ToString(),
                ct);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            return Results.Ok(new
            {
                access_token = result.Value,
                token_type = "Bearer"
            });
        }).RequireRateLimiting("auth");
    }
}
```

- [ ] **Step 3: Wire vào `api/Program.cs`** — cạnh các Map khác, thêm: `app.MapOAuthEndpoints();`

- [ ] **Step 4: Config**

a) `api/appsettings.json` — thêm section (sau `"Encryption"`):
```json
  "Oauth": {
    "PublicUrl": "http://localhost"
  },
```
b) `api/appsettings.Development.json` — thêm:
```json
  "Oauth": {
    "PublicUrl": "http://localhost:5847"
  },
```
c) `docker-compose.yml` — `api.environment` thêm: `Oauth__PublicUrl: ${EDM_PUBLIC_URL:-http://localhost}`

- [ ] **Step 5: Build + test → PASS. Commit**

```bash
git add api/ docker-compose.yml
git commit -m "feat: add OAuth 2.1 endpoints (metadata, DCR, authorize approve, token)"
```

---

### Task 4: Trang authorize (wwwroot)

**Files:**
- Create: `api/wwwroot/oauth-authorize.html`

**Interfaces:**
- Consumes: `GET /api/auth/me` (cookie), `POST /api/auth/login` (set cookie), `POST /api/oauth/authorize/approve` (cookie JWT) — tất cả có sẵn/từ Task 3.

- [ ] **Step 1: Tạo `api/wwwroot/oauth-authorize.html`** (nguyên văn):

```html
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Cho phép truy cập — Excel Dataset Manager</title>
<style>
  body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0;
         display: flex; justify-content: center; padding-top: 8vh; margin: 0; min-height: 100vh; }
  .card { background: #1e293b; border: 1px solid #334155; border-radius: 12px;
          padding: 32px; width: 420px; max-width: 92vw; }
  h1 { font-size: 20px; margin: 0 0 8px; }
  p  { color: #94a3b8; font-size: 14px; line-height: 1.5; }
  label { display: block; font-size: 13px; margin: 14px 0 4px; color: #cbd5e1; }
  input { width: 100%; box-sizing: border-box; padding: 10px 12px; border-radius: 8px;
          border: 1px solid #475569; background: #0f172a; color: #e2e8f0; font-size: 14px; }
  button { width: 100%; margin-top: 18px; padding: 12px; border: 0; border-radius: 8px;
           font-size: 15px; font-weight: 600; cursor: pointer; }
  .primary { background: linear-gradient(90deg, #2563eb, #3b82f6); color: white; }
  .secondary { background: transparent; color: #94a3b8; margin-top: 8px; }
  .error { color: #f87171; font-size: 13px; margin-top: 10px; min-height: 18px; }
  .meta { border-top: 1px solid #334155; margin-top: 20px; padding-top: 12px;
          font-size: 12px; color: #64748b; word-break: break-all; }
  .hidden { display: none; }
  .account { background: #0f172a; border-radius: 8px; padding: 10px 12px; font-size: 14px; margin-top: 12px; }
</style>
</head>
<body>
<div class="card">
  <h1>Cho phép truy cập EDM</h1>
  <p id="intro">Một ứng dụng AI đang yêu cầu quyền truy cập dữ liệu Excel Dataset Manager của bạn
     (đọc dataset, chạy truy vấn SQL chỉ-đọc).</p>

  <div id="invalid" class="hidden">
    <p class="error">Yêu cầu ủy quyền không hợp lệ (thiếu tham số OAuth). Hãy thử kết nối lại từ ứng dụng AI.</p>
  </div>

  <div id="login" class="hidden">
    <label>Email</label>
    <input id="email" type="email" autocomplete="username" />
    <label>Mật khẩu</label>
    <input id="password" type="password" autocomplete="current-password" />
    <button class="primary" id="btn-login">Đăng nhập</button>
  </div>

  <div id="approve" class="hidden">
    <div class="account">Đăng nhập với: <strong id="account-email"></strong></div>
    <button class="primary" id="btn-approve">Cho phép truy cập</button>
    <button class="secondary" id="btn-deny">Từ chối</button>
  </div>

  <div class="error" id="error"></div>

  <div class="meta">
    <div>Client: <span id="meta-client"></span></div>
    <div>Redirect: <span id="meta-redirect"></span></div>
  </div>
</div>

<script>
(function () {
  var q = new URLSearchParams(window.location.search);
  var params = {
    responseType: q.get('response_type'),
    clientId: q.get('client_id'),
    redirectUri: q.get('redirect_uri'),
    state: q.get('state') || '',
    codeChallenge: q.get('code_challenge'),
    codeChallengeMethod: q.get('code_challenge_method')
  };

  var el = function (id) { return document.getElementById(id); };
  var show = function (id) { el(id).classList.remove('hidden'); };
  var hide = function (id) { el(id).classList.add('hidden'); };
  var setError = function (msg) { el('error').textContent = msg || ''; };

  el('meta-client').textContent = params.clientId || '(không có)';
  el('meta-redirect').textContent = params.redirectUri || '(không có)';

  var valid = params.responseType === 'code' && params.clientId && params.redirectUri
              && params.codeChallenge && params.codeChallengeMethod === 'S256';
  if (!valid) { hide('intro'); show('invalid'); return; }

  function checkSession() {
    fetch('/api/auth/me', { credentials: 'include' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (body) {
        if (body && body.success) {
          el('account-email').textContent = body.user.email;
          hide('login'); show('approve');
        } else {
          hide('approve'); show('login');
        }
      })
      .catch(function () { hide('approve'); show('login'); });
  }

  el('btn-login').addEventListener('click', function () {
    setError('');
    fetch('/api/auth/login', {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: el('email').value.trim(), password: el('password').value })
    })
      .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
      .then(function (res) {
        if (res.ok && res.body.success) { checkSession(); }
        else { setError((res.body.error && res.body.error.message) || 'Đăng nhập thất bại.'); }
      })
      .catch(function () { setError('Không kết nối được máy chủ.'); });
  });

  el('btn-approve').addEventListener('click', function () {
    setError('');
    fetch('/api/oauth/authorize/approve', {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        client_id: params.clientId,
        redirect_uri: params.redirectUri,
        code_challenge: params.codeChallenge,
        code_challenge_method: params.codeChallengeMethod,
        state: params.state
      })
    })
      .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
      .then(function (res) {
        if (res.ok && res.body.redirect_to) { window.location.replace(res.body.redirect_to); }
        else { setError(res.body.error_description || res.body.error || 'Ủy quyền thất bại.'); }
      })
      .catch(function () { setError('Không kết nối được máy chủ.'); });
  });

  el('btn-deny').addEventListener('click', function () {
    var sep = params.redirectUri.indexOf('?') >= 0 ? '&' : '?';
    var target = params.redirectUri + sep + 'error=access_denied';
    if (params.state) target += '&state=' + encodeURIComponent(params.state);
    window.location.replace(target);
  });

  checkSession();
})();
</script>
</body>
</html>
```

- [ ] **Step 2: Build → OK. Commit**

```bash
git add api/wwwroot/oauth-authorize.html
git commit -m "feat: add OAuth authorize consent page (login via EDM account, no token pasting)"
```

---

### Task 5: Bridge — WWW-Authenticate + CORS expose

**Files:**
- Modify: `mcp-bridge/src/index.ts`
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: env `EDM_PUBLIC_URL` (default `http://localhost`).
- Produces: mọi response 401 của `/mcp` kèm header `WWW-Authenticate: Bearer resource_metadata="<EDM_PUBLIC_URL>/.well-known/oauth-protected-resource"` — Claude dùng header này để tự khám phá OAuth.

- [ ] **Step 1: Sửa `mcp-bridge/src/index.ts`**

a) Thêm helper (cạnh `extractPat`):

```typescript
function unauthorized(res: Response) {
  const publicUrl = (process.env.EDM_PUBLIC_URL ?? "http://localhost").replace(/\/+$/, "");
  res.setHeader(
    "WWW-Authenticate",
    `Bearer resource_metadata="${publicUrl}/.well-known/oauth-protected-resource"`
  );
  jsonRpcError(res, 401, -32001,
    "Unauthorized: send 'Authorization: Bearer edm_pat_...' (create a personal access token in the EDM web UI, or connect via OAuth).");
}
```

b) Trong CẢ BA handler `app.post/get/delete("/mcp", ...)`: thay khối

```typescript
    if (!pat) {
      return jsonRpcError(res, 401, -32001,
        "Unauthorized: send 'Authorization: Bearer edm_pat_...' (create a personal access token in the EDM web UI).");
    }
```
bằng:
```typescript
    if (!pat) {
      return unauthorized(res);
    }
```

c) Trong middleware CORS, thêm 1 dòng sau các setHeader hiện có:

```typescript
    res.setHeader("Access-Control-Expose-Headers", "WWW-Authenticate, MCP-Session-Id");
```

- [ ] **Step 2: `docker-compose.yml`** — `bridge.environment` thêm: `EDM_PUBLIC_URL: ${EDM_PUBLIC_URL:-http://localhost}`

- [ ] **Step 3: Build + smoke 401 header**

Run:
```bash
cd mcp-bridge && npm run build && EDM_API_URL=http://localhost:5847 MCP_PORT=55850 node dist/index.js & sleep 3
curl -si -X POST http://localhost:55850/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","method":"initialize","id":1}' | grep -i "www-authenticate\|HTTP/"
kill %1
```
Expected: `HTTP/1.1 401` và dòng `WWW-Authenticate: Bearer resource_metadata="http://localhost/.well-known/oauth-protected-resource"`.

- [ ] **Step 4: Commit**

```bash
git add mcp-bridge/src/index.ts docker-compose.yml
git commit -m "feat: advertise OAuth resource metadata via WWW-Authenticate on MCP 401"
```

---

### Task 6: Smoke test end-to-end toàn luồng OAuth

**Files:** không tạo file mới — chạy kiểm chứng.

- [ ] **Step 1: Dựng Postgres tạm + chạy API**

```bash
docker run -d --rm --name edm-oauth-pg -e POSTGRES_PASSWORD=review -e POSTGRES_DB=edm_oauth -p 55433:5432 postgres:16-alpine
sleep 4
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:55851 \
ConnectionStrings__Default="Host=localhost;Port=55433;Database=edm_oauth;Username=postgres;Password=review" \
dotnet run --project api/ExcelDatasetManager.Api.csproj &
sleep 12
```

- [ ] **Step 2: Chạy luồng đầy đủ bằng curl** (PKCE verifier/challenge dùng test vector RFC 7636):

```bash
BASE=http://localhost:55851
VERIFIER="dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
CHALLENGE="E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

# 1. Metadata
curl -s $BASE/.well-known/oauth-authorization-server | grep -o '"code_challenge_methods_supported":\["S256"\]'
curl -s $BASE/.well-known/oauth-protected-resource | grep -o '"authorization_servers"'

# 2. DCR
CLIENT_ID=$(curl -s -X POST $BASE/api/oauth/register -H "Content-Type: application/json" \
  -d '{"redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"client_name":"smoke"}' \
  | grep -o '"client_id":"[^"]*"' | cut -d'"' -f4)
echo "client: $CLIENT_ID"

# 3. Tạo user + lấy JWT
JWT=$(curl -s -X POST $BASE/api/auth/register -H "Content-Type: application/json" \
  -d '{"email":"oauth-smoke@test.local","password":"Password123!"}' \
  | grep -o '"token":"[^"]*"' | cut -d'"' -f4)

# 4. Approve → code
CODE=$(curl -s -X POST $BASE/api/oauth/authorize/approve -H "Content-Type: application/json" \
  -H "Authorization: Bearer $JWT" \
  -d "{\"client_id\":\"$CLIENT_ID\",\"redirect_uri\":\"https://claude.ai/api/mcp/auth_callback\",\"code_challenge\":\"$CHALLENGE\",\"code_challenge_method\":\"S256\",\"state\":\"xyz\"}" \
  | grep -o 'code=[^&"]*' | cut -d'=' -f2)
echo "code: $CODE"

# 5. Đổi code lấy PAT
PAT=$(curl -s -X POST $BASE/api/oauth/token \
  -d "grant_type=authorization_code&code=$CODE&redirect_uri=https://claude.ai/api/mcp/auth_callback&client_id=$CLIENT_ID&code_verifier=$VERIFIER" \
  | grep -o '"access_token":"[^"]*"' | cut -d'"' -f4)
echo "pat: $PAT"

# 6. PAT dùng được thật
curl -s $BASE/api/datasets/ -H "X-API-Key: $PAT" | grep -o '"success":true'

# 7. Code dùng lại lần 2 phải fail (single-use)
curl -s -X POST $BASE/api/oauth/token \
  -d "grant_type=authorization_code&code=$CODE&redirect_uri=https://claude.ai/api/mcp/auth_callback&client_id=$CLIENT_ID&code_verifier=$VERIFIER" \
  | grep -o '"error":"invalid_grant"'

# 8. Verifier sai phải fail (cần code mới — lặp bước 4 rồi):
#    thay code_verifier bằng "sai-sai-sai..." → expect invalid_grant
```
Expected: bước 1–6 in ra giá trị/`"success":true`; bước 7 in `"error":"invalid_grant"`; bước 8 (với code mới + verifier sai) in `invalid_grant`. PAT bắt đầu bằng `edm_pat_`.

- [ ] **Step 3: Dọn dẹp**

```bash
kill %1 2>/dev/null; docker stop edm-oauth-pg
```

- [ ] **Step 4: Chạy full test suite lần cuối + commit nếu có sửa gì phát sinh**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: PASS toàn bộ.

---

## Definition of Done (Phase 0b)

1. `dotnet build` + `dotnet test` pass (≥ 65 tests, gồm Pkce + RedirectUriValidator).
2. `GET /.well-known/oauth-authorization-server` và `/.well-known/oauth-protected-resource` trả metadata đúng, chứa `S256`.
3. Luồng curl end-to-end (Task 6): DCR → approve → token → PAT gọi được `/api/datasets/`.
4. Code là single-use: đổi lần 2 trả `invalid_grant`. Verifier sai trả `invalid_grant`.
5. `POST /mcp` không token → 401 kèm header `WWW-Authenticate` trỏ resource metadata.
6. Mở `http://localhost:5847/oauth/authorize?response_type=code&client_id=<id>&redirect_uri=<uri>&code_challenge=<c>&code_challenge_method=S256&state=x` trên browser: chưa đăng nhập → thấy form login; đăng nhập xong → thấy nút "Cho phép truy cập"; bấm → browser chuyển đến redirect_uri kèm `code` + `state`.
7. Token OAuth phát ra hiển thị trong danh sách PAT của user (tên `Claude MCP (OAuth) …`) và thu hồi được.
