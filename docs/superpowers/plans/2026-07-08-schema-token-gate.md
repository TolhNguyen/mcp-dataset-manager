# Schema-Token Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ép AI client (Claude qua MCP) đọc Query Guide + schema trước khi query EDM, trả lỗi đủ thông tin để tự sửa SQL, và chống bịa số liệu; đồng thời bỏ dataset API key, thay bằng toggle "AI ghi tri thức" trên dataset.

**Architecture:** Chuỗi proof-of-read stateless 2 tầng — `get_query_guide` trả `guide_token`, `get_context` (nộp guide_token) trả `schema_token`, các tool query (nộp schema_token). Enforcement chỉ áp cho request xác thực bằng PAT (`auth_method=user_api_key`); JWT (web UI) miễn. Toàn bộ thay đổi nằm ở API .NET + `mcp-bridge/tools.md`; bridge code không đổi. SQL pre-flight và error-enrichment dùng schema đã lưu trong Postgres (`dataset_tables`/`dataset_columns`), không gọi vào DB nguồn.

**Tech Stack:** ASP.NET Core 8 minimal API, Dapper + Npgsql (Postgres), DuckDB.NET, xUnit (test project `tests/ExcelDatasetManager.Tests`), migration runner đọc embedded `api/Migrations/NNNN_*.sql`.

## Global Constraints

- Ngôn ngữ chỉ thị/error cho AI: **tiếng Anh**. Thông báo lỗi hiện có bằng tiếng Việt cho người dùng giữ nguyên, không dịch.
- Enforcement gate **chỉ** áp dụng khi principal là PAT: `principal.FindFirstValue("auth_method") == "user_api_key"`. JWT (không có claim `auth_method`) luôn được miễn.
- Không lưu state ở bridge; không sửa file trong `mcp-bridge/src/`. Chỉ sửa `mcp-bridge/tools.md` và `mcp-bridge/tools.example.md`.
- Migration mới đánh số **0007**, đặt tại `api/Migrations/0007_schema_token_gate.sql`; runner tự nhặt qua `<EmbeddedResource Include="Migrations\*.sql" />` (đã có trong csproj).
- Token format: guide = `gd_` + 12 hex đầu SHA-256; schema = `st_` + 12 hex đầu SHA-256. Hex viết thường.
- Không cần backward-compat: dataset API key cũ hết hiệu lực ngay khi drop bảng — đã thống nhất trong spec.
- Test project là xUnit; chạy `dotnet test tests/ExcelDatasetManager.Tests`.
- Build kiểm tra: `dotnet build api/ExcelDatasetManager.Api.csproj`.

**Nguồn spec:** `docs/superpowers/specs/2026-07-07-schema-token-gate-design.md`

---

### Task 1: Migration 0007 + DatasetRecord field

**Files:**
- Create: `api/Migrations/0007_schema_token_gate.sql`
- Modify: `api/Models/Contracts.cs:40-58` (thêm field `AiCanWriteKnowledge` vào `DatasetRecord`)
- Modify: `api/Services/DatasetService.cs:16-34` (`SelectDatasetSql` — thêm cột `ai_can_write_knowledge`)

**Interfaces:**
- Produces: cột `datasets.schema_hash TEXT NULL`, `datasets.ai_can_write_knowledge BOOLEAN NOT NULL DEFAULT TRUE`; bảng `dataset_api_keys` bị DROP. `DatasetRecord.AiCanWriteKnowledge : bool` (cuối danh sách positional).

- [ ] **Step 1: Viết migration**

Tạo `api/Migrations/0007_schema_token_gate.sql`:

```sql
-- Schema-token gate: schema fingerprint + AI knowledge-write toggle; remove dataset-scoped API keys.
ALTER TABLE datasets ADD COLUMN IF NOT EXISTS schema_hash TEXT NULL;
ALTER TABLE datasets ADD COLUMN IF NOT EXISTS ai_can_write_knowledge BOOLEAN NOT NULL DEFAULT TRUE;

DROP TABLE IF EXISTS dataset_api_keys;
```

- [ ] **Step 2: Thêm field vào DatasetRecord**

Trong `api/Models/Contracts.cs`, sửa record `DatasetRecord` — thêm dòng cuối trước `)`:

```csharp
public record DatasetRecord(
    Guid Id,
    Guid UserId,
    string Name,
    string OriginalFileName,
    string FileType,
    string StoredFileName,
    long FileSizeBytes,
    string? ManifestFileName,
    string Status,
    int TableCount,
    long TotalRows,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? ProcessedAt,
    string SourceKind,
    Guid? ConnectionId,
    string? Alias,
    bool AiCanWriteKnowledge
);
```

- [ ] **Step 3: Cập nhật SelectDatasetSql**

Trong `api/Services/DatasetService.cs`, thêm cột vào `SelectDatasetSql` (ngay sau `alias AS Alias`):

```csharp
    public const string SelectDatasetSql = """
        SELECT id AS Id,
               user_id AS UserId,
               name AS Name,
               original_file_name AS OriginalFileName,
               file_type AS FileType,
               stored_file_name AS StoredFileName,
               file_size_bytes AS FileSizeBytes,
               manifest_file_name AS ManifestFileName,
               status AS Status,
               table_count AS TableCount,
               total_rows AS TotalRows,
               error_message AS ErrorMessage,
               created_at AS CreatedAt,
               processed_at AS ProcessedAt,
               source_kind AS SourceKind,
               connection_id AS ConnectionId,
               alias AS Alias,
               ai_can_write_knowledge AS AiCanWriteKnowledge
        FROM datasets
        """;
```

- [ ] **Step 4: Build để phát hiện mọi chỗ khởi tạo DatasetRecord bị thiếu field**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj`
Expected: có thể FAIL nếu có nơi `new DatasetRecord(...)` bằng positional — sửa các nơi đó bằng cách thêm giá trị cuối (thường không có; DatasetRecord chủ yếu do Dapper map). Nếu FAIL ở test helper `TestData.cs`, thêm `AiCanWriteKnowledge: true`. Sửa tới khi build PASS.

- [ ] **Step 5: Commit**

```bash
git add api/Migrations/0007_schema_token_gate.sql api/Models/Contracts.cs api/Services/DatasetService.cs
git commit -m "feat: migration 0007 — schema_hash + ai_can_write_knowledge, drop dataset_api_keys"
```

---

### Task 2: Bỏ dataset API key, thêm toggle ghi tri thức

**Files:**
- Delete: `api/Services/DatasetApiKeyService.cs`, `api/wwwroot/js/api-keys.js`
- Modify: `api/Endpoints/ApiKeyEndpoints.cs` (bỏ 3 route dataset api-key, giữ user PAT routes)
- Modify: `api/Auth/ApiKeyAuthenticationHandler.cs` (bỏ nhánh dataset key)
- Modify: `api/Auth/ClaimsPrincipalExtensions.cs` (thêm `IsApiKeyPrincipal`; đơn giản hoá `CanWriteKnowledge`)
- Modify: `api/Program.cs:228` (bỏ `AddScoped<DatasetApiKeyService>`), `api/Program.cs:159-166` (đơn giản hoá policy KnowledgeWrite)
- Modify: `api/Models/Contracts.cs:27` (bỏ `CanWrite` khỏi `CreateDatasetApiKeyRequest` — đổi thành record chỉ `Name`)
- Modify: `api/Endpoints/DatasetEndpoints.cs` (thêm route PATCH toggle)
- Modify: `api/Endpoints/KnowledgeEndpoints.cs` (chèn check toggle ở 2 write path: POST knowledge, POST documents, PUT — xem Step 6)
- Modify: `api/wwwroot/dataset-detail.html` (thay block "API key cho Claude / MCP" bằng toggle)
- Modify: `api/Models/Errors.cs` (thêm `KnowledgeWriteDisabled`)

**Interfaces:**
- Consumes: `DatasetRecord.AiCanWriteKnowledge` (Task 1).
- Produces: `ClaimsPrincipalExtensions.IsApiKeyPrincipal(this ClaimsPrincipal) : bool`; endpoint `PATCH /api/datasets/{id}/settings` body `{ "ai_can_write_knowledge": bool }`; error code `KNOWLEDGE_WRITE_DISABLED`.

- [ ] **Step 1: Xoá service + JS, thêm error code + helper**

Xoá file `api/Services/DatasetApiKeyService.cs` và `api/wwwroot/js/api-keys.js`.

Trong `api/Models/Errors.cs`, thêm dưới nhóm Knowledge (sau `KnowledgeLimitReached`):

```csharp
    public const string KnowledgeWriteDisabled = "KNOWLEDGE_WRITE_DISABLED";
```

Trong `api/Auth/ClaimsPrincipalExtensions.cs`, thay `CanWriteKnowledge` và thêm helper:

```csharp
    /// <summary>True nếu principal xác thực bằng API key (PAT). JWT session không có claim này.</summary>
    public static bool IsApiKeyPrincipal(this ClaimsPrincipal principal)
        => principal.FindFirstValue(AuthMethodClaim) is not null;
```

Xoá method `CanWriteKnowledge` (không còn dataset key nên claim `can_write` không tồn tại; quyền ghi giờ theo toggle per-dataset, kiểm ở endpoint). Xoá luôn hằng `CanWriteClaim` và `DatasetIdClaim` nếu không còn tham chiếu nào khác (build sẽ báo — xem Step 8).

- [ ] **Step 2: Gỡ nhánh dataset key trong auth handler**

Trong `api/Auth/ApiKeyAuthenticationHandler.cs`, sau khi xác thực PAT (`if (rawKey.StartsWith(UserKeyPrefix...))`) thì mọi key không phải PAT là không hợp lệ. Thay toàn bộ đoạn từ sau block PAT (dòng ~90 `var datasetRow = ...`) tới trước `AuthSuccess` helper bằng:

```csharp
        return AuthenticateResult.Fail("API key is invalid or revoked.");
    }
```

Đồng thời: bỏ record `DatasetKeyRow`, method `GenerateDatasetKey`/`GenerateKey`/`KeyPrefix` nếu không dùng nơi khác (build báo). Đổi guard đầu hàm: key phải bắt đầu bằng `UserKeyPrefix` (`edm_pat_`) mới hợp lệ — thay `DatasetKeyPrefix` bằng `UserKeyPrefix` trong check format:

```csharp
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith(UserKeyPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Invalid API key format.");
        }
```

- [ ] **Step 3: Gỡ route + DI + request record**

Trong `api/Endpoints/ApiKeyEndpoints.cs`, xoá 3 route `datasets.MapPost/MapGet/MapDelete(".../{datasetId:guid}/api-keys...")` và biến `var datasets = ...`; giữ nguyên nhóm `userKeys` (PAT). Thay `CreateDatasetApiKeyRequest` trong 2 handler user-key: đổi sang record mới ở dưới.

Trong `api/Models/Contracts.cs`, đổi:

```csharp
public record CreateUserApiKeyRequest(string Name);
```
(và cập nhật `UserApiKeyService.CreateAsync` + 2 handler userKeys dùng `CreateUserApiKeyRequest`). Xoá `CreateDatasetApiKeyRequest`.

Trong `api/Program.cs`: xoá dòng `builder.Services.AddScoped<DatasetApiKeyService>();`.

- [ ] **Step 4: Đơn giản hoá policy KnowledgeWrite**

Trong `api/Program.cs`, policy `"KnowledgeWrite"` — bỏ dòng `policy.RequireAssertion(ctx => ctx.User.CanWriteKnowledge());` (quyền ghi giờ kiểm theo toggle ở endpoint). Còn lại:

```csharp
    options.AddPolicy("KnowledgeWrite", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ApiKeyAuthenticationOptions.SchemeName);
        policy.RequireAuthenticatedUser();
    });
```

- [ ] **Step 5: Thêm endpoint PATCH toggle**

Trong `api/Endpoints/DatasetEndpoints.cs`, thêm route (trong nhóm `datasets` đã có, hoặc map trực tiếp) — dùng `JwtOnly` vì đây là quản trị dataset:

```csharp
        app.MapPatch("/api/datasets/{datasetId:guid}/settings", async (
            Guid datasetId, UpdateDatasetSettingsRequest req,
            ClaimsPrincipal principal, DatasetService datasetService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (req.AiCanWriteKnowledge is null)
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = "ai_can_write_knowledge is required." } });

            var ok = await datasetService.SetAiCanWriteKnowledgeAsync(userId.Value, datasetId, req.AiCanWriteKnowledge.Value, ct);
            return ok
                ? Results.Ok(new { success = true, data = new { ai_can_write_knowledge = req.AiCanWriteKnowledge.Value } })
                : Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
        }).RequireAuthorization("JwtOnly");
```

Thêm vào `api/Models/Contracts.cs`:

```csharp
public record UpdateDatasetSettingsRequest(bool? AiCanWriteKnowledge);
```

Thêm method vào `api/Services/DatasetService.cs`:

```csharp
    public async Task<bool> SetAiCanWriteKnowledgeAsync(Guid userId, Guid datasetId, bool value, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(
            "UPDATE datasets SET ai_can_write_knowledge = @Value WHERE id = @Id AND user_id = @UserId",
            new { Value = value, Id = datasetId, UserId = userId });
        return affected > 0;
    }
```

- [ ] **Step 6: Chặn ghi tri thức khi toggle tắt (chỉ với PAT)**

Trong `api/Endpoints/KnowledgeEndpoints.cs`, ở 3 write path (POST `/knowledge`, POST `/knowledge/documents`, PUT `/knowledge/{entryId}`), ngay sau khi `dataset` được load và không null, chèn:

```csharp
            if (principal.IsApiKeyPrincipal() && !dataset.AiCanWriteKnowledge)
            {
                return Results.Json(new { success = false, error = new {
                    code = ErrorCodes.KnowledgeWriteDisabled,
                    message = "The dataset owner disabled AI knowledge writes for this dataset.",
                    assistant_instruction = "Report this to the user verbatim. Do not retry."
                }}, statusCode: 403);
            }
```

(DELETE/hard-delete không cần chặn — AI không xoá; nhưng nếu muốn nhất quán, áp cùng check ở DELETE. YAGNI: bỏ qua DELETE.)

- [ ] **Step 7: Thay UI**

Trong `api/wwwroot/dataset-detail.html`: xoá `<script src="js/api-keys.js">` và toàn bộ block card "API key cho Claude / MCP" (chứa input tên key + checkbox can_write + bảng key). Thay bằng:

```html
        <div class="card">
          <h2>Quyền AI ghi tri thức</h2>
          <p class="muted">Khi bật, Claude/MCP được phép lưu và cập nhật ghi chú nghiệp vụ (knowledge) cho dataset này. Tắt cho dữ liệu nhạy cảm.</p>
          <label class="switch-row">
            <input type="checkbox" id="aiCanWriteToggle" />
            <span>Cho phép AI ghi tri thức</span>
          </label>
          <span id="aiCanWriteStatus" class="status-msg" hidden></span>
        </div>
```

Thêm script inline (hoặc file mới `js/dataset-settings.js`) khởi tạo trạng thái toggle từ dataset detail (field `ai_can_write_knowledge` — đảm bảo `GetDetailAsync`/payload trả field này; nếu chưa, thêm vào payload detail) và PATCH khi đổi:

```javascript
(function () {
  const toggle = document.getElementById('aiCanWriteToggle');
  if (!toggle) return;
  const datasetId = new URLSearchParams(location.search).get('id');
  const status = document.getElementById('aiCanWriteStatus');

  // Api.get trả detail; field ai_can_write_knowledge nằm trong data.
  Api.get(`/api/datasets/${datasetId}`).then(d => {
    toggle.checked = d?.data?.ai_can_write_knowledge ?? true;
  });

  toggle.addEventListener('change', async () => {
    status.hidden = false; status.textContent = 'Đang lưu…'; status.className = 'status-msg';
    try {
      await Api.patch(`/api/datasets/${datasetId}/settings`, { ai_can_write_knowledge: toggle.checked });
      status.textContent = 'Đã lưu.'; status.className = 'status-msg success';
    } catch {
      toggle.checked = !toggle.checked;
      status.textContent = 'Lưu thất bại.'; status.className = 'status-msg error';
    }
  });
})();
```

Nếu `Api.patch` chưa tồn tại trong `js/` helper, thêm nó (theo mẫu `Api.post`). Nếu `GetDetailAsync` payload chưa có `ai_can_write_knowledge`, thêm field đó vào object trả về trong `DatasetService.GetDetailAsync`.

- [ ] **Step 8: Build, sửa mọi tham chiếu còn sót**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj`
Expected: FAIL ở các chỗ còn tham chiếu `DatasetApiKeyService`, `CanWriteKnowledge`, `GetScopedDatasetId`, `CreateDatasetApiKeyRequest`, `DatasetIdClaim`, `CanWriteClaim`. Gỡ/sửa từng chỗ:
- `GetScopedDatasetId()` guard trong `QueryEndpoints.cs`, `ContextEndpoints.cs`, `KnowledgeEndpoints.cs`: bỏ các block `if (scopedDatasetId is not null ...) Forbid()` và biến `scopedDatasetId` (không còn dataset-scoped key). Giữ nguyên phần còn lại.
- Sửa tới khi build PASS.

- [ ] **Step 9: Cập nhật test auth handler**

`tests/ExcelDatasetManager.Tests/ApiKeyAuthenticationHandlerTests.cs` có thể test dataset key — xoá/điều chỉnh các test dùng dataset key, giữ test PAT. Run: `dotnet test tests/ExcelDatasetManager.Tests` → PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: remove dataset API keys, add per-dataset AI knowledge-write toggle"
```

---

### Task 3: SchemaTokenService

**Files:**
- Create: `api/Services/SchemaTokenService.cs`
- Test: `tests/ExcelDatasetManager.Tests/SchemaTokenServiceTests.cs`

**Interfaces:**
- Produces:
  - `SchemaTokenService.Compute(IEnumerable<(string TableName, IReadOnlyList<(string Name, string Type)> Columns)> tables) : string` → `st_` + 12 hex.
  - `SchemaTokenService.Matches(string? provided, string expected) : bool` (so khớp ordinal, trim).

- [ ] **Step 1: Viết test**

Tạo `tests/ExcelDatasetManager.Tests/SchemaTokenServiceTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SchemaTokenServiceTests
{
    private static (string, IReadOnlyList<(string, string)>) T(string name, params (string, string)[] cols)
        => (name, cols);

    [Fact]
    public void Compute_is_stable_for_same_schema()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        Assert.Equal(a, b);
        Assert.StartsWith("st_", a);
        Assert.Equal(15, a.Length); // "st_" + 12 hex
    }

    [Fact]
    public void Compute_is_order_independent_across_tables()
    {
        var a = SchemaTokenService.Compute(new[] { T("a", ("x", "INT")), T("b", ("y", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("b", ("y", "INT")), T("a", ("x", "INT")) });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_changes_when_column_added()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_changes_when_type_changes()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "BIGINT")) });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Matches_true_only_for_equal_token()
    {
        var t = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        Assert.True(SchemaTokenService.Matches(t, t));
        Assert.True(SchemaTokenService.Matches("  " + t + " ", t));
        Assert.False(SchemaTokenService.Matches("st_000000000000", t));
        Assert.False(SchemaTokenService.Matches(null, t));
        Assert.False(SchemaTokenService.Matches("", t));
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~SchemaTokenServiceTests`
Expected: FAIL (`SchemaTokenService` not defined).

- [ ] **Step 3: Viết implementation**

Tạo `api/Services/SchemaTokenService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Tính "schema token" — dấu vân tay ổn định của cấu trúc bảng/cột một dataset. AI phải đọc
/// schema qua get_context (nhận token) rồi nộp lại khi query; schema đổi ⇒ token đổi ⇒ token cũ
/// vô hiệu, buộc AI đọc lại. Canonical: bảng sort theo tên; mỗi bảng nối tên + (cột:kiểu) theo
/// thứ tự nhận vào (ordinal_position khi gọi từ DB).
/// </summary>
public static class SchemaTokenService
{
    public const string Prefix = "st_";

    public static string Compute(IEnumerable<(string TableName, IReadOnlyList<(string Name, string Type)> Columns)> tables)
    {
        var sb = new StringBuilder();
        foreach (var (tableName, columns) in tables.OrderBy(t => t.TableName, StringComparer.Ordinal))
        {
            sb.Append(tableName).Append('{');
            foreach (var (name, type) in columns)
            {
                sb.Append(name).Append(':').Append(type ?? "UNKNOWN").Append(',');
            }
            sb.Append("}|");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Prefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static bool Matches(string? provided, string expected)
        => !string.IsNullOrWhiteSpace(provided)
           && string.Equals(provided.Trim(), expected, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~SchemaTokenServiceTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add api/Services/SchemaTokenService.cs tests/ExcelDatasetManager.Tests/SchemaTokenServiceTests.cs
git commit -m "feat: SchemaTokenService — stable schema fingerprint"
```

---

### Task 4: DialectNotes

**Files:**
- Create: `api/Services/DialectNotes.cs`
- Test: `tests/ExcelDatasetManager.Tests/DialectNotesTests.cs`

**Interfaces:**
- Produces:
  - `DialectNotes.MapDialect(string sourceKind, string? provider) : string` — trả `tsql|bigquery|postgresql|mysql|duckdb`.
  - `DialectNotes.For(string dialect) : IReadOnlyList<string>` — 3–5 dòng quy tắc; dialect lạ → mảng rỗng.

- [ ] **Step 1: Viết test**

Tạo `tests/ExcelDatasetManager.Tests/DialectNotesTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DialectNotesTests
{
    [Theory]
    [InlineData("external_db", "mssql", "tsql")]
    [InlineData("external_db", "bigquery", "bigquery")]
    [InlineData("external_db", "postgresql", "postgresql")]
    [InlineData("external_db", "mysql", "mysql")]
    [InlineData("file", null, "duckdb")]
    public void MapDialect_maps_provider(string kind, string? provider, string expected)
        => Assert.Equal(expected, DialectNotes.MapDialect(kind, provider));

    [Theory]
    [InlineData("tsql")]
    [InlineData("bigquery")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("duckdb")]
    public void For_known_dialect_returns_notes(string dialect)
        => Assert.NotEmpty(DialectNotes.For(dialect));

    [Fact]
    public void For_unknown_dialect_is_empty()
        => Assert.Empty(DialectNotes.For("oracle"));

    [Fact]
    public void Tsql_notes_mention_bracket_rule()
        => Assert.Contains(DialectNotes.For("tsql"), n => n.Contains("[dbo.table"));
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~DialectNotesTests`
Expected: FAIL.

- [ ] **Step 3: Viết implementation**

Tạo `api/Services/DialectNotes.cs`:

```csharp
namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Quy tắc cú pháp SQL ngắn cho từng dialect, trả kèm get_context (`dialect_notes`). Nội dung đúc
/// từ các lỗi thực tế: MSSQL `[dbo.table]` (bracket sai), BigQuery gửi CTE thiếu SELECT cuối + dùng
/// `@param`. Giữ tiếng Anh để model tuân thủ tốt.
/// </summary>
public static class DialectNotes
{
    public static string MapDialect(string sourceKind, string? provider)
    {
        var isExternal = string.Equals(sourceKind, "external_db", StringComparison.OrdinalIgnoreCase);
        if (!isExternal) return "duckdb";
        return (provider ?? "").ToLowerInvariant() switch
        {
            "mssql" => "tsql",
            "bigquery" => "bigquery",
            "postgresql" => "postgresql",
            "mysql" => "mysql",
            _ => provider ?? "unknown"
        };
    }

    public static IReadOnlyList<string> For(string dialect) => dialect switch
    {
        "tsql" =>
        [
            "Reference tables as dbo.table_name or [dbo].[table_name] — NEVER [dbo.table_name] (that is one identifier and will not resolve).",
            "No query parameters (@name). Inline literal values, e.g. WHERE date = '2026-06-01'.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses SELECT TOP (n) or OFFSET/FETCH, not LIMIT."
        ],
        "bigquery" =>
        [
            "Use GoogleSQL (standard SQL). Qualify tables as `project.dataset.table` in backticks.",
            "No query parameters (@name). Inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT — do not stop after the CTEs.",
            "Row limiting uses LIMIT n."
        ],
        "postgresql" =>
        [
            "Use standard PostgreSQL. Quote identifiers with double quotes only when needed.",
            "No bound parameters ($1 / :name); inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        "mysql" =>
        [
            "Use MySQL syntax. Quote identifiers with backticks when needed.",
            "No bound parameters (?/:name); inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        "duckdb" =>
        [
            "Use DuckDB SQL. Reference each table by its name (a view) from get_context.",
            "Use the normalized column names from get_context, not the original headers.",
            "Send ONE complete SELECT/WITH statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        _ => Array.Empty<string>()
    };
}
```

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~DialectNotesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add api/Services/DialectNotes.cs tests/ExcelDatasetManager.Tests/DialectNotesTests.cs
git commit -m "feat: DialectNotes — per-dialect SQL syntax rules"
```

---

### Task 5: get_context trả schema_token + dialect_notes

**Files:**
- Modify: `api/Services/ContextShaper.cs` (thêm `SchemaToken`, `DialectNotes` vào input + payload)
- Modify: `api/Services/ContextService.cs` (tính token + notes khi load)
- Test: `tests/ExcelDatasetManager.Tests/ContextShaperTests.cs` (thêm assert token/notes có trong payload)

**Interfaces:**
- Consumes: `SchemaTokenService.Compute` (Task 3), `DialectNotes.For` (Task 4).
- Produces: mỗi dataset trong payload `/api/context` có thêm `schema_token: string`, `dialect_notes: string[]`.

- [ ] **Step 1: Mở rộng ContextDatasetInput**

Trong `api/Services/ContextShaper.cs`, thêm 2 field vào record `ContextDatasetInput` (cuối danh sách):

```csharp
public record ContextDatasetInput(
    Guid DatasetId,
    string Name,
    string? Alias,
    string SourceKind,
    string? Provider,
    string Dialect,
    IReadOnlyList<ContextTableInput> Tables,
    IReadOnlyList<ContextKnowledgeInput> Knowledge,
    int ActiveKnowledgeCount,
    string SchemaToken,
    IReadOnlyList<string> DialectNotes);
```

Trong method `Build`, thêm vào object mỗi dataset (sau `dialect = d.Dialect,`):

```csharp
            dialect = d.Dialect,
            dialect_notes = d.DialectNotes,
            schema_token = d.SchemaToken,
```

- [ ] **Step 2: Tính token + notes trong ContextService.LoadDatasetAsync**

Trong `api/Services/ContextService.cs`, method `LoadDatasetAsync`: sau khi có `tables` (list `ContextTableInput`) và trước `return new ContextDatasetInput(...)`, tính:

```csharp
        var schemaToken = SchemaTokenService.Compute(
            tables.Select(t => (
                t.TableName,
                (IReadOnlyList<(string, string)>)t.Columns.Select(c => (c.Name, c.Type)).ToList())));
        var notes = DialectNotes.For(DialectNotes.MapDialect(dataset.SourceKind, dataset.FileType));
```

Và bổ sung 2 tham số cuối vào `return new ContextDatasetInput(...)`:

```csharp
            ActiveKnowledgeCount: activeCount,
            SchemaToken: schemaToken,
            DialectNotes: notes);
```

Lưu ý: dùng chính danh sách bảng/cột này để token trùng khớp với token mà query endpoint sẽ tính lại (Task 8 tính từ cùng nguồn `dataset_columns`).

- [ ] **Step 3: (tuỳ chọn) Lưu schema_hash vào datasets**

Trong cùng method, sau khi tính `schemaToken`, ghi lazy nếu khác giá trị đã lưu — để query endpoint có thể so nhanh mà không cần load lại toàn bộ cột (Task 8 dùng cách tính lại trực tiếp, nên bước này chỉ để đồng bộ cột `schema_hash`; giữ đơn giản, có thể bỏ nếu Task 8 luôn tính lại). **Quyết định:** bỏ qua ghi `schema_hash` ở đây — Task 8 tính token trực tiếp từ `dataset_columns`, không đọc cột `schema_hash`. Cột `schema_hash` để dành cho tối ưu sau. (Ghi chú lại trong commit.)

- [ ] **Step 4: Cập nhật test ContextShaper**

Trong `tests/ExcelDatasetManager.Tests/ContextShaperTests.cs`, mọi nơi tạo `ContextDatasetInput` thêm 2 tham số cuối `SchemaToken: "st_test000000", DialectNotes: new[] { "note" }`. Thêm 1 test:

```csharp
    [Fact]
    public void Payload_includes_schema_token_and_dialect_notes()
    {
        var ds = /* build a ContextDatasetInput with SchemaToken "st_abc123def456" and DialectNotes ["r1"] */;
        var result = ContextShaper.Shape(new[] { ds }, null, "full", 100000, _ => 10);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Payload);
        Assert.Contains("st_abc123def456", json);
        Assert.Contains("dialect_notes", json);
    }
```
(Điền phần dựng `ds` theo helper sẵn có trong file test — dùng đúng constructor đã cập nhật.)

- [ ] **Step 5: Build + test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~ContextShaperTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add api/Services/ContextShaper.cs api/Services/ContextService.cs tests/ExcelDatasetManager.Tests/ContextShaperTests.cs
git commit -m "feat: get_context returns schema_token + dialect_notes per dataset"
```

---

### Task 6: QueryGuideService + get_query_guide endpoint

**Files:**
- Create: `api/Services/QueryGuideService.cs`
- Create: `api/Endpoints/QueryGuideEndpoints.cs`
- Modify: `api/Program.cs` (đăng ký service singleton + `app.MapQueryGuideEndpoints()`)
- Test: `tests/ExcelDatasetManager.Tests/QueryGuideServiceTests.cs`

**Interfaces:**
- Produces:
  - `QueryGuideService.GetGuide() : (string Token, string Content)` — token `gd_`+12hex của content; đọc `storage/query-guide.md` nếu tồn tại, else embedded default; cache theo mtime.
  - `QueryGuideService.CurrentToken() : string` — token hiện tại (để Task 7 so khớp).
  - Endpoint `GET /api/query-guide` (policy `QueryAccess`) → `{ guide_token, content }`.

- [ ] **Step 1: Viết test**

Tạo `tests/ExcelDatasetManager.Tests/QueryGuideServiceTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryGuideServiceTests
{
    [Fact]
    public void Guide_token_matches_content_and_is_stable()
    {
        var svc = new QueryGuideService(storageDir: null);
        var (token, content) = svc.GetGuide();
        Assert.StartsWith("gd_", token);
        Assert.Equal(15, token.Length);
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains(token, content); // guide ends with its own token line
        Assert.Equal(token, svc.CurrentToken());

        var (token2, _) = svc.GetGuide();
        Assert.Equal(token, token2);
    }

    [Fact]
    public void File_override_changes_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edm-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "query-guide.md"), "# Custom guide\nHello.");
        try
        {
            var svc = new QueryGuideService(storageDir: dir);
            var (token, content) = svc.GetGuide();
            Assert.Contains("Custom guide", content);

            var svcDefault = new QueryGuideService(storageDir: null);
            Assert.NotEqual(svcDefault.CurrentToken(), token);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~QueryGuideServiceTests`
Expected: FAIL.

- [ ] **Step 3: Viết implementation**

Tạo `api/Services/QueryGuideService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Phục vụ "Query Guide" — tài liệu AI phải đọc trước khi dùng EDM. Trả kèm guide_token
/// (gd_ + 12 hex của nội dung); get_context yêu cầu token này (proof-of-read). Nội dung mặc định
/// embedded; nếu có file storage/query-guide.md thì operator override được, token tự đổi theo nội dung.
/// </summary>
public class QueryGuideService
{
    public const string Prefix = "gd_";
    private readonly string? _storageDir;
    private readonly object _lock = new();
    private string? _cachedPath;
    private DateTime _cachedMtimeUtc;
    private string _content = "";
    private string _token = "";

    // storageDir = thư mục chứa query-guide.md (thường ContentRoot/storage). null = luôn dùng default.
    public QueryGuideService(string? storageDir)
    {
        _storageDir = storageDir;
        Reload();
    }

    public (string Token, string Content) GetGuide()
    {
        MaybeReload();
        return (_token, _content);
    }

    public string CurrentToken()
    {
        MaybeReload();
        return _token;
    }

    private void MaybeReload()
    {
        var path = _storageDir is null ? null : Path.Combine(_storageDir, "query-guide.md");
        if (path is not null && File.Exists(path))
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cachedPath == path && mtime == _cachedMtimeUtc) return;
        }
        else if (_cachedPath is null)
        {
            return; // already on embedded default
        }
        Reload();
    }

    private void Reload()
    {
        lock (_lock)
        {
            var path = _storageDir is null ? null : Path.Combine(_storageDir, "query-guide.md");
            if (path is not null && File.Exists(path))
            {
                _content = File.ReadAllText(path);
                _cachedPath = path;
                _cachedMtimeUtc = File.GetLastWriteTimeUtc(path);
            }
            else
            {
                _content = DefaultGuide;
                _cachedPath = null;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_content));
            _token = Prefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();

            // Chèn dòng token ở cuối để AI đọc được và nộp lại (nếu chưa có).
            if (!_content.Contains(_token))
            {
                _content = _content.TrimEnd() +
                    $"\n\n---\nYour guide_token is {_token} — pass it to get_context to prove you have read this guide.\n";
                // token tính trên nội dung gốc; dòng token thêm sau không đổi token.
            }
        }
    }

    private const string DefaultGuide = """
        # EDM Query Guide (read this before querying)

        You are querying datasets in Excel Dataset Manager (EDM) through MCP tools. Follow this
        workflow exactly. Never invent data.

        ## Required workflow every session
        1. Call `get_query_guide` (this) once — it returns a `guide_token`.
        2. Call `get_context` with the dataset_ids AND the `guide_token`. It returns each dataset's
           tables, normalized column names, sample rows, `dialect`, `dialect_notes`, and a
           `schema_token`. Read the schema — never guess table or column names.
        3. Call `query_dataset` / `query_datasets` with your SQL AND the `schema_token` from get_context.

        ## When to do what
        - Read schema (get_context): before your first query on a dataset, and again whenever a query
          returns `SCHEMA_CHANGED` (the schema was updated — read it and use the new schema_token).
        - Read sample rows: they are in get_context. Use them to understand value formats. Do NOT run
          exploratory probe queries column-by-column.
        - Query: only after you have the schema_token. Write ONE complete SELECT/WITH statement.

        ## SQL syntax by dataset type
        Each dataset reports a `dialect` in get_context with `dialect_notes`. Obey them. Highlights:
        - tsql (MSSQL): `dbo.table` or `[dbo].[table]`, never `[dbo.table]`. No `@params`. TOP/OFFSET, not LIMIT.
        - bigquery: backticked `project.dataset.table`. No `@params`. A `WITH` must end with its final SELECT.
        - postgresql / mysql: standard SQL, LIMIT n, no bound params.
        - duckdb (uploaded Excel/CSV): each table is a view by name; use normalized column names.

        ## When a query fails
        - Read `error.details`: it may contain `available_tables`, `suggested_columns`, or `did_you_mean`.
          Fix your SQL and retry — at most twice.
        - If it still fails, report the exact error to the user and STOP.
        - NEVER estimate, interpolate, or fabricate data values. If you cannot get real data, say so.

        ## Writing business knowledge
        - Save durable business facts with `save_dataset_knowledge` when the user teaches you something,
          following `memory_instructions` from get_context. If a dataset has AI knowledge-writing
          disabled you will get `KNOWLEDGE_WRITE_DISABLED` — report it, do not retry.
        """;
}
```

- [ ] **Step 4: Đăng ký service + endpoint**

Tạo `api/Endpoints/QueryGuideEndpoints.cs`:

```csharp
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class QueryGuideEndpoints
{
    public static void MapQueryGuideEndpoints(this WebApplication app)
    {
        app.MapGet("/api/query-guide", (QueryGuideService svc) =>
        {
            var (token, content) = svc.GetGuide();
            return Results.Ok(new { guide_token = token, content });
        }).RequireAuthorization("QueryAccess").RequireRateLimiting("query");
    }
}
```

Trong `api/Program.cs`, đăng ký singleton (đặt cạnh các `AddScoped` service; guide state là process-wide nên singleton, truyền storage dir):

```csharp
builder.Services.AddSingleton(sp =>
    new QueryGuideService(Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "storage")));
```

Và thêm cùng chỗ các `app.Map...Endpoints()`:

```csharp
app.MapQueryGuideEndpoints();
```

- [ ] **Step 5: Build + test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~QueryGuideServiceTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add api/Services/QueryGuideService.cs api/Endpoints/QueryGuideEndpoints.cs api/Program.cs tests/ExcelDatasetManager.Tests/QueryGuideServiceTests.cs
git commit -m "feat: QueryGuideService + get_query_guide endpoint with guide_token"
```

---

### Task 7: Bắt buộc guide_token ở get_context (PAT only)

**Files:**
- Modify: `api/Endpoints/ContextEndpoints.cs` (thêm param `guide_token`, kiểm với PAT)

**Interfaces:**
- Consumes: `QueryGuideService.CurrentToken()` (Task 6), `ClaimsPrincipalExtensions.IsApiKeyPrincipal` (Task 2).
- Produces: `/api/context` với principal PAT thiếu/sai `guide_token` → 400 `GUIDE_REQUIRED` + `assistant_instruction`.

- [ ] **Step 1: Thêm kiểm tra guide_token**

Trong `api/Endpoints/ContextEndpoints.cs`, thêm `QueryGuideService guide` vào tham số handler và `string? guide_token` vào query params. Ngay sau khi `userId` hợp lệ:

```csharp
            if (principal.IsApiKeyPrincipal()
                && !SchemaTokenLikeMatch(guide_token, guide.CurrentToken()))
            {
                return Results.Json(new { success = false, error = new {
                    code = "GUIDE_REQUIRED",
                    message = "Call get_query_guide and READ it first, then pass the guide_token it returns.",
                    assistant_instruction = "Do not guess schema or fabricate data. Call get_query_guide, read it, then retry get_context with the guide_token."
                }}, statusCode: 400);
            }
```

Thêm local helper trong class (so khớp trim/ordinal — dùng lại quy tắc của SchemaTokenService.Matches):

```csharp
    private static bool SchemaTokenLikeMatch(string? provided, string expected)
        => Services.SchemaTokenService.Matches(provided, expected);
```

`guide_token` truyền qua query string (bridge tool sẽ map — Task 12). Đảm bảo signature endpoint nhận `string? guide_token`.

- [ ] **Step 2: Build**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj`
Expected: PASS.

- [ ] **Step 3: Kiểm thử thủ công (ghi lại lệnh để chạy trên server sau deploy)**

Tạo PAT, rồi:

```bash
# thiếu guide_token → GUIDE_REQUIRED
curl -s -H "X-API-Key: edm_pat_XXX" "http://127.0.0.1:5847/api/context?dataset_ids=<id>" | head
# đúng guide_token → trả context
TOKEN=$(curl -s -H "X-API-Key: edm_pat_XXX" http://127.0.0.1:5847/api/query-guide | python -c "import sys,json;print(json.load(sys.stdin)['guide_token'])")
curl -s -H "X-API-Key: edm_pat_XXX" "http://127.0.0.1:5847/api/context?dataset_ids=<id>&guide_token=$TOKEN" | head
```

Expected: lệnh 1 trả `GUIDE_REQUIRED`; lệnh 3 trả payload context có `schema_token`.

- [ ] **Step 4: Commit**

```bash
git add api/Endpoints/ContextEndpoints.cs
git commit -m "feat: require guide_token on get_context for PAT callers"
```

---

### Task 8: Bắt buộc schema_token ở query endpoints (PAT only)

**Files:**
- Create: `api/Services/SchemaTokenGate.cs` (helper load token hiện tại của 1 dataset từ DB + build lỗi)
- Modify: `api/Endpoints/QueryEndpoints.cs` (kiểm trước khi gọi service, cho cả single + multi)
- Modify: `api/Models/Contracts.cs` (thêm `SchemaToken` vào `QueryOptions`; thêm `SchemaTokens` vào `MultiQueryRequest`)
- Modify: `api/Models/Errors.cs` (thêm `ContextRequired`, `SchemaChanged`)
- Test: `tests/ExcelDatasetManager.Tests/SchemaTokenGateTests.cs`

**Interfaces:**
- Consumes: `SchemaTokenService` (Task 3), `DatasetService` (dataSource), `IsApiKeyPrincipal` (Task 2).
- Produces:
  - `SchemaTokenGate.ComputeCurrentAsync(NpgsqlDataSource ds, Guid datasetId, CancellationToken) : Task<string>` — token hiện tại từ `dataset_tables`/`dataset_columns`.
  - `QueryOptions.SchemaToken : string?`; `MultiQueryRequest.SchemaTokens : Dictionary<string,string>?`.
  - Error codes `CONTEXT_REQUIRED`, `SCHEMA_CHANGED`.

- [ ] **Step 1: Thêm error codes + model fields**

`api/Models/Errors.cs`, nhóm Query:

```csharp
    public const string ContextRequired = "CONTEXT_REQUIRED";
    public const string SchemaChanged = "SCHEMA_CHANGED";
```

`api/Models/Contracts.cs`, thêm field cuối `QueryOptions` (sau `BypassAiBudget`):

```csharp
public record QueryOptions(
    int? MaxRows,
    string? ReturnFormat,
    bool? IncludeSql,
    bool? IncludeProfile,
    int? MaxTokens,
    bool? AllowLargeResult,
    string? ConfirmationId,
    string? ResponseMode,
    [property: System.Text.Json.Serialization.JsonIgnore] bool? BypassAiBudget = null,
    string? SchemaToken = null);
```

Và `MultiQueryRequest`:

```csharp
public record MultiQueryRequest(Guid[]? DatasetIds, string Sql, QueryOptions? Options, Dictionary<string, string>? SchemaTokens = null);
```

- [ ] **Step 2: Viết test cho gate helper**

Vì `ComputeCurrentAsync` cần DB, tách phần thuần: test `SchemaTokenService.Compute` đã có (Task 3). Cho gate, viết test kiểm builder lỗi (thuần):

Tạo `tests/ExcelDatasetManager.Tests/SchemaTokenGateTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SchemaTokenGateTests
{
    [Fact]
    public void Missing_token_builds_context_required()
    {
        var err = SchemaTokenGate.BuildGateError(provided: null, expected: "st_abc123def456", datasetId: Guid.NewGuid());
        var json = System.Text.Json.JsonSerializer.Serialize(err);
        Assert.Contains("CONTEXT_REQUIRED", json);
        Assert.Contains("assistant_instruction", json);
    }

    [Fact]
    public void Wrong_token_builds_schema_changed()
    {
        var err = SchemaTokenGate.BuildGateError(provided: "st_000000000000", expected: "st_abc123def456", datasetId: Guid.NewGuid());
        var json = System.Text.Json.JsonSerializer.Serialize(err);
        Assert.Contains("SCHEMA_CHANGED", json);
    }

    [Fact]
    public void Matching_token_returns_null()
    {
        var err = SchemaTokenGate.BuildGateError(provided: "st_abc123def456", expected: "st_abc123def456", datasetId: Guid.NewGuid());
        Assert.Null(err);
    }
}
```

- [ ] **Step 3: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~SchemaTokenGateTests`
Expected: FAIL.

- [ ] **Step 4: Viết gate helper**

Tạo `api/Services/SchemaTokenGate.cs`:

```csharp
using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Cổng schema-token cho các endpoint query: tính token hiện tại của một dataset từ schema đã lưu
/// và, nếu token client nộp thiếu/sai, dựng error envelope CONTEXT_REQUIRED / SCHEMA_CHANGED
/// (kèm assistant_instruction chống bịa). Chỉ áp cho principal PAT — caller tự kiểm IsApiKeyPrincipal.
/// </summary>
public static class SchemaTokenGate
{
    public static async Task<string> ComputeCurrentAsync(NpgsqlDataSource dataSource, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(string TableName, string Name, string? Type, int Ordinal)>("""
            SELECT t.table_name AS TableName, c.normalized_name AS Name,
                   c.inferred_type AS Type, c.ordinal_position AS Ordinal
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId
            ORDER BY t.table_name, c.ordinal_position
            """, new { DatasetId = datasetId })).ToList();

        var tables = rows
            .GroupBy(r => r.TableName)
            .Select(g => (g.Key,
                (IReadOnlyList<(string, string)>)g.Select(r => (r.Name, r.Type ?? "UNKNOWN")).ToList()));

        return SchemaTokenService.Compute(tables);
    }

    /// <summary>Trả null nếu token hợp lệ; ngược lại trả object lỗi để endpoint Results.Json(...).</summary>
    public static object? BuildGateError(string? provided, string expected, Guid datasetId)
    {
        if (SchemaTokenService.Matches(provided, expected)) return null;

        var code = string.IsNullOrWhiteSpace(provided) ? ErrorCodes.ContextRequired : ErrorCodes.SchemaChanged;
        var message = code == ErrorCodes.ContextRequired
            ? "Call get_context for this dataset first, then pass the schema_token it returns. Do NOT guess table or column names."
            : "The dataset schema changed since you last read it. Call get_context again and use the new schema_token.";

        return new
        {
            success = false,
            dataset_id = datasetId,
            status = "failed",
            error = new
            {
                code,
                message,
                assistant_instruction = "Report this to the user only if it recurs. Do not fabricate data. Call get_context, then retry with the schema_token.",
                retryable = true
            }
        };
    }
}
```

Lưu ý cột: token tính từ `(normalized_name, inferred_type)` — **phải khớp** với ContextService (Task 5 dùng `c.Name`=`normalized_name`, `c.Type`=`inferred_type`). Kiểm lại ContextService.LoadDatasetAsync map `inferred_type AS Type` (đúng — dòng SQL `c.inferred_type AS Type`).

- [ ] **Step 5: Chạy test gate — PASS**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~SchemaTokenGateTests`
Expected: PASS.

- [ ] **Step 6: Cắm gate vào QueryEndpoints**

Trong `api/Endpoints/QueryEndpoints.cs`, thêm `NpgsqlDataSource dataSource` vào tham số cả 2 handler. 

Handler single (`/api/datasets/{datasetId:guid}/query`), sau khi có `dataset` (không null, đã load) và trước khi gọi service:

```csharp
            if (principal.IsApiKeyPrincipal() && dataset is not null)
            {
                var expected = await SchemaTokenGate.ComputeCurrentAsync(dataSource, datasetId, ct);
                var gateError = SchemaTokenGate.BuildGateError(req.Options?.SchemaToken, expected, datasetId);
                if (gateError is not null) return Results.Json(gateError, statusCode: 400);
            }
```

Handler multi (`/api/query`), sau khi resolve `ids` và trước khi gọi `QueryMultiAsync`:

```csharp
            if (principal.IsApiKeyPrincipal())
            {
                foreach (var id in ids)
                {
                    var expected = await SchemaTokenGate.ComputeCurrentAsync(dataSource, id, ct);
                    var provided = req.SchemaTokens is not null && req.SchemaTokens.TryGetValue(id.ToString(), out var t) ? t : null;
                    var gateError = SchemaTokenGate.BuildGateError(provided, expected, id);
                    if (gateError is not null) return Results.Json(gateError, statusCode: 400);
                }
            }
```

(Cần `using ExcelDatasetManager.Api.Services;` và `using Npgsql;` trong file.)

- [ ] **Step 7: Build + toàn bộ test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add api/Services/SchemaTokenGate.cs api/Endpoints/QueryEndpoints.cs api/Models/Contracts.cs api/Models/Errors.cs tests/ExcelDatasetManager.Tests/SchemaTokenGateTests.cs
git commit -m "feat: enforce schema_token on query endpoints for PAT callers"
```

---

### Task 9: Pre-flight SQL rules trong ExternalQueryGuard

**Files:**
- Modify: `api/Services/Connectors/ExternalQueryGuard.cs` (thêm bước pre-flight sau các check hiện có)
- Test: `tests/ExcelDatasetManager.Tests/ExternalQueryGuardTests.cs` (thêm case)

**Interfaces:**
- Produces: `ExternalQueryGuard.Validate` trả các code mới `SQL_INCOMPLETE`, `SQL_PARAMETERS_NOT_SUPPORTED`, `SQL_INVALID_IDENTIFIER_QUOTING` cho SQL sai cú pháp thường gặp.

- [ ] **Step 1: Viết test**

Trong `tests/ExcelDatasetManager.Tests/ExternalQueryGuardTests.cs`, thêm:

```csharp
    [Fact]
    public void Rejects_with_cte_without_final_select()
    {
        var sql = "WITH x AS (SELECT 1 AS a)";
        var r = ExternalQueryGuard.Validate(sql, "bigquery");
        Assert.False(r.Success);
        Assert.Equal("SQL_INCOMPLETE", r.Code);
    }

    [Fact]
    public void Allows_with_cte_with_final_select()
    {
        var sql = "WITH x AS (SELECT 1 AS a) SELECT * FROM x";
        var r = ExternalQueryGuard.Validate(sql, "bigquery");
        Assert.True(r.Success);
    }

    [Fact]
    public void Rejects_at_parameter()
    {
        var sql = "SELECT * FROM t WHERE d = @run_date";
        var r = ExternalQueryGuard.Validate(sql, "bigquery");
        Assert.False(r.Success);
        Assert.Equal("SQL_PARAMETERS_NOT_SUPPORTED", r.Code);
    }

    [Fact]
    public void Allows_at_inside_string_literal()
    {
        var sql = "SELECT * FROM t WHERE email = 'a@b.com'";
        var r = ExternalQueryGuard.Validate(sql, "bigquery");
        Assert.True(r.Success);
    }

    [Fact]
    public void Rejects_mssql_bracket_spanning_dot()
    {
        var sql = "SELECT * FROM [dbo.sync_fb_campaigns_report_days]";
        var r = ExternalQueryGuard.Validate(sql, "mssql");
        Assert.False(r.Success);
        Assert.Equal("SQL_INVALID_IDENTIFIER_QUOTING", r.Code);
    }

    [Fact]
    public void Allows_mssql_proper_brackets()
    {
        var sql = "SELECT * FROM [dbo].[sync_fb_campaigns]";
        var r = ExternalQueryGuard.Validate(sql, "mssql");
        Assert.True(r.Success);
    }
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~ExternalQueryGuardTests`
Expected: FAIL (các case mới).

- [ ] **Step 3: Thêm pre-flight**

Trong `api/Services/Connectors/ExternalQueryGuard.cs`, ngay trước dòng `var cleanedOriginal = sql.Trim()...` (cuối `Validate`), chèn:

```csharp
        // --- Pre-flight: bắt các lỗi cú pháp thường gặp trước khi chạm DB khách, kèm cách sửa ---

        // (a) Bracket MSSQL bao cả schema.tên: [dbo.table] là MỘT identifier → không resolve.
        if (provider == ExternalDbProviders.MsSql
            && Regex.IsMatch(scrubbed, @"\[[^\]\[]*\.[^\]\[]*\]"))
        {
            return QueryValidationResult.Fail("SQL_INVALID_IDENTIFIER_QUOTING",
                "Use [dbo].[table] or dbo.table — [dbo.table] is a single identifier and will not resolve.");
        }

        // (b) Tham số hoá không hỗ trợ (SQL chạy nguyên văn, không bind).
        if (Regex.IsMatch(scrubbed, @"@[A-Za-z_][A-Za-z0-9_]*"))
        {
            return QueryValidationResult.Fail("SQL_PARAMETERS_NOT_SUPPORTED",
                "Query parameters like @name are not supported. Inline literal values, e.g. '2026-06-01'.");
        }

        // (c) WITH thiếu SELECT/(INSERT... không cho) cuối: sau danh sách CTE ở paren-depth 0 phải còn SELECT.
        if (Regex.IsMatch(trimmed, @"^\s*with\b", RegexOptions.IgnoreCase)
            && !HasTopLevelSelectAfterCtes(scrubbed))
        {
            return QueryValidationResult.Fail("SQL_INCOMPLETE",
                "Your WITH block has no final SELECT. Append the main SELECT after the last CTE.");
        }
```

Thêm helper private static trong class:

```csharp
    // True nếu, sau khi bỏ qua danh sách CTE, còn một SELECT ở paren-depth 0.
    // Heuristic bảo thủ: tìm token 'select' đầu tiên xuất hiện ở depth 0 sau ký tự ')' đóng CTE cuối.
    private static bool HasTopLevelSelectAfterCtes(string scrubbed)
    {
        var depth = 0;
        var lastTopLevelCloseParen = -1;
        for (var i = 0; i < scrubbed.Length; i++)
        {
            var ch = scrubbed[i];
            if (ch == '(') depth++;
            else if (ch == ')') { depth--; if (depth == 0) lastTopLevelCloseParen = i; }
        }
        if (lastTopLevelCloseParen < 0) return false; // WITH mà không có (...) → coi như thiếu
        var tail = scrubbed[(lastTopLevelCloseParen + 1)..];
        return Regex.IsMatch(tail, @"\bselect\b", RegexOptions.IgnoreCase);
    }
```

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~ExternalQueryGuardTests`
Expected: PASS (cả case cũ + mới). Nếu case hợp lệ bị chặn oan, nới heuristic (ví dụ `HasTopLevelSelectAfterCtes` với CTE lồng nhau).

- [ ] **Step 5: Commit**

```bash
git add api/Services/Connectors/ExternalQueryGuard.cs tests/ExcelDatasetManager.Tests/ExternalQueryGuardTests.cs
git commit -m "feat: pre-flight SQL checks (incomplete WITH, @params, MSSQL bracket)"
```

---

### Task 10: ExternalErrorEnricher

**Files:**
- Create: `api/Services/Connectors/ExternalErrorEnricher.cs`
- Modify: `api/Services/ExternalQueryService.cs` (catch-block: gắn `details` từ enricher)
- Test: `tests/ExcelDatasetManager.Tests/ExternalErrorEnricherTests.cs`

**Interfaces:**
- Produces: `ExternalErrorEnricher.Enrich(string provider, string message, IReadOnlyList<string> knownTables, IReadOnlyList<string> knownColumns) : object?` — trả object `details` (`available_tables`/`did_you_mean`/`dialect`…) hoặc null.

- [ ] **Step 1: Viết test**

Tạo `tests/ExcelDatasetManager.Tests/ExternalErrorEnricherTests.cs`:

```csharp
using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ExternalErrorEnricherTests
{
    private static readonly string[] Tables = { "sync_fb_campaigns", "sync_fb_campaigns_days", "sync_tt_campaigns" };
    private static readonly string[] Columns = { "campaign_id", "project_id", "spend" };

    [Fact]
    public void Mssql_invalid_object_lists_tables_and_suggests()
    {
        var d = ExternalErrorEnricher.Enrich("mssql",
            "Invalid object name 'dbo.sync_fb_campaigns_report_days'.", Tables, Columns);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("available_tables", json);
        Assert.Contains("sync_fb_campaigns", json);
    }

    [Fact]
    public void Bigquery_not_found_table_lists_tables()
    {
        var d = ExternalErrorEnricher.Enrich("bigquery",
            "Not found: Table hv-data:ds.sync_xx was not found", Tables, Columns);
        Assert.NotNull(d);
        Assert.Contains("available_tables", System.Text.Json.JsonSerializer.Serialize(d));
    }

    [Fact]
    public void Bigquery_syntax_error_returns_dialect_hint()
    {
        var d = ExternalErrorEnricher.Enrich("bigquery",
            "Syntax error: Unexpected end of script at [62:2]", Tables, Columns);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("dialect", json);
    }

    [Fact]
    public void Unrecognized_message_returns_null()
    {
        var d = ExternalErrorEnricher.Enrich("mysql", "Some unrelated driver error", Tables, Columns);
        Assert.Null(d);
    }
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~ExternalErrorEnricherTests`
Expected: FAIL.

- [ ] **Step 3: Viết implementation**

Tạo `api/Services/Connectors/ExternalErrorEnricher.cs`:

```csharp
using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Nhận diện lỗi phổ biến từ driver DB nguồn (đã scrub) và bổ sung `error.details` giúp AI tự sửa:
/// available_tables / suggested_columns / did_you_mean / dialect hint. Dùng schema đã lưu — không
/// gọi vào DB nguồn. Trả null nếu không nhận diện được (giữ nguyên message gốc ở caller).
/// </summary>
public static class ExternalErrorEnricher
{
    private static readonly Regex MsSqlObject = new(@"Invalid object name '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MsSqlColumn = new(@"Invalid column name '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqTable = new(@"Not found: Table\s+(?<name>[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqName = new(@"Unrecognized name:\s*(?<name>[^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqSyntax = new(@"Syntax error", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PgRelation = new(@"relation ""(?<name>[^""]+)"" does not exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PgColumn = new(@"column ""?(?<name>[^""\s]+)""? does not exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyTable = new(@"Table '(?<name>[^']+)' doesn't exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyColumn = new(@"Unknown column '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static object? Enrich(string provider, string message, IReadOnlyList<string> knownTables, IReadOnlyList<string> knownColumns)
    {
        if (string.IsNullOrEmpty(message)) return null;

        // Bảng không tồn tại
        foreach (var rx in new[] { MsSqlObject, BqTable, PgRelation, MyTable })
        {
            var m = rx.Match(message);
            if (m.Success)
            {
                var name = LastSegment(m.Groups["name"].Value);
                return new
                {
                    missing_table = name,
                    available_tables = knownTables.Take(50).ToArray(),
                    did_you_mean = Closest(name, knownTables)
                };
            }
        }

        // Cột không tồn tại
        foreach (var rx in new[] { MsSqlColumn, BqName, PgColumn, MyColumn })
        {
            var m = rx.Match(message);
            if (m.Success)
            {
                var name = LastSegment(m.Groups["name"].Value);
                return new
                {
                    missing_column = name,
                    suggested_columns = knownColumns.Take(50).ToArray(),
                    did_you_mean = Closest(name, knownColumns)
                };
            }
        }

        // Lỗi cú pháp (thường BigQuery) → nhắc dialect
        if (provider == ExternalDbProviders.BigQuery && BqSyntax.IsMatch(message))
        {
            return new
            {
                dialect = "bigquery",
                hint = "Send ONE complete GoogleSQL statement. A WITH block must end with its final SELECT. No @parameters."
            };
        }

        return null;
    }

    private static string LastSegment(string identifier)
    {
        var trimmed = identifier.Trim().Trim('`', '"', '[', ']');
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }

    private static string? Closest(string target, IReadOnlyList<string> candidates)
    {
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Levenshtein(target.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; best = c; }
        }
        // Chỉ gợi ý nếu đủ gần (tránh gợi ý rác).
        return bestDist <= Math.Max(3, target.Length / 2) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        return dp[a.Length, b.Length];
    }
}
```

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test tests/ExcelDatasetManager.Tests --filter FullyQualifiedName~ExternalErrorEnricherTests`
Expected: PASS.

- [ ] **Step 5: Cắm vào ExternalQueryService catch-block**

Trong `api/Services/ExternalQueryService.cs`, catch-block (dòng ~219): trước khi `return new {...}`, load schema đã biết và enrich:

```csharp
                var knownTables = new List<string>();
                var knownColumns = new List<string>();
                try
                {
                    var tabs = await dbConnectionService /* KHÔNG có */;
                }
                catch { }
```

Thực tế `ExternalQueryService` có `NpgsqlDataSource dataSource`. Dùng nó:

```csharp
                var (knownTables, knownColumns) = await LoadKnownSchemaAsync(datasetId, ct);
                var details = ExternalErrorEnricher.Enrich(provider, message, knownTables, knownColumns);

                return new
                {
                    success = false,
                    dataset_id = datasetId,
                    query_id = queryId,
                    status = "failed",
                    result = (object?)null,
                    execution = new { engine = provider, elapsed_ms = sw.ElapsedMilliseconds },
                    sql = new { submitted = request.Sql, executed = executedSql },
                    warnings = Array.Empty<string>(),
                    error = new
                    {
                        code = ErrorCodes.ExternalQueryFailed,
                        message,
                        details,
                        assistant_instruction = "Fix the SQL using error.details (available_tables / did_you_mean / hint). Retry at most twice. If it still fails, report this error to the user verbatim and never fabricate data.",
                        retryable = details is not null
                    }
                };
```

Thêm helper private trong `ExternalQueryService`:

```csharp
    private async Task<(List<string> Tables, List<string> Columns)> LoadKnownSchemaAsync(Guid datasetId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM dataset_tables WHERE dataset_id = @Id ORDER BY table_name",
                new { Id = datasetId })).ToList();
            var columns = (await conn.QueryAsync<string>("""
                SELECT DISTINCT c.normalized_name
                FROM dataset_columns c JOIN dataset_tables t ON t.id = c.dataset_table_id
                WHERE t.dataset_id = @Id
                """, new { Id = datasetId })).ToList();
            return (tables, columns);
        }
        catch { return (new List<string>(), new List<string>()); }
    }
```

(Cần `using Dapper;` — file đã có.)

- [ ] **Step 6: Build + test toàn bộ**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add api/Services/Connectors/ExternalErrorEnricher.cs api/Services/ExternalQueryService.cs tests/ExcelDatasetManager.Tests/ExternalErrorEnricherTests.cs
git commit -m "feat: enrich external query errors with available_tables/did_you_mean/dialect hint"
```

---

### Task 11: assistant_instruction trên mọi error envelope của query

**Files:**
- Modify: `api/Services/DuckDbQueryService.cs` (`BuildErrorResponse`, `BuildMultiError`, catch-block, `BuildTokenBudgetError`)
- Modify: `api/Services/ExternalQueryService.cs` (`BuildErrorResponse`, `BuildTooManyConcurrentResponse`, `BuildTokenBudgetError`)

**Interfaces:**
- Produces: mọi object `error` trong response query có field `assistant_instruction: string`.

- [ ] **Step 1: Thêm hằng chỉ thị dùng chung**

Trong `api/Models/Errors.cs`, thêm:

```csharp
public static class AssistantInstructions
{
    public const string NeverFabricate =
        "Report this error to the user verbatim. Never estimate, interpolate, or fabricate data values. If you cannot obtain real data, tell the user what failed and stop.";
}
```

- [ ] **Step 2: DuckDbQueryService — thêm field vào từng error object**

Trong `api/Services/DuckDbQueryService.cs`:
- `BuildErrorResponse`: trong `error = new { code, message, retryable ... }` thêm `assistant_instruction = AssistantInstructions.NeverFabricate,`.
- `BuildMultiError`: tương tự.
- catch-block (`return new {... error = new { code, message, details, retryable ...}}`): thêm `assistant_instruction = AssistantInstructions.NeverFabricate,`.
- `BuildTokenBudgetError`: thêm `assistant_instruction = AssistantInstructions.NeverFabricate,`.

(Thêm `using ExcelDatasetManager.Api.Models;` nếu chưa — file đã có.)

- [ ] **Step 3: ExternalQueryService — tương tự**

Trong `api/Services/ExternalQueryService.cs`: `BuildErrorResponse`, `BuildTooManyConcurrentResponse`, `BuildTokenBudgetError` — thêm `assistant_instruction = AssistantInstructions.NeverFabricate,` vào object `error`. (Catch-block đã có instruction riêng ở Task 10 — giữ nguyên bản chi tiết đó.)

- [ ] **Step 4: Build**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add api/Models/Errors.cs api/Services/DuckDbQueryService.cs api/Services/ExternalQueryService.cs
git commit -m "feat: add anti-fabrication assistant_instruction to all query error envelopes"
```

---

### Task 12: Cập nhật tools.md + tools.example.md

**Files:**
- Modify: `mcp-bridge/tools.md`
- Modify: `mcp-bridge/tools.example.md`

**Interfaces:**
- Consumes: endpoints `/api/query-guide` (Task 6), `/api/context` guide_token (Task 7), schema_token (Task 8).
- Produces: MCP tool `get_query_guide`; `get_context` có param `guide_token`; `query_dataset`/`query_datasets` có param `schema_token`.

- [ ] **Step 1: Thêm tool get_query_guide**

Trong `mcp-bridge/tools.md`, trước `## list_datasets` (khoảng dòng 56), thêm block:

````markdown
## get_query_guide

```yaml
type: tool
name: get_query_guide
description: |
  Call this FIRST in every session, before any other EDM tool. Returns the EDM usage guide
  (required workflow, per-dialect SQL rules, error-handling and anti-fabrication rules) and a
  guide_token. You must READ the guide and pass guide_token to get_context.
connection: edm
method: GET
path: /api/query-guide
response_hint: |
  Read `content` fully. Keep `guide_token` and pass it to get_context.
```
````

- [ ] **Step 2: Thêm guide_token vào get_context**

Trong block `get_context`, thêm vào `params`:

```yaml
  guide_token:
    in: query
    type: string
    required: true
    description: The token returned by get_query_guide (proves you read the guide).
```

Và bổ sung vào `description` câu: `Requires guide_token from get_query_guide. Returns each dataset's schema_token — you must pass it to query tools.` Bổ sung `response_hint`: `Each dataset has schema_token + dialect_notes. Obey dialect_notes. Pass schema_token to query_dataset/query_datasets.`

- [ ] **Step 3: Thêm schema_token vào query tools**

Trong `query_dataset` params, thêm:

```yaml
  schema_token:
    in: body
    type: string
    required: true
    description: The schema_token from get_context for this dataset. Required; the server rejects queries without it.
```

Và sửa `body_template` để chuyển token vào options:

```yaml
body_template: |
  {
    "query_type": "sql",
    "sql": {{sql | json}},
    "options": { "max_rows": {{max_rows}}, "include_sql": true, "schema_token": {{schema_token | json}} }
  }
```

Cập nhật `description` + `response_hint`: thêm "If a query fails, read error.details and fix; NEVER invent data." Cho `query_datasets` (multi): thêm param `schema_tokens` (object dataset_id→token) vào body template tương ứng — kiểm block hiện có và thêm:

```yaml
  schema_tokens:
    in: body
    type: object
    required: true
    description: Map of dataset_id → schema_token (from get_context) for every dataset in the query.
```
(và thêm `"schema_tokens": {{schema_tokens | json}}` vào body_template của `/api/query`.)

- [ ] **Step 4: Widget tools**

Nếu `create_dashboard_widget`/`update_dashboard_widget` chạy SQL qua cùng endpoint query, đảm bảo chúng cũng truyền `schema_token`. Nếu chúng gọi endpoint dashboard riêng (không đi qua `/api/datasets/{id}/query`), thì gate không áp — ghi chú trong tools.md rằng widget SQL nên được kiểm bằng query_dataset trước. **Kiểm:** đọc block widget trong tools.md; nếu path là `/api/datasets/{id}/query` thì thêm `schema_token` như Step 3, nếu khác thì để nguyên (ngoài phạm vi gate hiện tại).

- [ ] **Step 5: Đồng bộ tools.example.md**

Áp cùng thay đổi (Step 1–4) vào `mcp-bridge/tools.example.md` để repo mẫu nhất quán.

- [ ] **Step 6: Validate cấu hình bridge**

Run: `cd mcp-bridge && node dist/index.js validate ./tools.md`
Expected: `tools.md` parse OK, liệt kê số tool tăng thêm 1 (get_query_guide). Nếu `dist` chưa build: `npm run build` trước.

- [ ] **Step 7: Commit**

```bash
git add mcp-bridge/tools.md mcp-bridge/tools.example.md
git commit -m "feat: tools.md — get_query_guide + guide_token/schema_token params"
```

---

## Post-implementation: deploy + smoke test trên server

Sau khi mọi task xong (không phải task, là hướng dẫn vận hành):

1. Đưa repo lên server (`D:\DokerDomain\MCP\edm`), chạy migration tự áp khi API khởi động.
2. `docker compose -f allinone.yaml up -d --build api bridge`.
3. Smoke test bằng PAT (curl ở Task 7 Step 3 + một query thật) — xác nhận: get_context thiếu guide_token → GUIDE_REQUIRED; query thiếu schema_token → CONTEXT_REQUIRED; query `[dbo.table]` → SQL_INVALID_IDENTIFIER_QUOTING; query bảng sai tên → details.available_tables.
4. Trên claude.ai bắt đầu chat mới, xác nhận Claude gọi get_query_guide → get_context → query đúng chuỗi.

---

## Self-Review

**Spec coverage:**
- Thành phần 0 (guide + guide_token) → Task 6, 7, 12. ✓
- Thành phần 1 (schema token) → Task 3, 5, 8. ✓
- Thành phần 2a (pre-flight) → Task 9. ✓
- Thành phần 2b (error enricher) → Task 10. ✓
- Thành phần 3 (chống bịa) → Task 8 (gate errors), 10 (external catch), 11 (mọi envelope), 12 (tools.md). ✓
- Thành phần 4 (bỏ dataset key + toggle) → Task 1, 2. ✓
- Testing spec → mỗi task có test/verify. ✓

**Type consistency:**
- `SchemaTokenService.Compute` chữ ký `(IEnumerable<(string, IReadOnlyList<(string,string)>)>)` dùng nhất quán ở Task 3, 5 (ContextService), 8 (gate). ✓
- Token tính từ `normalized_name` + `inferred_type` ở cả ContextService (Task 5) và SchemaTokenGate (Task 8) — cùng nguồn ⇒ token khớp. ✓
- `IsApiKeyPrincipal` định nghĩa Task 2, dùng Task 7, 8, và knowledge toggle Task 2. ✓
- `AssistantInstructions.NeverFabricate` định nghĩa Task 11, dùng trong cùng task. ✓
- `guide_token` là query param (get_context, Task 7 & 12); `schema_token` là body/options (query, Task 8 & 12). ✓

**Placeholder scan:** Task 5 Step 4 test có phần "build a ContextDatasetInput" mô tả — chấp nhận vì phụ thuộc helper sẵn có trong file test hiện trạng; executor điền theo constructor đã cập nhật. Task 12 Step 4 (widget) là điều kiện "kiểm rồi quyết" — hợp lệ vì phụ thuộc nội dung tools.md thực tế. Không có TODO/TBD khác.
