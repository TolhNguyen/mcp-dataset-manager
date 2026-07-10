# Realtime Custom-HTML Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dashboard `kind='custom'`: Claude dựng trang HTML đẹp, host trên EDM server, render trong iframe sandbox, data live bơm qua postMessage từ shell tin cậy; owner xem được SQL từng endpoint.

**Architecture:** Mở rộng `dashboards` hiện có (cột `kind` + bảng `dashboard_pages`); endpoint = `dashboard_widgets` không đổi. HTML của AI được serve qua route riêng mang header `Content-Security-Policy: sandbox allow-scripts` (tự sandbox kể cả khi mở trực tiếp); shell (dashboards.html owner / share.html viewer) fetch data qua các route có sẵn rồi `postMessage` vào iframe.

**Tech Stack:** ASP.NET Core minimal API + Dapper + Npgsql (api/), xUnit unit tests KHÔNG chạm DB (tests/), vanilla JS không bundler (wwwroot/), MCP bridge định nghĩa tool bằng YAML block trong file .md.

**Spec:** `docs/superpowers/specs/2026-07-10-realtime-dashboard-design.md`

## Global Constraints

- Unit tests KHÔNG được mở kết nối DB — chỉ test class thuần (pure) như `DashboardGuardTests` hiện có.
- Widget SQL bị ĐÓNG BĂNG và validate 2 lần (giữ nguyên pipeline hiện có — plan này không chạm vào validate/execute).
- Share viewer KHÔNG BAO GIỜ nhận SQL trong bất kỳ response nào.
- HTML của AI KHÔNG BAO GIỜ được chạy cùng origin không-sandbox: mọi route serve nó PHẢI đặt `DashboardPageHeaders.Apply` (CSP `sandbox allow-scripts`, KHÔNG có `allow-same-origin`).
- KHÔNG đặt thuộc tính `sandbox` trên thẻ `<iframe>` ở shell — sandbox qua attribute tạo opaque origin ngay từ request nên cookie phiên (JWT cookie `edm_token` / share session cookie) sẽ không được gửi kèm; sandbox phải đến từ CSP header của response.
- Cap HTML: 2MB (2_097_152 bytes UTF-8), hằng số trong `DashboardPageGuard`, không config.
- UI copy tiếng Việt, JS theo phong cách hiện có (`escapeHtml`, `$()` helpers, global namespace object, không module).
- Trang HTML tĩnh wwwroot load script qua `<script src>` có query `?v=YYYYMMDD-slug` — khi sửa JS phải bump version string.
- Commit message: `feat:`/`fix:`/`docs:` + `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` (dòng cuối).
- Chạy test: `dotnet test tests/ExcelDatasetManager.Tests` từ repo root (Windows, PowerShell hoặc bash đều được).

---

### Task 1: Migration 0009 + cột `kind` xuyên suốt model/service

**Files:**
- Create: `api/Migrations/0009_dashboard_pages.sql`
- Modify: `api/Models/Dashboard.cs`
- Modify: `api/Services/DashboardGuard.cs`
- Modify: `api/Services/DashboardService.cs` (SelectDashboardSql, CreateOrEnsureDashboardAsync, BuildDashboardDto)
- Test: `tests/ExcelDatasetManager.Tests/DashboardGuardTests.cs` (thêm test consts)

**Interfaces:**
- Produces: `Dashboard` record có thêm `string Kind`; `DashboardGuard.KindGrid = "grid"`, `DashboardGuard.KindCustom = "custom"`; `DashboardService.EnsureDashboardByNameAsync(..., string kind, ...)`; mọi dashboard DTO (`BuildDashboardDto`) có field `kind`. Task 3 dùng `EnsureDashboardCoreAsync` (private helper mới trả `(Dashboard?, ApiResult<object>?)`).

- [ ] **Step 1: Viết migration**

`api/Migrations/0009_dashboard_pages.sql`:

```sql
ALTER TABLE dashboards ADD COLUMN IF NOT EXISTS kind VARCHAR(10) NOT NULL DEFAULT 'grid';

CREATE TABLE IF NOT EXISTS dashboard_pages (
    dashboard_id UUID PRIMARY KEY REFERENCES dashboards(id) ON DELETE CASCADE,
    html TEXT NOT NULL,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

Kiểm tra migration được nhúng: mở `api/ExcelDatasetManager.Api.csproj`, tìm `EmbeddedResource` cho `Migrations`. Nếu pattern là glob (`Migrations\**\*.sql` hoặc tương tự) thì không cần sửa; nếu liệt kê từng file thì thêm 0009. `MigrationRunner` tự phát hiện theo tên `NNNN_*.sql`.

- [ ] **Step 2: Viết test cho consts mới (fail trước)**

Thêm vào `tests/ExcelDatasetManager.Tests/DashboardGuardTests.cs`:

```csharp
[Fact]
public void Kind_constants_are_grid_and_custom()
{
    Assert.Equal("grid", DashboardGuard.KindGrid);
    Assert.Equal("custom", DashboardGuard.KindCustom);
}
```

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter DashboardGuardTests`
Expected: FAIL — `KindGrid` không tồn tại (lỗi compile là dạng fail chấp nhận được ở bước này).

- [ ] **Step 3: Thêm consts + model Kind**

`api/Services/DashboardGuard.cs` — thêm ngay dưới `ChartTypes`:

```csharp
    public const string KindGrid = "grid";
    public const string KindCustom = "custom";
```

`api/Models/Dashboard.cs` — thêm `Kind` vào record (Dapper map theo tên):

```csharp
public record Dashboard(
    Guid Id, Guid UserId, string Name, string? Description, string Kind, string CreatedBy,
    DateTime CreatedAt, DateTime UpdatedAt);
```

- [ ] **Step 4: Luồn `kind` qua DashboardService**

Trong `api/Services/DashboardService.cs`:

1. `SelectDashboardSql` thêm cột:

```csharp
    private const string SelectDashboardSql = """
        SELECT id AS Id, user_id AS UserId, name AS Name, description AS Description,
               kind AS Kind, created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM dashboards
        """;
```

2. Tách core ra khỏi wrapper DTO — thay `CreateOrEnsureDashboardAsync` hiện tại bằng 2 tầng (giữ nguyên logic advisory lock / cap / insert, chỉ thêm `kind` và đổi kiểu trả về của core):

```csharp
    public Task<ApiResult<object>> CreateDashboardAsync(
        Guid userId, string? name, string? description, string source, string createdBy, CancellationToken ct)
        => WrapEnsure(EnsureDashboardCoreAsync(userId, name, description, source, createdBy,
            allowExisting: false, DashboardGuard.KindGrid, ct));

    public Task<ApiResult<object>> EnsureDashboardByNameAsync(
        Guid userId, string? name, string source, string createdBy, CancellationToken ct)
        => WrapEnsure(EnsureDashboardCoreAsync(userId, name, null, source, createdBy,
            allowExisting: true, DashboardGuard.KindGrid, ct));

    private static async Task<ApiResult<object>> WrapEnsure(Task<(Dashboard? Dashboard, ApiResult<object>? Error)> task)
    {
        var (dashboard, error) = await task;
        return error ?? ApiResult<object>.Ok(BuildDashboardDto(dashboard!));
    }

    /// <summary>Core ensure/create trả record thật (không phải DTO) để các caller nội bộ —
    /// SetPageByNameAsync (Task 3) — dùng tiếp dashboard.Id/Kind mà không phải đọc dynamic.</summary>
    private async Task<(Dashboard? Dashboard, ApiResult<object>? Error)> EnsureDashboardCoreAsync(
        Guid userId, string? name, string? description, string source, string createdBy,
        bool allowExisting, string kind, CancellationToken ct)
```

Trong thân core (logic cũ giữ nguyên, các điểm đổi):

- Mỗi đường `return ApiResult<object>.Fail(...)` cũ đổi thành `return (null, ApiResult<object>.Fail(...));`
- Nhánh `allowExisting` khi tìm thấy dashboard cùng tên — thêm kiểm tra kind TRƯỚC khi commit:

```csharp
            if (existing is not null)
            {
                if (!string.Equals(existing.Kind, kind, StringComparison.Ordinal))
                {
                    await tx.RollbackAsync(ct);
                    return (null, ApiResult<object>.Fail(
                        ErrorCodes.DashboardKindMismatch,
                        $"Dashboard '{existing.Name}' đã tồn tại với kind '{existing.Kind}' — không thể dùng làm dashboard '{kind}'. Chọn tên khác.",
                        new { dashboard_id = existing.Id, kind = existing.Kind }));
                }
                await tx.CommitAsync(ct);
                return (existing, null);
            }
```

- INSERT thêm cột kind:

```csharp
        await conn.ExecuteAsync("""
            INSERT INTO dashboards (id, user_id, name, description, kind, created_by)
            VALUES (@Id, @UserId, @Name, @Description, @Kind, @CreatedBy)
            """, new
        {
            Id = dashboardId, UserId = userId, Name = trimmedName, Description = description,
            Kind = kind, CreatedBy = createdBy
        }, tx);
```

- Cuối core: `return (created, null);`

3. `BuildDashboardDto` thêm `kind = d.Kind` (đặt sau `description`).

4. `ErrorCodes` (`api/Models/Errors.cs`) — thêm vào nhóm `// Dashboard`:

```csharp
    public const string DashboardKindMismatch = "DASHBOARD_KIND_MISMATCH";
```

- [ ] **Step 5: Build + test pass**

Run: `dotnet build api && dotnet test tests/ExcelDatasetManager.Tests`
Expected: build sạch, toàn bộ test PASS (kể cả test mới Step 2).

- [ ] **Step 6: Commit**

```bash
git add api/Migrations/0009_dashboard_pages.sql api/Models/Dashboard.cs api/Models/Errors.cs api/Services/DashboardGuard.cs api/Services/DashboardService.cs tests/ExcelDatasetManager.Tests/DashboardGuardTests.cs
git commit -m "feat: dashboards.kind (grid|custom) + dashboard_pages table (migration 0009)"
```

---

### Task 2: DashboardPageGuard — validate HTML thuần (TDD)

**Files:**
- Create: `api/Services/DashboardPageGuard.cs`
- Test: `tests/ExcelDatasetManager.Tests/DashboardPageGuardTests.cs`

**Interfaces:**
- Produces: `DashboardPageGuard.MaxHtmlBytes` (int, 2_097_152); `DashboardPageGuard.ValidateHtml(string? html)` → `string?` (null = hợp lệ, ngược lại là message lỗi). Task 3 gọi hàm này đầu tiên trong SetPageByNameAsync.

- [ ] **Step 1: Viết test fail**

`tests/ExcelDatasetManager.Tests/DashboardPageGuardTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardPageGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateHtml_rejects_missing(string? html)
        => Assert.Equal("html is required.", DashboardPageGuard.ValidateHtml(html));

    [Fact]
    public void ValidateHtml_accepts_small_page()
        => Assert.Null(DashboardPageGuard.ValidateHtml("<h1>ok</h1><script>parent.postMessage({type:'edm:ready'},'*')</script>"));

    [Fact]
    public void ValidateHtml_accepts_exactly_at_cap()
        => Assert.Null(DashboardPageGuard.ValidateHtml(new string('a', DashboardPageGuard.MaxHtmlBytes)));

    [Fact]
    public void ValidateHtml_rejects_over_cap()
        => Assert.NotNull(DashboardPageGuard.ValidateHtml(new string('a', DashboardPageGuard.MaxHtmlBytes + 1)));

    [Fact]
    public void ValidateHtml_counts_utf8_bytes_not_chars()
    {
        // 'ă' = 2 bytes UTF-8: nửa cap + 1 ký tự 2-byte là vượt cap dù char count < cap.
        var html = new string('ă', DashboardPageGuard.MaxHtmlBytes / 2) + "x";
        Assert.NotNull(DashboardPageGuard.ValidateHtml(html));
    }
}
```

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter DashboardPageGuardTests`
Expected: FAIL (compile error — class chưa tồn tại).

- [ ] **Step 2: Implement**

`api/Services/DashboardPageGuard.cs`:

```csharp
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure validation cho trang HTML custom của dashboard (kind='custom'). Không sanitize nội dung —
/// ranh giới an ninh là CSP sandbox ở route serve (DashboardPageHeaders), không phải regex ở đây.
/// Chỉ enforce: bắt buộc có nội dung + trần dung lượng theo BYTES UTF-8 (khớp cách Postgres đo TEXT).
/// </summary>
public static class DashboardPageGuard
{
    public const int MaxHtmlBytes = 2 * 1024 * 1024;

    public static string? ValidateHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "html is required.";
        }

        if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
        {
            return $"html must be at most {MaxHtmlBytes} bytes UTF-8 (2MB).";
        }

        return null;
    }
}
```

- [ ] **Step 3: Test pass**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter DashboardPageGuardTests`
Expected: 6 PASS.

- [ ] **Step 4: Commit**

```bash
git add api/Services/DashboardPageGuard.cs tests/ExcelDatasetManager.Tests/DashboardPageGuardTests.cs
git commit -m "feat: DashboardPageGuard - 2MB UTF-8 cap cho trang HTML custom"
```

---

### Task 3: DashboardService — SetPageByNameAsync / GetPageHtmlAsync / share view có kind

**Files:**
- Modify: `api/Services/DashboardService.cs`

**Interfaces:**
- Consumes: `EnsureDashboardCoreAsync` (Task 1), `DashboardPageGuard` (Task 2).
- Produces:
  - `Task<ApiResult<object>> SetPageByNameAsync(Guid userId, string? dashboardName, string? html, string source, string createdBy, CancellationToken ct)` — DTO: `{dashboard_id, name, kind, view_url, endpoints: [{widget_id, title}], html_bytes, updated_at}`.
  - `Task<string?> GetPageHtmlAsync(Guid userId, Guid dashboardId, CancellationToken ct)` — html hoặc null (không có page / không phải chủ).
  - `GetShareViewAsync` DTO thêm `kind` + `has_page` (bool).

- [ ] **Step 1: SetPageByNameAsync + GetPageHtmlAsync**

Thêm vào `DashboardService` (sau vùng Widgets, trước "Widget data execution"), một vùng mới:

```csharp
    // ============================================================
    // Custom page (kind='custom'): trang HTML do AI dựng, serve qua route CSP-sandbox
    // ============================================================

    /// <summary>
    /// Upsert trang HTML cho dashboard kind='custom', addressing theo tên (convention giống
    /// EnsureDashboardByNameAsync để agent không cần lookup trước). Dashboard cùng tên nhưng
    /// kind='grid' bị từ chối (DASHBOARD_KIND_MISMATCH) — không bao giờ tự đổi kind.
    /// KHÔNG sanitize html: ranh giới an ninh là CSP sandbox lúc serve, không phải lúc lưu.
    /// </summary>
    public async Task<ApiResult<object>> SetPageByNameAsync(
        Guid userId, string? dashboardName, string? html, string source, string createdBy, CancellationToken ct)
    {
        var htmlError = DashboardPageGuard.ValidateHtml(html);
        if (htmlError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, htmlError);
        }

        var (dashboard, error) = await EnsureDashboardCoreAsync(
            userId, dashboardName, null, source, createdBy, allowExisting: true, DashboardGuard.KindCustom, ct);
        if (error is not null) return error;

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO dashboard_pages (dashboard_id, html, created_by)
            VALUES (@DashboardId, @Html, @CreatedBy)
            ON CONFLICT (dashboard_id) DO UPDATE
            SET html = EXCLUDED.html, created_by = EXCLUDED.created_by, updated_at = NOW()
            """, new { DashboardId = dashboard!.Id, Html = html, CreatedBy = createdBy });

        var endpoints = (await conn.QueryAsync<(Guid WidgetId, string Title)>("""
            SELECT id AS WidgetId, title AS Title
            FROM dashboard_widgets
            WHERE dashboard_id = @DashboardId AND archived_at IS NULL
            ORDER BY position, created_at
            """, new { DashboardId = dashboard.Id })).ToList();

        var updatedAt = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT updated_at FROM dashboard_pages WHERE dashboard_id = @Id", new { Id = dashboard.Id });

        // Oauth:PublicUrl là issuer công khai (production: https) — dùng làm base cho view link
        // trả về agent, vì agent/user mở link ngoài origin của API nội bộ.
        var baseUrl = (configuration["Oauth:PublicUrl"] ?? "").TrimEnd('/');

        return ApiResult<object>.Ok(new
        {
            dashboard_id = dashboard.Id,
            name = dashboard.Name,
            kind = dashboard.Kind,
            view_url = $"{baseUrl}/dashboards.html?id={dashboard.Id}",
            endpoints = endpoints.Select(e => new { widget_id = e.WidgetId, title = e.Title }).ToList(),
            html_bytes = System.Text.Encoding.UTF8.GetByteCount(html!),
            updated_at = updatedAt
        });
    }

    /// <summary>
    /// HTML trang custom, scoped theo ownership (join dashboards.user_id). Trả null khi không có
    /// page hoặc dashboard không thuộc user — caller trả 404, không phân biệt 2 trường hợp
    /// (tránh existence oracle). Share route gọi với share.UserId (chủ share) nên cùng path này.
    /// </summary>
    public async Task<string?> GetPageHtmlAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>("""
            SELECT p.html
            FROM dashboard_pages p
            JOIN dashboards d ON d.id = p.dashboard_id
            WHERE d.id = @DashboardId AND d.user_id = @UserId
            """, new { DashboardId = dashboardId, UserId = userId });
    }
```

- [ ] **Step 2: GetShareViewAsync thêm kind + has_page**

Trong `GetShareViewAsync`, thay đoạn lấy `name` bằng lấy cả record + thêm `has_page`, và DTO trả về:

```csharp
        var dashboard = await conn.QuerySingleOrDefaultAsync<Dashboard>(
            SelectDashboardSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });
        if (dashboard is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var hasPage = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboard_pages WHERE dashboard_id = @Id",
            new { Id = dashboardId }) > 0;
```

DTO cuối (widget list giữ nguyên `ShareWidgetRow` — vẫn KHÔNG có sql):

```csharp
        return ApiResult<object>.Ok(new
        {
            dashboard_name = dashboard.Name,
            kind = dashboard.Kind,
            has_page = hasPage,
            widgets = widgets.Select(w => new
            {
                widget_id = w.WidgetId,
                title = w.Title,
                chart_type = w.ChartType,
                chart_config = ParseChartConfig(w.ChartConfigJson),
                position = w.Position
            })
        });
```

- [ ] **Step 3: Build + toàn bộ test pass**

Run: `dotnet build api && dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS. (Các method này chạm DB nên không có unit test mới — theo convention của repo, hành vi được kiểm ở e2e tay sau khi có UI.)

- [ ] **Step 4: Commit**

```bash
git add api/Services/DashboardService.cs
git commit -m "feat: SetPageByNameAsync/GetPageHtmlAsync + share view mang kind/has_page"
```

---

### Task 4: HTTP routes serve page + CSP sandbox headers (TDD phần header)

**Files:**
- Create: `api/Endpoints/DashboardPageHeaders.cs`
- Modify: `api/Endpoints/DashboardEndpoints.cs`
- Modify: `api/Endpoints/ShareEndpoints.cs`
- Modify: `api/Models/Dashboard.cs` (request record mới)
- Test: `tests/ExcelDatasetManager.Tests/DashboardPageHeadersTests.cs`

**Interfaces:**
- Consumes: `SetPageByNameAsync`, `GetPageHtmlAsync` (Task 3).
- Produces:
  - `PUT /api/dashboards/page` body `{dashboard_name, html}` — policy `KnowledgeWrite` (JWT + user-PAT; dataset-scoped key đã bị xoá khỏi hệ thống nên policy này chính là "owner").
  - `GET /api/dashboards/{id:guid}/page/raw` — `JwtOnly` (iframe owner xác thực bằng cookie `edm_token`, được JwtBearer OnMessageReceived đọc sẵn).
  - `GET /api/share/{token}/page` — share-session gate như 3 route share hiện có.
  - `DashboardPageHeaders.Csp` const + `Apply(HttpContext)`.

- [ ] **Step 1: Test const CSP (fail trước)**

`tests/ExcelDatasetManager.Tests/DashboardPageHeadersTests.cs`:

```csharp
using ExcelDatasetManager.Api.Endpoints;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardPageHeadersTests
{
    [Fact]
    public void Csp_sandboxes_without_same_origin()
    {
        Assert.StartsWith("sandbox allow-scripts;", DashboardPageHeaders.Csp);
        Assert.DoesNotContain("allow-same-origin", DashboardPageHeaders.Csp);
    }

    [Fact]
    public void Csp_blocks_network_but_allows_inline_and_cdnjs()
    {
        Assert.Contains("default-src 'none'", DashboardPageHeaders.Csp);
        Assert.Contains("connect-src 'none'", DashboardPageHeaders.Csp);
        Assert.Contains("script-src 'unsafe-inline' https://cdnjs.cloudflare.com", DashboardPageHeaders.Csp);
    }
}
```

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter DashboardPageHeadersTests`
Expected: FAIL (compile — class chưa có).

- [ ] **Step 2: DashboardPageHeaders**

`api/Endpoints/DashboardPageHeaders.cs`:

```csharp
namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// Headers cho MỌI response serve HTML do AI dựng (owner raw route + share page route).
/// CSP `sandbox allow-scripts` (KHÔNG allow-same-origin) sandbox chính DOCUMENT được trả về —
/// tức là kể cả khi user mở URL này top-level (không qua iframe), trang vẫn chạy trong opaque
/// origin: không cookie, không localStorage, không gọi được API cùng origin. Vì sandbox nằm ở
/// response chứ không ở thẻ iframe, shell KHÔNG đặt sandbox attribute (đặt sẽ chặn cookie phiên
/// ngay từ request nạp iframe — xem js/page-embed.js).
/// script-src cho phép inline + cdnjs.cloudflare.com (thói quen artifact của Claude: Chart.js…);
/// connect-src 'none' chặn mọi fetch/XHR/WebSocket từ bên trong trang — data chỉ vào được qua
/// postMessage từ shell.
/// </summary>
public static class DashboardPageHeaders
{
    public const string Csp =
        "sandbox allow-scripts; default-src 'none'; " +
        "script-src 'unsafe-inline' https://cdnjs.cloudflare.com; " +
        "style-src 'unsafe-inline' https://cdnjs.cloudflare.com; " +
        "img-src data: blob:; font-src data: https://cdnjs.cloudflare.com; " +
        "connect-src 'none'";

    public static void Apply(HttpContext ctx)
    {
        ctx.Response.Headers["Content-Security-Policy"] = Csp;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Cache-Control"] = "no-store";
    }
}
```

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter DashboardPageHeadersTests`
Expected: PASS.

- [ ] **Step 3: Request record + routes owner**

`api/Models/Dashboard.cs` — thêm cuối file:

```csharp
// MCP convenience: upsert trang HTML custom, addressing dashboard theo tên (tự tạo kind='custom').
public record SetPageByNameRequest(string? DashboardName, string? Html);
```

`api/Endpoints/DashboardEndpoints.cs` — thêm sau route `POST /api/dashboards/widgets` (cuối MapDashboardEndpoints):

```csharp
        // ============================================================
        // Custom page (kind='custom')
        // ============================================================

        // MCP: upsert trang HTML theo dashboard_name (tự tạo dashboard kind='custom' nếu chưa có).
        // KnowledgeWrite = JWT + user-PAT — chính là "owner"; không còn dataset-scoped key.
        app.MapPut("/api/dashboards/page", async (
            SetPageByNameRequest req,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await dashboardService.SetPageByNameAsync(
                userId.Value, req.DashboardName, req.Html, source, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        // Iframe src cho shell owner (dashboards.html). Xác thực bằng JWT cookie edm_token —
        // JwtBearer OnMessageReceived đã đọc cookie khi không có Authorization header, và
        // same-origin subframe navigation gửi kèm cookie SameSite=Lax. Response tự sandbox
        // qua DashboardPageHeaders (xem doc ở đó) nên mở trực tiếp URL này cũng vô hại.
        app.MapGet("/api/dashboards/{id:guid}/page/raw", async (
            Guid id, HttpContext ctx,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var html = await dashboardService.GetPageHtmlAsync(userId.Value, id, ct);
            if (html is null) return Results.NotFound();

            DashboardPageHeaders.Apply(ctx);
            return Results.Text(html, "text/html", System.Text.Encoding.UTF8);
        })
        .RequireAuthorization("JwtOnly");
```

`MapWriteResult` — thêm 1 case (trên nhánh `_`):

```csharp
            ErrorCodes.DashboardKindMismatch => Results.BadRequest(new { success = false, error = result.Error }),
```

- [ ] **Step 4: Share page route**

`api/Endpoints/ShareEndpoints.cs` — thêm sau route `/api/share/{token}/widgets/{widgetId:guid}/data`:

```csharp
        // Trang HTML custom (kind='custom') cho share viewer — cùng session gate như 3 route trên.
        // KHÔNG dùng ApplyShareHeaders: response này là document AI dựng, cần CSP sandbox riêng
        // (DashboardPageHeaders) chứ không phải CSP của shell.
        app.MapGet("/api/share/{token}/page", async (
            string token, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector, DashboardService dashboards,
            CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null || !HasValidSession(ctx, protector, share.Id)) return Results.NotFound();

            var html = await dashboards.GetPageHtmlAsync(share.UserId, share.DashboardId, ct);
            if (html is null) return Results.NotFound();

            DashboardPageHeaders.Apply(ctx);
            return Results.Text(html, "text/html", System.Text.Encoding.UTF8);
        });
```

Sửa doc comment đầu class `ShareEndpoints`: "The ONLY three routes" → "The ONLY four routes" + thêm dòng: `/page` serve HTML custom dưới CSP sandbox (không SQL, không cookie chạm được từ trong trang).

- [ ] **Step 5: Build + test + commit**

Run: `dotnet build api && dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS.

```bash
git add api/Endpoints/DashboardPageHeaders.cs api/Endpoints/DashboardEndpoints.cs api/Endpoints/ShareEndpoints.cs api/Models/Dashboard.cs tests/ExcelDatasetManager.Tests/DashboardPageHeadersTests.cs
git commit -m "feat: routes serve trang HTML custom duoi CSP sandbox (owner raw + share page + PUT by-name)"
```

---

### Task 5: js/page-embed.js — shell pump (iframe + postMessage)

**Files:**
- Create: `api/wwwroot/js/page-embed.js`
- Modify: `api/wwwroot/css/style.css` (class `.page-embed-frame`)

**Interfaces:**
- Produces: global `EdmPageEmbed.mount(opts) -> { destroy() }` với `opts = { container, iframeSrc, widgets: [{widget_id, title, refresh_interval_sec?}], fetchWidgetData(widgetId) -> Promise<{columns, rows}>, onWarning(message)? }`. Task 6 (owner) và Task 7 (share) gọi.
- Message protocol (khớp spec): iframe→shell `{type:'edm:ready'}`; shell→iframe `{type:'edm:data', endpoints:[{id, title, columns, rows, error?}]}`.

- [ ] **Step 1: Viết page-embed.js**

`api/wwwroot/js/page-embed.js`:

```js
// api/wwwroot/js/page-embed.js — shell pump cho dashboard kind='custom'.
// Dùng bởi cả dashboards.js (owner, iframeSrc=/api/dashboards/{id}/page/raw, fetch qua Api.get)
// và share.js (viewer, iframeSrc=/api/share/{token}/page, fetch qua route share no-SQL).
//
// An ninh: HTML của AI được response tự sandbox qua header CSP `sandbox allow-scripts`
// (DashboardPageHeaders.cs). KHÔNG đặt sandbox attribute trên thẻ iframe ở đây — attribute tạo
// opaque origin ngay từ request nạp iframe nên cookie phiên (edm_token / edm_share_*) sẽ không
// được gửi và route trả 401/404. Sandbox đến từ response là đủ: trang bị opaque origin sau khi
// nạp, không đọc được cookie/localStorage, connect-src 'none' chặn mọi fetch từ bên trong.
// Data chỉ vào trang qua postMessage một chiều từ shell này.

const EdmPageEmbed = {
    mount(opts) {
        const { container, iframeSrc, widgets, fetchWidgetData, onWarning } = opts;

        const entries = new Map(); // widget_id -> {id, title, columns, rows, error?}
        widgets.forEach(w => entries.set(w.widget_id, {
            id: w.widget_id, title: w.title, columns: [], rows: [], error: 'Đang tải…'
        }));

        const iframe = document.createElement('iframe');
        iframe.src = iframeSrc;
        iframe.className = 'page-embed-frame';
        iframe.title = 'Dashboard';
        container.appendChild(iframe);

        let ready = false;
        let disposed = false;
        const intervals = [];

        function post() {
            if (disposed || !iframe.contentWindow) return;
            // targetOrigin '*' là bắt buộc: document trong iframe có opaque origin (CSP sandbox)
            // nên không thể chỉ định origin cụ thể. Payload chỉ chứa data widget mà người xem
            // này vốn được phép thấy qua chính các route data — không có gì nhạy cảm hơn.
            iframe.contentWindow.postMessage(
                { type: 'edm:data', endpoints: Array.from(entries.values()) }, '*');
        }

        async function refreshWidget(w) {
            if (disposed) return;
            try {
                const table = await fetchWidgetData(w.widget_id) || {};
                entries.set(w.widget_id, {
                    id: w.widget_id, title: w.title,
                    columns: table.columns || [], rows: table.rows || []
                });
            } catch (err) {
                entries.set(w.widget_id, {
                    id: w.widget_id, title: w.title, columns: [], rows: [],
                    error: (err && err.message) || 'Không tải được dữ liệu.'
                });
            }
            if (ready) post();
        }

        const onMessage = (e) => {
            if (e.source !== iframe.contentWindow) return;
            if (!e.data || e.data.type !== 'edm:ready') return;
            ready = true;
            post();
        };
        window.addEventListener('message', onMessage);

        widgets.forEach(w => {
            refreshWidget(w);
            const sec = Math.max(30, w.refresh_interval_sec || 60);
            intervals.push(setInterval(() => refreshWidget(w), sec * 1000));
        });

        const readyTimeout = setTimeout(() => {
            if (ready || disposed) return;
            if (typeof onWarning === 'function') {
                onWarning('Trang không gửi edm:ready — HTML có thể chưa đúng contract postMessage. Nhờ Claude kiểm tra lại. Vẫn thử bơm data…');
            }
            ready = true; // trang thiếu handshake nhưng có listener thì vẫn nhận được data
            post();
        }, 5000);

        return {
            destroy() {
                disposed = true;
                window.removeEventListener('message', onMessage);
                intervals.forEach(clearInterval);
                clearTimeout(readyTimeout);
                iframe.remove();
            }
        };
    }
};
```

- [ ] **Step 2: CSS**

Thêm cuối `api/wwwroot/css/style.css`:

```css
/* Dashboard kind='custom': iframe chiếm trọn card, chiều cao lớn cho trang báo cáo. */
.page-embed-frame {
    width: 100%;
    height: 78vh;
    border: 0;
    border-radius: 8px;
    background: #fff;
    display: block;
}
```

- [ ] **Step 3: Kiểm tra syntax nhanh**

Run: `node --check api/wwwroot/js/page-embed.js`
Expected: không lỗi. (Node có sẵn vì mcp-bridge là TypeScript/Node project.)

- [ ] **Step 4: Commit**

```bash
git add api/wwwroot/js/page-embed.js api/wwwroot/css/style.css
git commit -m "feat: EdmPageEmbed - shell pump postMessage cho trang dashboard custom"
```

---

### Task 6: Owner UI — dashboards.html/js: view custom + tab Endpoints (xem SQL, chạy thử)

**Files:**
- Modify: `api/wwwroot/dashboards.html`
- Modify: `api/wwwroot/js/dashboards.js`

**Interfaces:**
- Consumes: `EdmPageEmbed` (Task 5); `GET /api/dashboards/{id}` (nay trả `dashboard.kind`); `GET /api/dashboards/{id}/page/raw`; `GET /api/dashboards/{id}/widgets/{wid}/data`; `EdmChartRender.renderTable`.
- Lưu ý: đọc kỹ `dashboards.js` hiện tại trước khi sửa — cấu trúc là object `DashboardsPage` với `selectDashboard(id)` (dòng ~161), `renderDashboardList()` (~77), `clearAllTimers()` (~304). Làm theo đúng phong cách đó.

- [ ] **Step 1: dashboards.html — khối custom + script**

Trong `dashboards.html`, bên trong `#detailCard` (sau phần widget grid hiện có — tìm `id="widgetGrid"` và đặt khối mới NGAY SAU element cha trực tiếp của nó), thêm:

```html
        <div id="customDash" hidden>
            <div class="card-head">
                <div>
                    <button type="button" class="btn-link" id="tabViewBtn">Xem</button>
                    <button type="button" class="btn-link" id="tabEndpointsBtn">Endpoints</button>
                </div>
            </div>
            <div id="customWarning" class="status-msg error" hidden></div>
            <div id="customView"></div>
            <div id="customEndpoints" hidden></div>
        </div>
```

Cuối file, TRƯỚC script `dashboards.js`, thêm (và bump version của dashboards.js):

```html
<script src="/js/page-embed.js?v=20260710-custom"></script>
```

```html
<script src="/js/dashboards.js?v=20260710-custom"></script>
```

- [ ] **Step 2: dashboards.js — nhánh kind='custom'**

1. Thêm state vào object `DashboardsPage` (cạnh `pollTimer`/`charts` hiện có):

```js
    pageEmbed: null, // EdmPageEmbed instance của dashboard custom đang mở
```

2. Trong `selectDashboard(id)`, sau khi có `{dashboard, widgets}` (chỗ đang set `detailTitle`), rẽ nhánh:

```js
            if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }

            if (dashboard.kind === 'custom') {
                this.showCustomDashboard(id, dashboard, widgets);
                return;
            }
            $('#customDash').hidden = true;
```

(giữ nguyên phần render widget grid hiện có cho kind grid phía dưới; đảm bảo phần grid — element cha của `#widgetGrid` và nút "Thêm widget" — được show lại khi quay về dashboard grid.)

3. Thêm các method mới vào `DashboardsPage`:

```js
    // ============================================================
    // Dashboard kind='custom': iframe sandbox + tab Endpoints (xem SQL, chạy thử)
    // ============================================================

    showCustomDashboard(id, dashboard, widgets) {
        // Ẩn widget grid + nút thêm widget của dashboard grid
        $('#widgetGrid').innerHTML = '';
        $('#widgetGrid').parentElement.hidden = true;
        this.clearAllTimers();

        const wrap = $('#customDash');
        wrap.hidden = false;
        $('#customWarning').hidden = true;
        $('#customEndpoints').hidden = true;
        $('#customView').hidden = false;
        $('#customView').innerHTML = '';

        $('#tabViewBtn').onclick = () => {
            $('#customEndpoints').hidden = true;
            $('#customView').hidden = false;
        };
        $('#tabEndpointsBtn').onclick = () => {
            $('#customView').hidden = true;
            this.renderEndpointsTab(id, widgets);
            $('#customEndpoints').hidden = false;
        };

        if (widgets.length === 0 || !dashboard) {
            $('#customView').innerHTML = '<p class="muted">Dashboard này chưa có endpoint nào.</p>';
        }

        this.pageEmbed = EdmPageEmbed.mount({
            container: $('#customView'),
            iframeSrc: `/api/dashboards/${id}/page/raw`,
            widgets: widgets,
            fetchWidgetData: (wid) => Api.get(`/api/dashboards/${id}/widgets/${wid}/data`),
            onWarning: (msg) => {
                const box = $('#customWarning');
                box.hidden = false;
                box.textContent = msg;
            }
        });
    },

    renderEndpointsTab(id, widgets) {
        const wrap = $('#customEndpoints');
        if (widgets.length === 0) {
            wrap.innerHTML = '<p class="muted">Chưa có endpoint nào. Nhờ Claude tạo bằng create_dashboard_widget.</p>';
            return;
        }
        // Read-only theo spec: sửa SQL qua Claude/MCP (validate 2 lần) — UI chỉ xem + chạy thử.
        wrap.innerHTML = widgets.map(w => `
            <div class="widget-card" data-widget-id="${w.widget_id}">
                <div class="widget-card-head">
                    <div>
                        <div class="widget-title">${escapeHtml(w.title)}</div>
                        <span class="badge badge-source">dataset: ${escapeHtml(String(w.dataset_id))}</span>
                        <span class="badge badge-source">refresh: ${w.refresh_interval_sec}s</span>
                    </div>
                    <button type="button" class="btn-link" data-action="tryrun" data-id="${w.widget_id}">Chạy thử</button>
                </div>
                <pre class="endpoint-sql">${escapeHtml(w.sql)}</pre>
                <div class="widget-body" id="endpoint-result-${w.widget_id}" hidden></div>
            </div>`).join('');

        wrap.querySelectorAll('button[data-action="tryrun"]').forEach(btn => {
            btn.addEventListener('click', () => this.tryRunEndpoint(id, btn.dataset.id, btn));
        });
    },

    async tryRunEndpoint(dashboardId, widgetId, btn) {
        const body = $(`#endpoint-result-${widgetId}`);
        body.hidden = false;
        body.innerHTML = '<p class="muted">Đang chạy…</p>';
        btn.disabled = true;
        try {
            const table = await Api.get(`/api/dashboards/${dashboardId}/widgets/${widgetId}/data`);
            EdmChartRender.renderTable(body, table.columns || [], table.rows || []);
        } catch (err) {
            body.innerHTML = `<div class="error">${escapeHtml(err.message || 'Chạy thử thất bại.')}</div>`;
        } finally {
            btn.disabled = false;
        }
    },
```

4. `renderDashboardList()` — thêm badge kind vào mỗi item (cạnh tên dashboard):

```js
            const kindBadge = d.kind === 'custom'
                ? '<span class="badge badge-source">🧩 custom</span>'
                : '';
```

(chèn `${kindBadge}` vào template item — đọc template hiện có và đặt sau tên.)

5. Khi đóng/đổi dashboard hoặc rời trang: mọi chỗ gọi `clearAllTimers()` cho dashboard cũ phải kèm `if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }` (đã cover ở selectDashboard; kiểm tra thêm nút đóng detail nếu có).

6. CSS — thêm cuối `api/wwwroot/css/style.css`:

```css
.endpoint-sql {
    background: #f6f8fa;
    border-radius: 6px;
    padding: 10px 12px;
    font-size: 12.5px;
    overflow-x: auto;
    white-space: pre-wrap;
}
```

- [ ] **Step 3: Kiểm tra + commit**

Run: `node --check api/wwwroot/js/dashboards.js`
Expected: không lỗi.

Kiểm tra tay (cần server chạy — nếu môi trường không có DB/docker thì ghi rõ SKIPPED trong báo cáo): login, mở dashboards.html, dashboard grid cũ vẫn hoạt động như trước.

```bash
git add api/wwwroot/dashboards.html api/wwwroot/js/dashboards.js api/wwwroot/css/style.css
git commit -m "feat: owner UI dashboard custom - iframe view + tab Endpoints xem SQL/chay thu"
```

---

### Task 7: Share viewer — share.html/js render kind='custom'

**Files:**
- Modify: `api/wwwroot/share.html`
- Modify: `api/wwwroot/js/share.js`

**Interfaces:**
- Consumes: `EdmPageEmbed` (Task 5); `GET /api/share/{token}/dashboard` (nay trả `kind`, `has_page`); `GET /api/share/{token}/page`; `GET /api/share/{token}/widgets/{id}/data`.
- Share CSP của shell (`ApplyShareHeaders`) có `default-src 'self'` → frame-src same-origin OK, `script-src 'self'` → page-embed.js self-hosted OK.

- [ ] **Step 1: share.html**

Trong `#dash`, sau `<div id="widgetGrid" ...>`, thêm:

```html
        <div id="customWarning" class="status-msg error" hidden></div>
        <div id="customView" hidden></div>
```

Trước `<script src="/js/share.js...">` thêm + bump version share.js:

```html
<script src="/js/page-embed.js?v=20260710-custom"></script>
```

```html
<script src="/js/share.js?v=20260710-custom"></script>
```

- [ ] **Step 2: share.js**

1. Thêm state `pageEmbed: null,` vào `SharePage` (cạnh `charts`).

2. Đầu `showDashboard(data)` — rẽ nhánh custom:

```js
    showDashboard(data) {
        $('#pin-gate').hidden = true;
        const dash = $('#dash');
        dash.hidden = false;
        dash.querySelector('h1').textContent = data.dashboard_name || '';

        if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }

        if (data.kind === 'custom' && data.has_page) {
            this.showCustomDashboard(data);
            return;
        }
        $('#customView').hidden = true;
        $('#widgetGrid').hidden = false;
        // ... phần grid hiện có giữ nguyên từ đây
```

3. Method mới trong `SharePage`:

```js
    // Dashboard kind='custom': iframe sandbox nạp /api/share/{token}/page (cùng session cookie),
    // data bơm qua EdmPageEmbed. Share payload không có refresh_interval_sec (viewer không được
    // biết cấu hình) — refresh cố định 60s/widget ở phía shell.
    showCustomDashboard(data) {
        $('#widgetGrid').hidden = true;
        $('#widgetGrid').innerHTML = '';
        this.clearAllCharts();

        const view = $('#customView');
        view.hidden = false;
        view.innerHTML = '';

        this.pageEmbed = EdmPageEmbed.mount({
            container: view,
            iframeSrc: `/api/share/${token}/page`,
            widgets: (data.widgets || []).map(w => ({ widget_id: w.widget_id, title: w.title })),
            fetchWidgetData: async (wid) => {
                const res = await fetch(`/api/share/${token}/widgets/${wid}/data`);
                if (!res.ok) throw new Error('Không tải được dữ liệu widget.');
                const json = await res.json();
                return json.data || {};
            },
            onWarning: (msg) => {
                const box = $('#customWarning');
                box.hidden = false;
                box.textContent = msg;
            }
        });
    },
```

4. `showGate()` — thêm dọn embed:

```js
        if (this.pageEmbed) { this.pageEmbed.destroy(); this.pageEmbed = null; }
        $('#customView').hidden = true;
```

5. Cập nhật comment đầu file share.js: response shape của `/dashboard` nay có `kind`, `has_page`; route mới `/api/share/{token}/page` (HTML, CSP sandbox).

- [ ] **Step 3: Kiểm tra + commit**

Run: `node --check api/wwwroot/js/share.js`
Expected: không lỗi.

```bash
git add api/wwwroot/share.html api/wwwroot/js/share.js
git commit -m "feat: share viewer render dashboard custom qua iframe sandbox + pump 60s"
```

---

### Task 8: MCP tools + query guide — cây quyết định snapshot/realtime, visual-first

**Files:**
- Modify: `mcp-bridge/tools.example.md`
- Modify: `api/Services/QueryGuideService.cs` (DefaultGuide)

**Interfaces:**
- Consumes: `PUT /api/dashboards/page` (Task 4).
- Lưu ý vận hành: file tools THẬT trên production được deploy riêng (tools.example.md chỉ là mẫu trong repo) và storage có thể override query guide bằng `storage/query-guide.md` — Task 9 ghi chú việc sync này vào docs.

- [ ] **Step 1: Tool mới `set_dashboard_html` trong tools.example.md**

Thêm section mới NGAY SAU `## update_dashboard_widget` (trước `## share_dashboard`):

````markdown
## set_dashboard_html

```yaml
type: tool
name: set_dashboard_html
description: |
  Đặt/thay trang HTML tuỳ chỉnh cho một dashboard REALTIME (kind='custom').
  Dùng khi người dùng muốn dashboard xem lại lúc nào cũng ra data mới, với
  giao diện đẹp tự do như artifact.

  QUY TRÌNH BẮT BUỘC khi người dùng yêu cầu "tạo dashboard/báo cáo":
  1. Xác định loại: SNAPSHOT (xem một lần, data đóng băng → dựng artifact
     ngay trong chat, KHÔNG dùng tool này) hay REALTIME (mở lại thấy data
     mới → dùng tool này). Không rõ thì hỏi người dùng MỘT câu rồi mới làm.
  2. REALTIME: tạo từng endpoint bằng create_dashboard_widget (mỗi endpoint
     = 1 câu SQL, cần schema_token), cùng dashboard_name.
  3. Dựng trang HTML hoàn chỉnh (visual-first: ưu tiên chart/KPI tile hơn
     bảng số liệu; chất lượng thiết kế như artifact) rồi gọi tool này.
  4. Gửi view_url trong response cho người dùng.

  CONTRACT của trang HTML (bắt buộc — trang chạy trong CSP sandbox, KHÔNG
  fetch được gì; data do server bơm vào qua postMessage):
  - Không dùng fetch/XHR/WebSocket. Thư viện ngoài chỉ được load từ
    https://cdnjs.cloudflare.com (ví dụ Chart.js).
  - Phải có skeleton này:
      window.addEventListener('message', (e) => {
        if (!e.data || e.data.type !== 'edm:data') return;
        // e.data.endpoints = [{id, title, columns:[{name,type}], rows:[[..]], error?}]
        render(e.data.endpoints);   // hàm bạn tự viết — vẽ lại TOÀN BỘ trang
      });
      parent.postMessage({ type: 'edm:ready' }, '*');
  - endpoints khớp với các widget đã tạo (id = widget_id). Entry có field
    `error` thì hiện trạng thái lỗi cho chart đó, các chart khác vẫn vẽ.
  - render() phải idempotent: được gọi lại mỗi lần data refresh (server tự
    bơm lại theo refresh_interval_sec của từng endpoint).
connection: edm
method: PUT
path: /api/dashboards/page
params:
  dashboard_name:
    in: body
    type: string
    required: true
    description: Tên dashboard (tự tạo kind='custom' nếu chưa tồn tại). Phải trùng dashboard_name đã dùng ở create_dashboard_widget.
  html:
    in: body
    type: string
    required: true
    description: Trang HTML hoàn chỉnh (<!DOCTYPE html>...), tối đa 2MB, theo đúng CONTRACT trong description.
response_hint: |
  Shape: {success, data: {dashboard_id, name, kind, view_url, endpoints:
  [{widget_id, title}], html_bytes, updated_at}}. GỬI view_url cho người
  dùng — họ mở link đó (cần đăng nhập; muốn gửi người ngoài thì gọi
  share_dashboard). Đối chiếu endpoints với các widget bạn đã tạo — thiếu
  cái nào thì tạo bổ sung bằng create_dashboard_widget rồi không cần gọi
  lại tool này (trang nhận data theo widget_id lúc runtime).
  error.code=DASHBOARD_KIND_MISMATCH nghĩa là tên này đã là dashboard grid
  thường — chọn dashboard_name khác.
  error.code=VALIDATION_ERROR: html thiếu hoặc quá 2MB.
```
````

- [ ] **Step 2: Cập nhật 2 tool cũ trong tools.example.md**

`create_dashboard_widget` — thay description hiện tại bằng:

```yaml
description: |
  Tạo một endpoint dữ liệu (widget) cho dashboard. Với dashboard REALTIME
  kind='custom' (xem set_dashboard_html), mỗi endpoint là một câu SQL đóng
  băng mà trang HTML nhận data qua postMessage. Với dashboard grid thường,
  widget hiển thị trực tiếp trên web app. SQL phải là SELECT/WITH read-only
  trên đúng dataset. Nếu dashboard_name chưa tồn tại, server tự tạo dashboard
  grid mới với tên đó — muốn dashboard custom thì gọi set_dashboard_html
  (trước hoặc sau đều được, nhưng tên phải nhất quán).
```

`get_dashboard` — thay description + response_hint:

```yaml
description: |
  Fetch one dashboard's metadata (including kind: 'grid' | 'custom') plus all
  of its active widgets/endpoints (title, sql, chart_type, chart_config,
  refresh_interval_sec, position). Use this to show the user what's already
  on a dashboard, to review/edit an endpoint's SQL when the user asks, or to
  find a widget_id before calling update_dashboard_widget.
```

```yaml
response_hint: |
  Shape: {success, data: {dashboard: {dashboard_id, name, kind, ...},
  widgets: [...]}}. Each widget has widget_id, dashboard_id, dataset_id,
  title, sql, chart_type, chart_config, refresh_interval_sec, position,
  source. Với dashboard kind='custom', widgets chính là các endpoint của
  trang HTML — sql ở đây là chỗ xem/sửa query khi người dùng yêu cầu.
  error.code=DASHBOARD_NOT_FOUND means the id doesn't belong to this user.
```

- [ ] **Step 3: Query guide — cây quyết định dashboard**

Trong `api/Services/QueryGuideService.cs`, thêm section vào `DefaultGuide` (sau "## Writing business knowledge", trước dấu đóng chuỗi):

```
        ## Dashboards & reports (visual-first)
        When the user asks for a "dashboard" or "report", FIRST decide (ask ONE question if unclear):
        - SNAPSHOT (one-off, frozen data): query the data, then build an HTML artifact in chat with
          the data embedded. Do NOT call dashboard tools.
        - REALTIME (data must be fresh every time it is opened): create one endpoint per query with
          create_dashboard_widget (same dashboard_name), then build the page and call
          set_dashboard_html (its description contains the REQUIRED postMessage contract), and give
          the user the returned view_url.
        Always visual-first for both kinds: prefer charts and KPI tiles over raw tables, with
        artifact-quality layout. Only the dashboard owner (or Claude via their PAT) can edit
        endpoint SQL; share viewers can never see SQL.
```

- [ ] **Step 4: Build + test (QueryGuideServiceTests tự tính token theo content nên phải re-run)**

Run: `dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS. Nếu QueryGuideServiceTests fail vì hardcode content/token cũ → cập nhật test theo giá trị mới (token là SHA-256 12 hex đầu của content — tính lại, không hardcode tay).

- [ ] **Step 5: Commit**

```bash
git add mcp-bridge/tools.example.md api/Services/QueryGuideService.cs
git commit -m "feat: MCP set_dashboard_html + cay quyet dinh snapshot/realtime trong query guide"
```

---

### Task 9: Docs + verify tổng

**Files:**
- Modify: `docs/ARCHITECTURE.md` (section Dashboards)
- Modify: `docs/API.md` (routes mới)
- Modify: `README.md` (nếu README có mục dashboard — đọc rồi thêm 1-2 dòng cùng giọng văn)

- [ ] **Step 1: ARCHITECTURE.md**

Trong section `## Dashboards (widget SQL đóng băng)` thêm:

```markdown
### Custom page (kind='custom') — dashboard realtime HTML tự do

- `dashboards.kind` (`grid` mặc định | `custom`) + `dashboard_pages` (1 trang HTML/dashboard, cap 2MB — `DashboardPageGuard`). Migration `0009`.
- HTML do AI dựng KHÔNG BAO GIỜ chạy same-origin: mọi route serve nó (`GET /api/dashboards/{id}/page/raw` owner-cookie, `GET /api/share/{token}/page` share-session) đặt `Content-Security-Policy: sandbox allow-scripts` + `connect-src 'none'` (`DashboardPageHeaders`) — document tự sandbox thành opaque origin kể cả khi mở URL trực tiếp. Shell (`dashboards.html`/`share.html`) KHÔNG đặt sandbox attribute trên iframe (attribute chặn cookie phiên ngay từ request nạp iframe).
- Data bơm một chiều qua postMessage (`js/page-embed.js`): iframe→shell `edm:ready`, shell→iframe `edm:data {endpoints:[{id,title,columns,rows,error?}]}`; owner refresh theo `refresh_interval_sec` từng widget, share viewer cố định 60s. Trong iframe không fetch được gì (connect-src 'none'); thư viện chart chỉ từ cdnjs.cloudflare.com.
- `PUT /api/dashboards/page` (KnowledgeWrite = JWT/PAT owner) upsert theo `dashboard_name`, tự tạo dashboard `kind='custom'`; tên đã là grid → `DASHBOARD_KIND_MISMATCH`, không tự đổi kind. Endpoint = `dashboard_widgets` không đổi (validate 2 lần, cache, row cap như cũ). Share viewer vẫn không bao giờ thấy SQL.
- MCP: tool `set_dashboard_html` (contract postMessage nằm trong description), query guide có cây quyết định SNAPSHOT (artifact trong chat) / REALTIME (endpoint + trang HTML) + visual-first.
- Vận hành: tools.md production và `storage/query-guide.md` (nếu có override) phải sync thủ công khi deploy bản này.
```

- [ ] **Step 2: API.md**

Đọc format hiện có của `docs/API.md` rồi thêm 3 route mới (PUT page, GET page/raw, GET share page) cùng auth + response shape, theo đúng format các route dashboard hiện có.

- [ ] **Step 3: Verify tổng**

```bash
dotnet build api && dotnet test tests/ExcelDatasetManager.Tests
node --check api/wwwroot/js/page-embed.js
node --check api/wwwroot/js/dashboards.js
node --check api/wwwroot/js/share.js
```
Expected: build sạch, toàn bộ test PASS, JS không lỗi syntax.

E2E tay (nếu môi trường có docker/DB — nếu không, ghi SKIPPED và lý do trong báo cáo cuối):
1. Chạy server, login, tạo dataset nhỏ.
2. Giả lập MCP bằng curl/PowerShell với PAT: tạo 2 widget (`POST /api/dashboards/widgets`, cần schema_token qua get_context) rồi `PUT /api/dashboards/page` với HTML mẫu đúng contract (copy skeleton từ tool description).
3. Mở `dashboards.html?id=…` → tab Xem hiện trang, data đổ vào; tab Endpoints thấy SQL + Chạy thử OK.
4. Tạo share (`POST /api/dashboards/{id}/shares` qua UI), mở `/share/{token}`, nhập PIN → trang custom hiện, KHÔNG có SQL trong bất kỳ response nào (kiểm tab Network).
5. Mở thẳng `/api/dashboards/{id}/page/raw` top-level → trang hiện nhưng console báo sandbox (không cookie/API access).

- [ ] **Step 4: Commit**

```bash
git add docs/ARCHITECTURE.md docs/API.md README.md
git commit -m "docs: dashboard custom kind - kien truc, API routes, van hanh sync tools"
```
