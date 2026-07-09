# Dashboard Share (Link + PIN + Export) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho phép chia sẻ 1 dashboard qua link + PIN cho người xem không có tài khoản (live data, thu hồi được từng link), kèm export HTML tĩnh tự chứa (tuỳ chọn mã hoá AES-GCM bằng PIN).

**Architecture:** Bảng `dashboard_shares` lưu hash của token và PIN. Người xem đi qua đúng 3 endpoint anonymous: trang share, đổi PIN lấy cookie phiên (ASP.NET Data Protection), và đọc data widget — data luôn chạy frozen SQL qua `DashboardService.GetWidgetDataAsync` sẵn có. Quản lý share (tạo/list/revoke) qua JWT + PAT. Export sinh HTML tự chứa, trả download-url một-lần qua IMemoryCache.

**Tech Stack:** ASP.NET Core 8 minimal APIs, Npgsql + Dapper, ASP.NET Data Protection, `Rfc2898DeriveBytes.Pbkdf2` + `AesGcm` (System.Security.Cryptography, không cần package mới), Chart.js self-host (`api/wwwroot/js/chart.umd.min.js`), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-09-dashboard-share-design.md` — đọc trước khi làm.
- Token share: `shr_` + 40 hex (~160 bit); DB **chỉ lưu SHA-256** (theo pattern `ApiKeyAuthenticationHandler.HashKey`).
- PIN: PBKDF2-SHA256 ≥100_000 iterations, salt riêng; mặc định auto-sinh 6 chữ số.
- Mọi token viewer không hợp lệ (không tồn tại / hết hạn / revoked) → **404 body rỗng, không phân biệt lý do**.
- Viewer không bao giờ nhận field `sql`; không endpoint viewer nào nhận SQL.
- Revoke phải thắng cookie: mọi call viewer resolve lại share từ DB.
- Expiry mặc định 30 ngày, clamp 1–90. Cap 10 share sống / dashboard (`SHARE_LIMIT_REACHED`).
- Lockout PIN: mỗi bội số 5 lần sai → khoá `15min * 2^(failed/5 - 1)`.
- Test chạy bằng: `dotnet test tests/ExcelDatasetManager.Tests --nologo -v q` (working dir = repo root). KHÔNG sửa test có sẵn để cho pass.
- Commit sau mỗi task, message tiếng Anh, kết bằng `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## File Structure

| File | Trách nhiệm |
|---|---|
| `api/Migrations/0008_dashboard_shares.sql` | Bảng dashboard_shares (Task 1) |
| `api/Services/ShareCrypto.cs` | Token gen/hash, PIN gen/hash/verify, lockout policy, expiry clamp — thuần, không DB (Task 1) |
| `api/Services/ShareSessionProtector.cs` | Bọc Data Protection cho cookie phiên người xem (Task 2) |
| `api/Services/DashboardShareService.cs` | CRUD share + resolve + pin-failure + record view (DB) (Task 3) |
| `api/Endpoints/ShareEndpoints.cs` | 3 endpoint viewer + trang /share/{token} (Task 4) |
| `api/Endpoints/ShareAdminEndpoints.cs` | POST/GET/DELETE quản lý share (Task 5) |
| `api/Services/DashboardExportService.cs` + `api/Endpoints/ExportEndpoints.cs` | Export HTML + download một-lần (Task 6) |
| `api/wwwroot/share.html` + `api/wwwroot/js/share.js` | Trang người xem (Task 7) |
| `api/wwwroot/js/dashboard.js` + `api/wwwroot/dashboard.html` | Khối UI "Chia sẻ" cho chủ (Task 7) |
| `mcp-bridge/tools.md` + `mcp-bridge/tools.example.md` | 4 MCP tools (Task 8) |
| `tests/ExcelDatasetManager.Tests/ShareCryptoTests.cs`, `ShareSessionProtectorTests.cs`, `DashboardExportCryptoTests.cs` | Unit tests |

Task 9 = e2e + final review.

---

### Task 1: Migration 0008 + ShareCrypto (token/PIN/lockout thuần)

**Files:**
- Create: `api/Migrations/0008_dashboard_shares.sql`
- Create: `api/Services/ShareCrypto.cs`
- Test: `tests/ExcelDatasetManager.Tests/ShareCryptoTests.cs`

**Interfaces (Produces):**
```csharp
public static class ShareCrypto
{
    public const string TokenPrefix = "shr_";
    public const int MaxActiveSharesPerDashboard = 10;
    public static string GenerateToken();                 // "shr_" + 40 hex lowercase
    public static string HashToken(string token);         // SHA-256 hex (uppercase, khớp pattern PAT)
    public static string GeneratePin();                   // 6 chữ số, RNG
    public static string HashPin(string pin);             // "100000.{saltB64}.{hashB64}"
    public static bool VerifyPin(string pin, string stored);
    public static int ClampExpiryDays(int? requested);    // null→30, clamp 1..90
    public static DateTime? NextLockout(int failedPinCount, DateTime nowUtc); // null nếu chưa chạm bội số 5
}
```

- [ ] **Step 1: Viết migration**

```sql
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
```

- [ ] **Step 2: Viết failing tests**

```csharp
// tests/ExcelDatasetManager.Tests/ShareCryptoTests.cs
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ShareCryptoTests
{
    [Fact]
    public void GenerateToken_has_prefix_and_40_hex()
    {
        var t = ShareCrypto.GenerateToken();
        Assert.StartsWith("shr_", t);
        Assert.Matches("^shr_[0-9a-f]{40}$", t);
        Assert.NotEqual(t, ShareCrypto.GenerateToken()); // random
    }

    [Fact]
    public void HashToken_is_stable_and_not_identity()
    {
        var t = ShareCrypto.GenerateToken();
        Assert.Equal(ShareCrypto.HashToken(t), ShareCrypto.HashToken(t));
        Assert.NotEqual(t, ShareCrypto.HashToken(t));
    }

    [Fact]
    public void GeneratePin_is_6_digits()
        => Assert.Matches("^[0-9]{6}$", ShareCrypto.GeneratePin());

    [Fact]
    public void VerifyPin_roundtrip_and_reject_wrong()
    {
        var stored = ShareCrypto.HashPin("482913");
        Assert.True(ShareCrypto.VerifyPin("482913", stored));
        Assert.False(ShareCrypto.VerifyPin("482914", stored));
        Assert.False(ShareCrypto.VerifyPin("", stored));
    }

    [Fact]
    public void HashPin_uses_unique_salt()
        => Assert.NotEqual(ShareCrypto.HashPin("482913"), ShareCrypto.HashPin("482913"));

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(14, 14)]
    [InlineData(365, 90)]
    public void ClampExpiryDays(int? input, int expected)
        => Assert.Equal(expected, ShareCrypto.ClampExpiryDays(input));

    [Theory]
    [InlineData(4, null)]              // chưa chạm bội 5
    [InlineData(5, 15)]                // 15min * 2^0
    [InlineData(10, 30)]               // 15min * 2^1
    [InlineData(15, 60)]
    [InlineData(7, null)]
    public void NextLockout_backoff(int failed, int? minutes)
    {
        var now = new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc);
        var result = ShareCrypto.NextLockout(failed, now);
        if (minutes is null) Assert.Null(result);
        else Assert.Equal(now.AddMinutes(minutes.Value), result);
    }
}
```

- [ ] **Step 3: Chạy test, xác nhận FAIL vì `ShareCrypto` chưa tồn tại (compile error)**

Run: `dotnet test tests/ExcelDatasetManager.Tests --nologo -v q --filter "FullyQualifiedName~ShareCryptoTests"`
Expected: build FAIL `CS0103/CS0246: 'ShareCrypto' does not exist`.

- [ ] **Step 4: Implement**

```csharp
// api/Services/ShareCrypto.cs
using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure crypto/policy helpers for dashboard share links. DB access lives in
/// DashboardShareService; keep this class dependency-free so it stays unit-testable.
/// </summary>
public static class ShareCrypto
{
    public const string TokenPrefix = "shr_";
    public const int MaxActiveSharesPerDashboard = 10;
    private const int PinIterations = 100_000;

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(20); // 160 bit
        return TokenPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Same shape as ApiKeyAuthenticationHandler.HashKey: SHA-256, uppercase hex.
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string GeneratePin()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinIterations, HashAlgorithmName.SHA256, 32);
        return $"{PinIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPin(string pin, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[1]); expected = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static int ClampExpiryDays(int? requested) => Math.Clamp(requested ?? 30, 1, 90);

    /// <summary>Every 5th consecutive failure locks the share for 15min * 2^(n/5 - 1).</summary>
    public static DateTime? NextLockout(int failedPinCount, DateTime nowUtc)
    {
        if (failedPinCount < 5 || failedPinCount % 5 != 0) return null;
        var factor = Math.Min(failedPinCount / 5 - 1, 10); // cap 15min * 2^10 ≈ 10.6 ngày
        return nowUtc.AddMinutes(15 * Math.Pow(2, factor));
    }
}
```

- [ ] **Step 5: Chạy test, xác nhận PASS + toàn suite xanh**

Run: `dotnet test tests/ExcelDatasetManager.Tests --nologo -v q`
Expected: Passed, 0 Failed (353 cũ + 9 mới).

- [ ] **Step 6: Commit**

```bash
git add api/Migrations/0008_dashboard_shares.sql api/Services/ShareCrypto.cs tests/ExcelDatasetManager.Tests/ShareCryptoTests.cs
git commit -m "feat: migration 0008 dashboard_shares + ShareCrypto (token/PIN/lockout)"
```

---

### Task 2: ShareSessionProtector (cookie phiên người xem)

**Files:**
- Create: `api/Services/ShareSessionProtector.cs`
- Modify: `api/Program.cs` (đăng ký DI, cạnh chỗ `builder.Services.AddSingleton(sp => new QueryGuideService(...))`)
- Test: `tests/ExcelDatasetManager.Tests/ShareSessionProtectorTests.cs`

**Interfaces:**
- Consumes: `IDataProtectionProvider` (ASP.NET đăng ký sẵn trong WebApplicationBuilder).
- Produces:
```csharp
public class ShareSessionProtector(IDataProtectionProvider provider)
{
    public const int SessionHours = 12;
    public string Protect(Guid shareId);                  // payload {shareId}|{expiresUtcTicks}
    public Guid? TryUnprotect(string? cookieValue);       // null nếu thiếu/tampered/hết hạn
}
```

- [ ] **Step 1: Viết failing tests**

```csharp
// tests/ExcelDatasetManager.Tests/ShareSessionProtectorTests.cs
using ExcelDatasetManager.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ShareSessionProtectorTests
{
    private static ShareSessionProtector Create()
        => new(DataProtectionProvider.Create("edm-tests"));

    [Fact]
    public void Roundtrip_returns_share_id()
    {
        var p = Create();
        var id = Guid.NewGuid();
        Assert.Equal(id, p.TryUnprotect(p.Protect(id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Invalid_value_returns_null(string? value)
        => Assert.Null(Create().TryUnprotect(value));

    [Fact]
    public void Tampered_value_returns_null()
    {
        var p = Create();
        var cookie = p.Protect(Guid.NewGuid());
        Assert.Null(p.TryUnprotect(cookie[..^2] + "zz"));
    }

    [Fact]
    public void Different_key_ring_returns_null()
    {
        var cookie = Create().Protect(Guid.NewGuid());
        var other = new ShareSessionProtector(DataProtectionProvider.Create("khac"));
        Assert.Null(other.TryUnprotect(cookie));
    }
}
```

- [ ] **Step 2: Chạy filter `ShareSessionProtectorTests`, xác nhận FAIL (compile error — class chưa có)**

- [ ] **Step 3: Implement**

```csharp
// api/Services/ShareSessionProtector.cs
using Microsoft.AspNetCore.DataProtection;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Protects the viewer-session cookie for dashboard shares. The payload only carries the
/// share id + expiry; revocation still wins because every viewer call re-resolves the share
/// row from the DB.
/// </summary>
public class ShareSessionProtector(IDataProtectionProvider provider)
{
    public const int SessionHours = 12;
    private readonly IDataProtector _protector = provider.CreateProtector("edm.dashboard-share.v1");

    public string Protect(Guid shareId)
        => _protector.Protect($"{shareId:N}|{DateTime.UtcNow.AddHours(SessionHours).Ticks}");

    public Guid? TryUnprotect(string? cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue)) return null;
        string plain;
        try { plain = _protector.Unprotect(cookieValue); }
        catch (Exception) { return null; } // CryptographicException on tamper/wrong key

        var parts = plain.Split('|');
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out var shareId)
            || !long.TryParse(parts[1], out var ticks)
            || new DateTime(ticks, DateTimeKind.Utc) < DateTime.UtcNow)
        {
            return null;
        }

        return shareId;
    }
}
```

Đăng ký trong `api/Program.cs`, ngay sau dòng đăng ký `QueryGuideService`:

```csharp
builder.Services.AddSingleton<ShareSessionProtector>();
```

- [ ] **Step 4: Chạy toàn suite, PASS**

- [ ] **Step 5: Commit**

```bash
git add api/Services/ShareSessionProtector.cs api/Program.cs tests/ExcelDatasetManager.Tests/ShareSessionProtectorTests.cs
git commit -m "feat: ShareSessionProtector - data-protected viewer session cookie"
```

---

### Task 3: DashboardShareService (DB)

**Files:**
- Create: `api/Services/DashboardShareService.cs`
- Modify: `api/Models/Errors.cs` (thêm error codes)
- Modify: `api/Program.cs` (đăng ký scoped, cạnh `DashboardService`)

**Interfaces:**
- Consumes: `NpgsqlDataSource` (DI), `ShareCrypto`, bảng `dashboards` (check ownership).
- Produces:
```csharp
public sealed record ShareRecord(
    Guid Id, Guid DashboardId, Guid UserId, string PinHash,
    DateTime ExpiresAt, int FailedPinCount, DateTime? LockedUntil, int ViewCount);

public class DashboardShareService(NpgsqlDataSource dataSource)
{
    // Trả (Result, Token, Pin): token/pin CHỈ tồn tại trong response này.
    Task<ApiResult<object>> CreateAsync(Guid userId, Guid dashboardId, string? pin, int? expiresInDays, string createdBy, CancellationToken ct);
    Task<ShareRecord?> ResolveAsync(string token, CancellationToken ct);  // null nếu không tồn tại/revoked/expired
    Task<ApiResult<object>> ListAsync(Guid userId, Guid dashboardId, CancellationToken ct);
    Task<bool> RevokeAsync(Guid userId, Guid shareId, CancellationToken ct);
    Task RegisterPinFailureAsync(Guid shareId, CancellationToken ct);
    Task ResetPinFailuresAsync(Guid shareId, CancellationToken ct);
    Task RecordViewAsync(Guid shareId, CancellationToken ct);
}
```
Error codes thêm vào `Errors.cs` (mục `// Dashboard`): `ShareLimitReached = "SHARE_LIMIT_REACHED"`, `ShareNotFound = "SHARE_NOT_FOUND"`.

Lưu ý: service này là DB-bound — suite unit không có Postgres, nên task này không có unit test riêng; hành vi được cover ở Task 9 (e2e). Vẫn TDD được phần compile-consumer: Task 4/5 viết sau sẽ dùng đúng chữ ký này.

- [ ] **Step 1: Thêm error codes vào `api/Models/Errors.cs`** (trong khối `// Dashboard`):

```csharp
    public const string ShareLimitReached = "SHARE_LIMIT_REACHED";
    public const string ShareNotFound = "SHARE_NOT_FOUND";
```

- [ ] **Step 2: Implement service**

```csharp
// api/Services/DashboardShareService.cs
using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public sealed record ShareRecord(
    Guid Id, Guid DashboardId, Guid UserId, string PinHash,
    DateTime ExpiresAt, int FailedPinCount, DateTime? LockedUntil, int ViewCount);

/// <summary>
/// Persistence for dashboard share links. Tokens and PINs are stored hashed only —
/// the plaintext pair leaves this class exactly once, in CreateAsync's response.
/// </summary>
public class DashboardShareService(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    public async Task<ApiResult<object>> CreateAsync(
        Guid userId, Guid dashboardId, string? pin, int? expiresInDays, string createdBy, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var owns = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboards WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });
        if (owns == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var active = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dashboard_shares
            WHERE dashboard_id = @Id AND revoked_at IS NULL AND expires_at > NOW()
            """, new { Id = dashboardId });
        if (active >= ShareCrypto.MaxActiveSharesPerDashboard)
        {
            return ApiResult<object>.Fail(ErrorCodes.ShareLimitReached,
                $"This dashboard already has {ShareCrypto.MaxActiveSharesPerDashboard} active share links. Revoke one first.");
        }

        if (pin is not null && (pin.Length < 4 || pin.Length > 32))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "PIN must be 4-32 characters.");
        }

        var token = ShareCrypto.GenerateToken();
        var actualPin = pin ?? ShareCrypto.GeneratePin();
        var days = ShareCrypto.ClampExpiryDays(expiresInDays);
        var id = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(days);

        await conn.ExecuteAsync("""
            INSERT INTO dashboard_shares (id, dashboard_id, user_id, token_hash, pin_hash, created_by, expires_at)
            VALUES (@Id, @DashboardId, @UserId, @TokenHash, @PinHash, @CreatedBy, @ExpiresAt)
            """, new
        {
            Id = id, DashboardId = dashboardId, UserId = userId,
            TokenHash = ShareCrypto.HashToken(token),
            PinHash = ShareCrypto.HashPin(actualPin),
            CreatedBy = createdBy, ExpiresAt = expiresAt
        });

        var publicUrl = (configuration["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');
        return ApiResult<object>.Ok(new
        {
            share_id = id,
            share_url = $"{publicUrl}/share/{token}",
            pin = actualPin,
            expires_at = expiresAt,
            note = "The link and PIN are shown only once. Deliver them to the viewer over two separate channels when possible."
        });
    }

    public async Task<ShareRecord?> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(ShareCrypto.TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ShareRecord>("""
            SELECT id AS Id, dashboard_id AS DashboardId, user_id AS UserId, pin_hash AS PinHash,
                   expires_at AS ExpiresAt, failed_pin_count AS FailedPinCount,
                   locked_until AS LockedUntil, view_count AS ViewCount
            FROM dashboard_shares
            WHERE token_hash = @TokenHash AND revoked_at IS NULL AND expires_at > NOW()
            """, new { TokenHash = ShareCrypto.HashToken(token.Trim()) });
    }

    public async Task<ApiResult<object>> ListAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync("""
            SELECT id AS share_id, created_by, created_at, expires_at, view_count, last_viewed_at,
                   (revoked_at IS NOT NULL) AS revoked
            FROM dashboard_shares s
            WHERE s.dashboard_id = @DashboardId AND s.user_id = @UserId AND s.revoked_at IS NULL AND s.expires_at > NOW()
            ORDER BY s.created_at DESC
            """, new { DashboardId = dashboardId, UserId = userId });
        return ApiResult<object>.Ok(new { shares = rows });
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET revoked_at = NOW() WHERE id = @Id AND user_id = @UserId AND revoked_at IS NULL",
            new { Id = shareId, UserId = userId });
        return affected > 0;
    }

    public async Task RegisterPinFailureAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var failed = await conn.ExecuteScalarAsync<int>("""
            UPDATE dashboard_shares SET failed_pin_count = failed_pin_count + 1
            WHERE id = @Id RETURNING failed_pin_count
            """, new { Id = shareId });
        var lockedUntil = ShareCrypto.NextLockout(failed, DateTime.UtcNow);
        if (lockedUntil is not null)
        {
            await conn.ExecuteAsync(
                "UPDATE dashboard_shares SET locked_until = @Until WHERE id = @Id",
                new { Until = lockedUntil, Id = shareId });
        }
    }

    public async Task ResetPinFailuresAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET failed_pin_count = 0, locked_until = NULL WHERE id = @Id",
            new { Id = shareId });
    }

    public async Task RecordViewAsync(Guid shareId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE dashboard_shares SET view_count = view_count + 1, last_viewed_at = NOW() WHERE id = @Id",
            new { Id = shareId });
    }
}
```

- [ ] **Step 3: Đăng ký DI trong `api/Program.cs`** cạnh `builder.Services.AddScoped<DashboardService>();`:

```csharp
builder.Services.AddScoped<DashboardShareService>();
```

- [ ] **Step 4: Build + toàn suite PASS** — `dotnet test tests/ExcelDatasetManager.Tests --nologo -v q`

- [ ] **Step 5: Commit**

```bash
git add api/Services/DashboardShareService.cs api/Models/Errors.cs api/Program.cs
git commit -m "feat: DashboardShareService - hashed share links with revoke/lockout/audit"
```

---

### Task 4: Viewer endpoints + rate limiter PIN

**Files:**
- Create: `api/Endpoints/ShareEndpoints.cs`
- Modify: `api/Program.cs` (thêm limiter `share-pin` cạnh policy `query`; map endpoints cạnh `app.MapQueryGuideEndpoints();`)
- Modify: `api/Services/DashboardService.cs` (thêm `GetShareViewAsync` — bản dashboard KHÔNG có SQL)

**Interfaces:**
- Consumes: `DashboardShareService.ResolveAsync/RegisterPinFailureAsync/ResetPinFailuresAsync/RecordViewAsync`, `ShareSessionProtector`, `ShareCrypto.VerifyPin`, `DashboardService.GetWidgetDataAsync(Guid userId, Guid dashboardId, Guid widgetId, CancellationToken ct)`.
- Produces (route công khai — Task 7 UI sẽ gọi):
  - `GET /share/{token}` → HTML (`wwwroot/share.html`), 404 nếu share chết.
  - `POST /api/share/{token}/session` body `{"pin":"..."}` → 204 + Set-Cookie `edm_share_{shareIdN}` | 401 sai PIN | 429 khoá.
  - `GET /api/share/{token}/dashboard` → `{success, data:{dashboard_name, widgets:[{widget_id,title,chart_type,chart_config,position}]}}` — không `sql`.
  - `GET /api/share/{token}/widgets/{widgetId}/data` → nguyên shape data của `GetWidgetDataAsync`.
- Produces cho DashboardService:
```csharp
public async Task<ApiResult<object>> GetShareViewAsync(Guid userId, Guid dashboardId, CancellationToken ct)
// data = { dashboard_name, widgets: [ { widget_id, title, chart_type, chart_config, position } ] } — KHÔNG sql
```

- [ ] **Step 1: Thêm `GetShareViewAsync` vào `api/Services/DashboardService.cs`** (đặt cạnh `GetWidgetDatasetIdAsync`, dùng `SelectOwnedWidgetSql`-style join sẵn có):

```csharp
    /// <summary>
    /// Dashboard payload for anonymous share viewers: widget metadata WITHOUT the SQL —
    /// viewers must not learn schema/business logic from queries.
    /// </summary>
    public async Task<ApiResult<object>> GetShareViewAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var name = await conn.ExecuteScalarAsync<string?>(
            "SELECT name FROM dashboards WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });
        if (name is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var widgets = await conn.QueryAsync("""
            SELECT w.id AS widget_id, w.title, w.chart_type, w.chart_config::text AS chart_config_json,
                   w.position
            FROM dashboard_widgets w
            WHERE w.dashboard_id = @DashboardId AND w.archived_at IS NULL
            ORDER BY w.position
            """, new { DashboardId = dashboardId });

        return ApiResult<object>.Ok(new
        {
            dashboard_name = name,
            widgets = widgets.Select(w => new
            {
                w.widget_id,
                w.title,
                w.chart_type,
                chart_config = w.chart_config_json is string s && s.Length > 0
                    ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>((string)w.chart_config_json)
                    : (object?)null,
                w.position
            })
        });
    }
```

- [ ] **Step 2: Thêm limiter `share-pin` vào `api/Program.cs`** (ngay sau policy `query`):

```csharp
    // Anonymous PIN attempts on share links: strict per-IP window on top of the per-share
    // lockout stored in the DB.
    options.AddPolicy("share-pin", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
```

- [ ] **Step 3: Viết `api/Endpoints/ShareEndpoints.cs`**

```csharp
// api/Endpoints/ShareEndpoints.cs
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// The ONLY three routes an anonymous share viewer can reach. Every route re-resolves the
/// share row first (revoke/expiry wins over any cookie), and every invalid token yields a
/// bare 404 — never a reason — so the routes can't be used as an existence oracle.
/// No route here accepts SQL, and no response contains SQL.
/// </summary>
public static class ShareEndpoints
{
    public record PinRequest(string? Pin);

    public static void MapShareEndpoints(this WebApplication app)
    {
        app.MapGet("/share/{token}", async (
            string token, HttpContext ctx, DashboardShareService shares,
            IWebHostEnvironment env, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null) return Results.NotFound();

            ApplyShareHeaders(ctx);
            return Results.File(Path.Combine(env.WebRootPath, "share.html"), "text/html; charset=utf-8");
        });

        app.MapPost("/api/share/{token}/session", async (
            string token, PinRequest req, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null) return Results.NotFound();

            var log = loggerFactory.CreateLogger("ShareEndpoints");
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (share.LockedUntil is DateTime locked && locked > DateTime.UtcNow)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling((locked - DateTime.UtcNow).TotalMinutes));
                return Results.Json(new
                {
                    success = false,
                    error = new { code = "SHARE_LOCKED", message = $"Too many wrong PINs. Try again in {minutes} minute(s)." }
                }, statusCode: 429);
            }

            if (string.IsNullOrEmpty(req.Pin) || !ShareCrypto.VerifyPin(req.Pin, share.PinHash))
            {
                await shares.RegisterPinFailureAsync(share.Id, ct);
                log.LogWarning("Share {ShareId}: wrong PIN from {Ip}", share.Id, ip);
                return Results.Json(new
                {
                    success = false,
                    error = new { code = "SHARE_PIN_INVALID", message = "Wrong PIN." }
                }, statusCode: 401);
            }

            await shares.ResetPinFailuresAsync(share.Id, ct);
            // Path giới hạn /api/share theo spec — cookie không bao giờ đi kèm request nào khác.
            // Secure: sau IIS/ARR (TLS terminate ở proxy) IsHttps chỉ đúng khi app có
            // UseForwardedHeaders; nếu chưa có, thêm ForwardedHeaders middleware trước auth.
            ctx.Response.Cookies.Append(CookieName(share.Id), protector.Protect(share.Id), new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/api/share",
                MaxAge = TimeSpan.FromHours(ShareSessionProtector.SessionHours)
            });
            return Results.NoContent();
        })
        .RequireRateLimiting("share-pin");

        app.MapGet("/api/share/{token}/dashboard", async (
            string token, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector, DashboardService dashboards,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null || !HasValidSession(ctx, protector, share.Id)) return Results.NotFound();

            ApplyShareHeaders(ctx);
            await shares.RecordViewAsync(share.Id, ct);
            loggerFactory.CreateLogger("ShareEndpoints").LogInformation(
                "Share {ShareId} viewed from {Ip}", share.Id, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            var view = await dashboards.GetShareViewAsync(share.UserId, share.DashboardId, ct);
            return view.Success
                ? Results.Ok(new { success = true, data = view.Data })
                : Results.NotFound();
        });

        app.MapGet("/api/share/{token}/widgets/{widgetId:guid}/data", async (
            string token, Guid widgetId, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector, DashboardService dashboards,
            CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null || !HasValidSession(ctx, protector, share.Id)) return Results.NotFound();

            ApplyShareHeaders(ctx);
            // GetWidgetDataAsync joins widget→dashboard→user, so a widgetId outside this
            // share's dashboard fails ownership inside the service. Runs the FROZEN sql only.
            var result = await dashboards.GetWidgetDataAsync(share.UserId, share.DashboardId, widgetId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound();
        })
        .RequireRateLimiting("query");
    }

    private static string CookieName(Guid shareId) => $"edm_share_{shareId:N}";

    private static bool HasValidSession(HttpContext ctx, ShareSessionProtector protector, Guid shareId)
        => ctx.Request.Cookies.TryGetValue(CookieName(shareId), out var value)
           && protector.TryUnprotect(value) == shareId;

    private static void ApplyShareHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:";
    }
}
```

- [ ] **Step 4: Map trong `api/Program.cs`** sau `app.MapQueryGuideEndpoints();`:

```csharp
app.MapShareEndpoints();
```

- [ ] **Step 5: Build + toàn suite PASS** (`share.html` chưa tồn tại — route trả file sẽ 404 lúc runtime, chấp nhận tới Task 7).

- [ ] **Step 6: Commit**

```bash
git add api/Endpoints/ShareEndpoints.cs api/Services/DashboardService.cs api/Program.cs
git commit -m "feat: anonymous share viewer endpoints - PIN session + no-SQL dashboard view"
```

---

### Task 5: Endpoint quản lý share (JWT + PAT)

**Files:**
- Create: `api/Endpoints/ShareAdminEndpoints.cs`
- Modify: `api/Program.cs` (map cạnh `app.MapShareEndpoints();`)

**Interfaces:**
- Consumes: `DashboardShareService.CreateAsync/ListAsync/RevokeAsync`, `ClaimsPrincipalExtensions.GetUserId/IsApiKeyPrincipal`.
- Produces (Task 8 tools.md mô tả đúng các route này):
  - `POST /api/dashboards/{id}/shares` body `{pin?, expires_in_days?}` — policy `KnowledgeWrite`.
  - `GET /api/dashboards/{id}/shares` — policy `KnowledgeWrite`.
  - `DELETE /api/shares/{shareId}` — policy `KnowledgeWrite`.

- [ ] **Step 1: Viết `api/Endpoints/ShareAdminEndpoints.cs`**

```csharp
// api/Endpoints/ShareAdminEndpoints.cs
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// Owner-side share management. JWT and PAT both allowed (the user explicitly wants the AI
/// to create and revoke share links); everything is scoped by user_id, and the token+PIN
/// pair appears exactly once — in the create response.
/// </summary>
public static class ShareAdminEndpoints
{
    public record CreateShareRequest(string? Pin, int? ExpiresInDays);

    public static void MapShareAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/dashboards/{id:guid}/shares", async (
            Guid id, CreateShareRequest req, ClaimsPrincipal principal,
            DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var createdBy = principal.IsApiKeyPrincipal()
                ? "ai"
                : $"user:{principal.FindFirstValue(ClaimTypes.Email) ?? userId.Value.ToString()}";

            var result = await shares.CreateAsync(userId.Value, id, req.Pin, req.ExpiresInDays, createdBy, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.BadRequest(new { success = false, error = result.Error });
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapGet("/api/dashboards/{id:guid}/shares", async (
            Guid id, ClaimsPrincipal principal, DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await shares.ListAsync(userId.Value, id, ct);
            return Results.Ok(new { success = true, data = result.Data });
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/shares/{shareId:guid}", async (
            Guid shareId, ClaimsPrincipal principal, DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var revoked = await shares.RevokeAsync(userId.Value, shareId, ct);
            return revoked
                ? Results.Ok(new { success = true, data = new { revoked = true, share_id = shareId } })
                : Results.NotFound(new { success = false, error = new { code = ErrorCodes.ShareNotFound, message = "Share not found." } });
        })
        .RequireAuthorization("KnowledgeWrite");
    }
}
```

- [ ] **Step 2: Map trong `api/Program.cs`:** `app.MapShareAdminEndpoints();`

- [ ] **Step 3: Build + toàn suite PASS**

- [ ] **Step 4: Commit**

```bash
git add api/Endpoints/ShareAdminEndpoints.cs api/Program.cs
git commit -m "feat: share admin endpoints - create/list/revoke via JWT+PAT"
```

---

### Task 6: Export HTML tĩnh + download một-lần

**Files:**
- Create: `api/Services/DashboardExportService.cs`
- Create: `api/Endpoints/ExportEndpoints.cs`
- Modify: `api/Program.cs` (đăng ký scoped + map)
- Test: `tests/ExcelDatasetManager.Tests/DashboardExportCryptoTests.cs`

**Interfaces:**
- Consumes: `DashboardService.GetShareViewAsync` + `GetWidgetDataAsync`, `IMemoryCache` (đã có `AddMemoryCache()`), `IWebHostEnvironment` (đọc `wwwroot/js/chart.umd.min.js` để inline).
- Produces:
```csharp
public static class ExportCrypto
{
    // Trả (saltB64, ivB64, cipherWithTagB64): AES-256-GCM, key = PBKDF2(pin, salt, 150_000, SHA256).
    // cipherWithTag = ciphertext || tag(16B) — đúng format WebCrypto decrypt.
    public static (string SaltB64, string IvB64, string CipherB64) Encrypt(string pin, string plaintextJson);
    public static string Decrypt(string pin, string saltB64, string ivB64, string cipherB64); // reference cho test
}

public class DashboardExportService(DashboardService dashboards, IWebHostEnvironment env)
{
    public Task<ApiResult<string>> BuildHtmlAsync(Guid userId, Guid dashboardId, string? pin, CancellationToken ct);
}
```
  - `POST /api/dashboards/{id}/export` body `{pin?}` (policy `KnowledgeWrite`) → `{success, data:{download_url, expires_in_sec: 600}}`.
  - `GET /api/exports/{exportToken}` (anonymous, một-lần) → file HTML download.

- [ ] **Step 1: Viết failing test cho ExportCrypto**

```csharp
// tests/ExcelDatasetManager.Tests/DashboardExportCryptoTests.cs
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardExportCryptoTests
{
    [Fact]
    public void Encrypt_then_decrypt_roundtrips()
    {
        var (salt, iv, cipher) = ExportCrypto.Encrypt("1234", """{"a":1}""");
        Assert.Equal("""{"a":1}""", ExportCrypto.Decrypt("1234", salt, iv, cipher));
    }

    [Fact]
    public void Wrong_pin_fails_authentication()
    {
        var (salt, iv, cipher) = ExportCrypto.Encrypt("1234", """{"a":1}""");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ExportCrypto.Decrypt("9999", salt, iv, cipher));
    }

    [Fact]
    public void Cipher_carries_appended_tag()
    {
        var (_, _, cipher) = ExportCrypto.Encrypt("1234", "x");
        Assert.Equal(1 + 16, Convert.FromBase64String(cipher).Length); // 1 byte plaintext + 16 byte GCM tag
    }
}
```

- [ ] **Step 2: Chạy filter `DashboardExportCryptoTests`, xác nhận FAIL (compile — `ExportCrypto` chưa có)**

- [ ] **Step 3: Implement `api/Services/DashboardExportService.cs`**

```csharp
// api/Services/DashboardExportService.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDatasetManager.Api.Models;

namespace ExcelDatasetManager.Api.Services;

/// <summary>AES-GCM with a PBKDF2-derived key. Cipher output = ciphertext||tag so the
/// browser can decrypt it directly with WebCrypto (which expects the tag appended).</summary>
public static class ExportCrypto
{
    private const int Iterations = 150_000;

    public static (string SaltB64, string IvB64, string CipherB64) Encrypt(string pin, string plaintextJson)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(plaintextJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plain, cipher, tag);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(iv),
                Convert.ToBase64String(cipher.Concat(tag).ToArray()));
    }

    public static string Decrypt(string pin, string saltB64, string ivB64, string cipherB64)
    {
        var salt = Convert.FromBase64String(saltB64);
        var iv = Convert.FromBase64String(ivB64);
        var all = Convert.FromBase64String(cipherB64);
        var cipher = all[..^16];
        var tag = all[^16..];
        var key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Builds a fully self-contained snapshot HTML for a dashboard: Chart.js inlined,
/// widget data embedded (optionally AES-GCM-encrypted behind a PIN), timestamp banner.</summary>
public class DashboardExportService(DashboardService dashboards, IWebHostEnvironment env)
{
    public async Task<ApiResult<string>> BuildHtmlAsync(Guid userId, Guid dashboardId, string? pin, CancellationToken ct)
    {
        var view = await dashboards.GetShareViewAsync(userId, dashboardId, ct);
        if (!view.Success) return ApiResult<string>.Fail(view.Error!.Code, view.Error.Message);

        // Chạy frozen SQL từng widget đúng 1 lần, gom {widget_id -> data}.
        var viewJson = JsonSerializer.SerializeToElement(view.Data, JsonOpts);
        var widgetData = new Dictionary<string, JsonElement>();
        foreach (var w in viewJson.GetProperty("widgets").EnumerateArray())
        {
            var widgetId = w.GetProperty("widget_id").GetGuid();
            var data = await dashboards.GetWidgetDataAsync(userId, dashboardId, widgetId, ct);
            if (data.Success)
            {
                widgetData[widgetId.ToString()] = JsonSerializer.SerializeToElement(data.Data, JsonOpts);
            }
        }

        var payloadJson = JsonSerializer.Serialize(new { view = viewJson, data = widgetData }, JsonOpts);
        var chartJs = await File.ReadAllTextAsync(Path.Combine(env.WebRootPath, "js", "chart.umd.min.js"), ct);
        var stamp = DateTime.Now.ToString("HH:mm dd/MM/yyyy");
        var name = viewJson.GetProperty("dashboard_name").GetString() ?? "Dashboard";

        string body;
        if (string.IsNullOrEmpty(pin))
        {
            body = $"<script>window.__EDM_PAYLOAD__ = {payloadJson};</script>";
        }
        else
        {
            var (salt, iv, cipher) = ExportCrypto.Encrypt(pin, payloadJson);
            body = $$"""
                <script>
                window.__EDM_ENC__ = { salt: "{{salt}}", iv: "{{iv}}", cipher: "{{cipher}}", iterations: 150000 };
                </script>
                """;
        }

        var html = BuildShell(name, stamp, chartJs, body, encrypted: !string.IsNullOrEmpty(pin));
        return ApiResult<string>.Ok(html);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string BuildShell(string name, string stamp, string chartJs, string payloadBlock, bool encrypted) => $$"""
        <!DOCTYPE html>
        <html lang="vi"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{System.Net.WebUtility.HtmlEncode(name)}} — snapshot</title>
        <style>
        body{font-family:system-ui,sans-serif;margin:0;background:#f4f5f7;color:#1c1e21}
        header{background:#fff;padding:12px 20px;border-bottom:1px solid #e0e2e6}
        header .stamp{color:#8a8f98;font-size:13px}
        .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:16px;padding:16px}
        .card{background:#fff;border:1px solid #e0e2e6;border-radius:8px;padding:12px}
        .card h3{margin:0 0 8px;font-size:15px}
        table{border-collapse:collapse;width:100%;font-size:13px}
        td,th{border:1px solid #e0e2e6;padding:4px 8px;text-align:left}
        #pin-gate{max-width:340px;margin:80px auto;background:#fff;padding:24px;border-radius:8px;border:1px solid #e0e2e6}
        </style>
        <script>{{chartJs}}</script>
        {{payloadBlock}}
        </head><body>
        <header><strong>{{System.Net.WebUtility.HtmlEncode(name)}}</strong>
          <span class="stamp">— Snapshot lúc {{stamp}} (số liệu đóng băng tại thời điểm xuất)</span></header>
        {{(encrypted ? """
        <div id="pin-gate"><h3>Nhập PIN để mở báo cáo</h3>
          <input id="pin" type="password" inputmode="numeric" style="width:100%;padding:8px">
          <button onclick="unlock()" style="margin-top:8px;padding:8px 16px">Mở</button>
          <p id="pin-err" style="color:#c0392b;display:none">PIN sai.</p></div>
        """ : "")}}
        <div class="grid" id="grid"></div>
        <script>
        async function unlock() {
          const enc = window.__EDM_ENC__;
          const pin = document.getElementById('pin').value;
          try {
            const dec = new TextDecoder();
            const b64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
            const keyMaterial = await crypto.subtle.importKey('raw', new TextEncoder().encode(pin), 'PBKDF2', false, ['deriveKey']);
            const key = await crypto.subtle.deriveKey(
              { name: 'PBKDF2', salt: b64(enc.salt), iterations: enc.iterations, hash: 'SHA-256' },
              keyMaterial, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
            const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: b64(enc.iv) }, key, b64(enc.cipher));
            window.__EDM_PAYLOAD__ = JSON.parse(dec.decode(plain));
            document.getElementById('pin-gate').remove();
            render();
          } catch (e) { document.getElementById('pin-err').style.display = 'block'; }
        }
        function render() {
          const p = window.__EDM_PAYLOAD__;
          if (!p) return;
          const grid = document.getElementById('grid');
          for (const w of p.view.widgets) {
            const card = document.createElement('div'); card.className = 'card';
            card.innerHTML = '<h3></h3>'; card.querySelector('h3').textContent = w.title;
            const d = p.data[w.widget_id];
            if (!d) { card.append('Không có dữ liệu.'); grid.append(card); continue; }
            const cols = d.columns || [], rows = d.rows || [];
            if (w.chart_type === 'table' || !window.Chart) {
              const t = document.createElement('table');
              t.innerHTML = '<thead><tr></tr></thead><tbody></tbody>';
              for (const c of cols) { const th = document.createElement('th'); th.textContent = c; t.tHead.rows[0].append(th); }
              for (const r of rows.slice(0, 100)) {
                const tr = t.tBodies[0].insertRow();
                for (const v of r) tr.insertCell().textContent = v === null ? '' : String(v);
              }
              card.append(t);
            } else {
              const cv = document.createElement('canvas'); card.append(cv);
              const labels = rows.map(r => r[0]);
              const datasets = cols.slice(1).map((c, i) => ({ label: c, data: rows.map(r => r[i + 1]) }));
              new Chart(cv, { type: w.chart_type === 'stat' ? 'bar' : w.chart_type, data: { labels, datasets } });
            }
            grid.append(card);
          }
        }
        render();
        </script></body></html>
        """;
}
```

Lưu ý cho executor: shape `d.columns/d.rows` phải khớp shape thật `GetWidgetDataAsync` trả (xem `DashboardService` — nếu data là `{columns:[{name,...}], rows:[[...]]}` thì map `cols = d.columns.map(c=>c.name)`); chỉnh JS render theo shape thực tế, KHÔNG đổi shape API.

- [ ] **Step 4: Chạy filter `DashboardExportCryptoTests` → PASS; toàn suite PASS**

- [ ] **Step 5: Viết `api/Endpoints/ExportEndpoints.cs`**

```csharp
// api/Endpoints/ExportEndpoints.cs
using System.Security.Claims;
using System.Security.Cryptography;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Caching.Memory;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>Snapshot export. The HTML is parked in IMemoryCache behind a random one-time
/// token for 10 minutes instead of being streamed back through MCP (token bloat).</summary>
public static class ExportEndpoints
{
    public record ExportRequest(string? Pin);

    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/dashboards/{id:guid}/export", async (
            Guid id, ExportRequest req, ClaimsPrincipal principal,
            DashboardExportService export, IMemoryCache cache, IConfiguration config, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            if (req.Pin is not null && (req.Pin.Length < 4 || req.Pin.Length > 32))
            {
                return Results.BadRequest(new { success = false, error = new { code = "VALIDATION_ERROR", message = "PIN must be 4-32 characters." } });
            }

            var html = await export.BuildHtmlAsync(userId.Value, id, req.Pin, ct);
            if (!html.Success)
            {
                return Results.NotFound(new { success = false, error = html.Error });
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
            cache.Set($"export:{token}", html.Data!, TimeSpan.FromMinutes(10));

            var publicUrl = (config["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    download_url = $"{publicUrl}/api/exports/{token}",
                    expires_in_sec = 600,
                    one_time = true,
                    encrypted = req.Pin is not null
                }
            });
        })
        .RequireAuthorization("KnowledgeWrite")
        .RequireRateLimiting("query");

        app.MapGet("/api/exports/{token}", (string token, IMemoryCache cache) =>
        {
            var key = $"export:{token}";
            if (!cache.TryGetValue(key, out string? html) || html is null) return Results.NotFound();
            cache.Remove(key); // one-time
            return Results.File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", "dashboard-snapshot.html");
        });
    }
}
```

- [ ] **Step 6: Đăng ký + map trong `api/Program.cs`:**

```csharp
builder.Services.AddScoped<DashboardExportService>();   // cạnh DashboardShareService
app.MapExportEndpoints();                                // cạnh MapShareAdminEndpoints
```

- [ ] **Step 7: Build + toàn suite PASS**

- [ ] **Step 8: Commit**

```bash
git add api/Services/DashboardExportService.cs api/Endpoints/ExportEndpoints.cs api/Program.cs tests/ExcelDatasetManager.Tests/DashboardExportCryptoTests.cs
git commit -m "feat: static HTML dashboard export with optional AES-GCM PIN + one-time download"
```

---

### Task 7: UI — trang người xem + khối quản lý share

**Files:**
- Create: `api/wwwroot/share.html`, `api/wwwroot/js/share.js`
- Modify: `api/wwwroot/dashboard.html` + `api/wwwroot/js/dashboard.js` (thêm nút/khối "Chia sẻ")

**Interfaces:**
- Consumes: các route Task 4/5. Token lấy từ `location.pathname.split('/').pop()`.
- Style/pattern: theo `css/style.css` + cách `js/dashboard.js` render widget bằng `chart.umd.min.js` sẵn có — **đọc `js/dashboard.js` trước và tái dùng hàm render nếu tách được**.

- [ ] **Step 1: `share.html`** — layout tối giản: `<div id="pin-gate">` (input PIN + nút Mở + chỗ báo lỗi/khoá) và `<div id="dash" hidden>` (tiêu đề + grid widget). Load `js/chart.umd.min.js` + `js/share.js`. KHÔNG load `js/auth.js`/`js/api.js` (trang anonymous).

- [ ] **Step 2: `js/share.js`** — logic:

```javascript
// api/wwwroot/js/share.js — viewer page for /share/{token}. Anonymous: no auth.js.
const token = location.pathname.split('/').pop();

async function submitPin() {
  const pin = document.getElementById('pin').value;
  const res = await fetch(`/api/share/${token}/session`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pin })
  });
  if (res.status === 204) return loadDashboard();
  const err = await res.json().catch(() => null);
  showError(res.status === 429
    ? (err?.error?.message || 'Tạm khóa vì sai PIN nhiều lần.')
    : 'PIN sai.');
}

async function loadDashboard() {
  const res = await fetch(`/api/share/${token}/dashboard`);
  if (!res.ok) { showGate(); return; } // cookie hết hạn/revoked → quay lại nhập PIN
  const { data } = await res.json();
  document.getElementById('pin-gate').hidden = true;
  const dash = document.getElementById('dash');
  dash.hidden = false;
  dash.querySelector('h1').textContent = data.dashboard_name;
  for (const w of data.widgets) renderWidget(w); // fetch /widgets/{id}/data song song, vẽ Chart.js
}

// renderWidget: tái dùng pattern vẽ chart/table của js/dashboard.js (đọc file đó và
// copy hàm render, đổi endpoint data sang /api/share/{token}/widgets/{id}/data).

loadDashboard(); // thử luôn: nếu cookie còn hạn thì khỏi nhập PIN
```

(Executor viết đầy đủ `renderWidget`/`showGate`/`showError` theo pattern `dashboard.js` — giữ đúng shape data thật.)

- [ ] **Step 3: Khối "Chia sẻ" trong `dashboard.html`/`js/dashboard.js`** — nút "Chia sẻ" mở panel:
  - List share sống (`GET /api/dashboards/{id}/shares`): created_by, hạn, lượt xem, nút "Thu hồi" (`DELETE /api/shares/{shareId}` + confirm).
  - Form tạo: PIN (để trống = tự sinh), số ngày (mặc định 30) → `POST /api/dashboards/{id}/shares` → hiện `share_url` + `pin` MỘT LẦN kèm cảnh báo "PIN sẽ không xem lại được — hãy lưu ngay" + nút copy.
  - Nút "Xuất file HTML": gọi `POST /api/dashboards/{id}/export` (hỏi PIN tuỳ chọn) → mở `download_url`.

- [ ] **Step 4: Kiểm tra thủ công bằng dev stack** (`docker compose up` theo README dev): tạo dashboard có 1 widget → tạo share → mở link ẩn danh → nhập PIN → thấy chart; revoke → F5 → 404.

- [ ] **Step 5: Commit**

```bash
git add api/wwwroot/share.html api/wwwroot/js/share.js api/wwwroot/dashboard.html api/wwwroot/js/dashboard.js
git commit -m "feat: share viewer page + owner share management UI"
```

---

### Task 8: MCP tools (tools.md + tools.example.md)

**Files:**
- Modify: `mcp-bridge/tools.md`, `mcp-bridge/tools.example.md` (thêm 4 tool, đặt sau `update_dashboard_widget`)

**Interfaces:** Consumes các route Task 5/6. Format YAML y hệt các tool hiện có (xem `create_dashboard_widget` làm mẫu).

- [ ] **Step 1: Thêm 4 tool** (nội dung giống nhau cho cả 2 file):

```yaml
## share_dashboard

type: tool
name: share_dashboard
description: |
  Create a share link + PIN so someone WITHOUT an EDM account (e.g. the user's
  boss) can view this dashboard live in a browser. The link and PIN are
  returned ONCE and cannot be retrieved again. Present both to the user and
  advise sending the link and the PIN over two separate channels.
connection: edm
method: POST
path: /api/dashboards/{dashboard_id}/shares
params:
  dashboard_id: { in: path, type: string, required: true, description: UUID from list_dashboards. }
  pin: { in: body, type: string, description: Optional custom PIN (4-32 chars). Omit to auto-generate a 6-digit PIN. }
  expires_in_days: { in: body, type: integer, description: Link lifetime in days, default 30, max 90. }
response_hint: |
  Shape: {success, data: {share_id, share_url, pin, expires_at, note}}.
  Keep share_id if the user may want to revoke this link later.
  error.code=SHARE_LIMIT_REACHED means 10 active links exist - revoke one first.

## list_dashboard_shares

type: tool
name: list_dashboard_shares
description: List active share links of a dashboard (metadata only - tokens/PINs are never retrievable).
connection: edm
method: GET
path: /api/dashboards/{dashboard_id}/shares
params:
  dashboard_id: { in: path, type: string, required: true, description: UUID from list_dashboards. }
response_hint: |
  Shape: {success, data: {shares: [{share_id, created_by, created_at, expires_at, view_count, last_viewed_at}]}}.

## revoke_dashboard_share

type: tool
name: revoke_dashboard_share
description: |
  Immediately revoke one share link (e.g. when the user says a link leaked or
  someone should lose access). Viewers with that link lose access at once.
connection: edm
method: DELETE
path: /api/shares/{share_id}
params:
  share_id: { in: path, type: string, required: true, description: UUID from share_dashboard or list_dashboard_shares. }
response_hint: |
  Shape: {success, data: {revoked, share_id}}. error.code=SHARE_NOT_FOUND if already revoked/unknown.

## export_dashboard

type: tool
name: export_dashboard
description: |
  Export a dashboard as a self-contained snapshot HTML file (data frozen at
  export time) for sending by email/chat. Optionally protect the file with a
  PIN - the embedded data is then AES-GCM encrypted and unreadable without it.
connection: edm
method: POST
path: /api/dashboards/{dashboard_id}/export
params:
  dashboard_id: { in: path, type: string, required: true, description: UUID from list_dashboards. }
  pin: { in: body, type: string, description: Optional PIN (4-32 chars) to encrypt the embedded data. Without it the file is readable by anyone who has it. }
response_hint: |
  Shape: {success, data: {download_url, expires_in_sec, one_time, encrypted}}.
  Give download_url to the user immediately - it is single-use and expires in
  10 minutes. Remind them of the PIN separately if one was set.
```

(Executor: format lại đúng style block ```yaml như các tool hiện có trong từng file.)

- [ ] **Step 2: Commit**

```bash
git add mcp-bridge/tools.md mcp-bridge/tools.example.md
git commit -m "feat: MCP tools - share_dashboard/list/revoke + export_dashboard"
```

---

### Task 9: E2e + final review + merge

**Files:** không tạo file mới (script chạy tay).

- [ ] **Step 1: Dựng dev stack** (Postgres + API theo cách dev hiện có; migration 0008 tự chạy qua MigrationRunner).

- [ ] **Step 2: E2e flow bằng curl** (PAT = token thật của user dev; cần 1 dashboard có ≥1 widget):

```bash
BASE=http://localhost:5847
PAT=edm_pat_...
DASH=<dashboard-uuid>

# 1. Tạo share
curl -s -X POST $BASE/api/dashboards/$DASH/shares -H "X-API-Key: $PAT" -H "Content-Type: application/json" -d '{"expires_in_days":7}'
# → lấy share_url (token), pin, share_id. Kỳ vọng: success, pin 6 số.

TOKEN=shr_...
# 2. Trang share sống
curl -s -o /dev/null -w "%{http_code}" $BASE/share/$TOKEN          # → 200
curl -s -o /dev/null -w "%{http_code}" $BASE/share/shr_deadbeef    # → 404

# 3. Sai PIN 5 lần → lần 6 bị 429
for i in 1 2 3 4 5; do curl -s -o /dev/null -w "%{http_code} " -X POST $BASE/api/share/$TOKEN/session -H "Content-Type: application/json" -d '{"pin":"000000"}'; done
# → 401 x5
curl -s -X POST $BASE/api/share/$TOKEN/session -H "Content-Type: application/json" -d '{"pin":"000000"}'
# → 429 SHARE_LOCKED

# 4. (đợi hết khóa hoặc reset locked_until trong DB) đúng PIN → cookie
curl -s -c /tmp/share.jar -X POST $BASE/api/share/$TOKEN/session -H "Content-Type: application/json" -d '{"pin":"<PIN>"}' -o /dev/null -w "%{http_code}"   # → 204

# 5. Dashboard view KHÔNG chứa sql
curl -s -b /tmp/share.jar $BASE/api/share/$TOKEN/dashboard | grep -c '"sql"'   # → 0
WID=<widget-uuid>
curl -s -b /tmp/share.jar $BASE/api/share/$TOKEN/widgets/$WID/data             # → success + rows

# 6. Không cookie → 404
curl -s -o /dev/null -w "%{http_code}" $BASE/api/share/$TOKEN/dashboard        # → 404

# 7. Revoke thắng cookie
SHARE_ID=<share_id>
curl -s -X DELETE $BASE/api/shares/$SHARE_ID -H "X-API-Key: $PAT"              # → success
curl -s -b /tmp/share.jar -o /dev/null -w "%{http_code}" $BASE/api/share/$TOKEN/dashboard  # → 404

# 8. Export không PIN + có PIN
curl -s -X POST $BASE/api/dashboards/$DASH/export -H "X-API-Key: $PAT" -H "Content-Type: application/json" -d '{}'
# → download_url; curl download_url → file HTML chứa "Snapshot lúc"; curl lần 2 → 404 (one-time)
curl -s -X POST $BASE/api/dashboards/$DASH/export -H "X-API-Key: $PAT" -H "Content-Type: application/json" -d '{"pin":"1234"}'
# → tải file, mở browser, nhập 1234 → hiện chart; PIN sai → báo lỗi (data không đọc được trong source)
```

- [ ] **Step 3: Chạy toàn suite lần cuối** — `dotnet test tests/ExcelDatasetManager.Tests --nologo -v q` → 0 Failed.

- [ ] **Step 4: Final review** — rà theo Hardening checklist trong spec (10 mục), đối chiếu từng mục với code; đặc biệt: 404 chung chung mọi nhánh viewer, response viewer không `sql`, revoke thắng cookie, token/PIN không xuất hiện trong log.

- [ ] **Step 5: Merge + push**

```bash
git checkout main
git merge --no-ff <branch> -m "Merge dashboard share: link+PIN viewer, share admin, static export"
git push origin main
```

## Definition of Done

1. Migration 0008 chạy sạch trên DB dev (MigrationRunner).
2. Toàn suite unit xanh; các test mới: ShareCrypto (token/PIN/lockout/clamp), ShareSessionProtector (roundtrip/tamper/wrong-key), ExportCrypto (roundtrip/wrong-PIN/tag).
3. E2e Task 9 pass đủ 8 bước, đúng expected code từng bước.
4. Viewer surface = đúng 3 route; không route viewer nhận SQL; response viewer không chứa `sql`; 404 chung chung mọi nhánh token hỏng.
5. tools.md + tools.example.md có 4 tool mới, đúng format bridge.
6. UI: tạo share hiện link+PIN đúng 1 lần; revoke hoạt động; trang share render chart.
