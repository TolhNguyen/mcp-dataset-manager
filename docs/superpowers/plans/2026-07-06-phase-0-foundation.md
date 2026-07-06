# Phase 0 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trả nợ kỹ thuật nền tảng trước big update: đóng lỗ hổng xác thực MCP, migration framework, mã hoá secret, sửa rate limit sau proxy, chuyển test sang xUnit + test QueryValidator, tách Program.cs.

**Architecture:** ASP.NET Core 8 minimal API + Dapper + PostgreSQL (KHÔNG dùng EF). MCP bridge là Node/TypeScript (Express + @modelcontextprotocol/sdk). Test bằng xUnit.

**Tech Stack:** .NET 8, Dapper, Npgsql, xUnit, TypeScript/Node 20, Express.

**Spec gốc:** `docs/superpowers/specs/2026-07-06-big-update-design.md` (Phase 0, mục P0-1 … P0-6).

## Global Constraints

- TargetFramework: `net8.0`. Không thêm package ngoài danh sách ghi trong từng task.
- KHÔNG dùng Entity Framework — mọi truy vấn DB dùng Dapper như code hiện có.
- JSON API luôn snake_case (đã cấu hình sẵn trong Program.cs — không đổi).
- Mọi lệnh chạy từ repo root `D:\mcp-dataset-manager` (hoặc tương đương).
- Sau MỖI task: `dotnet build api/ExcelDatasetManager.Api.csproj` và `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` phải pass trước khi commit.
- Commit message tiếng Anh, prefix `feat:`/`fix:`/`refactor:`/`test:`/`docs:`.
- KHÔNG làm bất kỳ việc gì thuộc Phase A/B/C/D (connections, knowledge, context API, dashboards).

---

### Task 1: Chuyển test project sang xUnit

**Files:**
- Modify: `tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
- Delete: `tests/ExcelDatasetManager.Tests/Program.cs`
- Create: `tests/ExcelDatasetManager.Tests/TestData.cs`
- Create: `tests/ExcelDatasetManager.Tests/ManifestGeneratorTests.cs`
- Create: `tests/ExcelDatasetManager.Tests/AiTokenBudgetServiceTests.cs`

**Interfaces:**
- Consumes: `ManifestGenerator.GenerateAsync(path, DatasetRecord, IReadOnlyList<ParsedTable>, IReadOnlyList<string>, CancellationToken)`, `AiTokenBudgetService.Decide(object, QueryOptions?, string)` — có sẵn trong `api/Services/`.
- Produces: helper `TestData.NewDatasetRecord(string businessKnowledge)`, `TestData.NewParsedTable()`, `TestData.NewAiTokenBudgetService(int safeMaxTokens, int hardMaxTokens)` — các task sau dùng lại.

- [ ] **Step 1: Ghi đè file csproj**

Thay toàn bộ nội dung `tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` bằng:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <BaseOutputPath>..\..\.tmp\tests-bin\</BaseOutputPath>
    <BaseIntermediateOutputPath>..\..\.tmp\tests-obj\ExcelDatasetManager.Tests\</BaseIntermediateOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\..\api\ExcelDatasetManager.Api.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Tạo `tests/ExcelDatasetManager.Tests/TestData.cs`**

```csharp
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Configuration;

namespace ExcelDatasetManager.Tests;

internal static class TestData
{
    public static AiTokenBudgetService NewAiTokenBudgetService(int safeMaxTokens, int hardMaxTokens)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Query:SafeMaxTokens"] = safeMaxTokens.ToString(),
                ["Query:HardMaxTokens"] = hardMaxTokens.ToString(),
                ["Query:PreviewRows"] = "2",
                ["Query:TokenEstimationCharsPerToken"] = "4"
            })
            .Build();

        return new AiTokenBudgetService(config);
    }

    public static DatasetRecord NewDatasetRecord(string businessKnowledge) => new(
        Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        UserId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Name: "Orders",
        OriginalFileName: "orders.csv",
        FileType: "csv",
        StoredFileName: "original_file.csv",
        FileSizeBytes: 123,
        ManifestFileName: "manifest.md",
        Status: "ready",
        TableCount: 1,
        TotalRows: 1,
        ErrorMessage: null,
        CreatedAt: DateTime.UtcNow,
        ProcessedAt: DateTime.UtcNow,
        BusinessKnowledge: businessKnowledge,
        BusinessKnowledgeUpdatedAt: DateTime.UtcNow);

    public static ParsedTable NewParsedTable() => new(
        SourceName: "orders",
        SourceType: "csv_file",
        TableName: "orders",
        TempCsvPath: "",
        ParquetFileName: "orders.parquet",
        RowCount: 1,
        Columns:
        [
            new ParsedColumn(
                Ordinal: 1,
                OriginalHeader: "Revenue",
                NormalizedName: "revenue",
                DisplayName: "Revenue",
                Aliases: ["revenue"],
                InferredType: "DOUBLE",
                SemanticType: "amount",
                NullCount: 0,
                DistinctCount: 1,
                DistinctCapped: false,
                SampleValues: ["100"])
        ]);
}
```

- [ ] **Step 3: Tạo `tests/ExcelDatasetManager.Tests/ManifestGeneratorTests.cs`**

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ManifestGeneratorTests
{
    [Fact]
    public async Task Manifest_includes_provided_business_knowledge_as_reference_notes()
    {
        var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
        var generator = new ManifestGenerator();
        var dataset = TestData.NewDatasetRecord("Chi tinh doanh thu voi status = Completed.");
        var table = TestData.NewParsedTable();

        await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("## Business Knowledge / User-provided Notes", content);
        Assert.Contains("Treat them as reference context only", content);
        Assert.Contains("Chi tinh doanh thu voi status = Completed.", content);
    }

    [Fact]
    public async Task Manifest_explains_when_business_knowledge_is_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
        var generator = new ManifestGenerator();
        var dataset = TestData.NewDatasetRecord("");
        var table = TestData.NewParsedTable();

        await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("No user-provided business knowledge has been added yet.", content);
    }
}
```

- [ ] **Step 4: Tạo `tests/ExcelDatasetManager.Tests/AiTokenBudgetServiceTests.cs`**

```csharp
using ExcelDatasetManager.Api.Models;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class AiTokenBudgetServiceTests
{
    [Fact]
    public void Allows_safe_result()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 50, hardMaxTokens: 100);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, null));

        Assert.Equal("safe", decision.Status);
        Assert.False(decision.RequiresConfirmation);
        Assert.False(decision.Blocked);
    }

    [Fact]
    public void Requires_confirmation_above_safe_threshold_then_accepts_confirmation()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 100);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 40) } } };
        var options = new QueryOptions(null, null, null, null, null, null, null, "ai_safe");

        var decision = service.Decide(payload, options);

        Assert.Equal("requires_confirmation", decision.Status);
        Assert.True(decision.RequiresConfirmation);
        Assert.False(decision.Blocked);
        Assert.NotNull(decision.ConfirmationId);

        var confirmed = service.Decide(payload, options with
        {
            AllowLargeResult = true,
            ConfirmationId = decision.ConfirmationId,
            ResponseMode = "raw"
        });

        Assert.Equal("confirmed", confirmed.Status);
        Assert.False(confirmed.RequiresConfirmation);
    }

    [Fact]
    public void Blocks_above_hard_threshold()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 10);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 100) } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "raw"));

        Assert.Equal("blocked", decision.Status);
        Assert.True(decision.Blocked);
        Assert.False(decision.RequiresConfirmation);
    }

    [Fact]
    public void Summary_mode_never_returns_raw_rows()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 1000, hardMaxTokens: 2000);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "summary"));

        Assert.Equal("summary", decision.Status);
        Assert.False(decision.AllowRaw);
    }
}
```

- [ ] **Step 5: Xoá `tests/ExcelDatasetManager.Tests/Program.cs`** (console runner cũ — đã port hết sang 2 file test ở trên).

- [ ] **Step 6: Chạy test**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: `Passed!` — 6 tests passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add tests/
git commit -m "test: migrate hand-rolled test runner to xUnit"
```

---

### Task 2: Test QueryValidator (lớp an ninh SQL)

**Files:**
- Create: `tests/ExcelDatasetManager.Tests/QueryValidatorTests.cs`

**Interfaces:**
- Consumes: `QueryValidator.ValidateReadOnlySelect(string?)` trả `QueryValidationResult(bool Success, string? Sql, string? Code, string? Message)`; `QueryValidator.ApplyLimit(string sql, int maxRows)` — có sẵn trong `api/Services/QueryValidator.cs`.
- Produces: không có — task này chỉ chốt hành vi hiện tại bằng test.

Lưu ý cho người thực thi: các test này mô tả hành vi ĐÚNG hiện tại. Nếu test nào FAIL, KHÔNG sửa test cho pass — dừng lại và báo cáo, vì đó là bug thật trong validator.

- [ ] **Step 1: Tạo `tests/ExcelDatasetManager.Tests/QueryValidatorTests.cs`**

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryValidatorTests
{
    private readonly QueryValidator _validator = new();

    // ---------- Cho phép ----------

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("select id, name from customers where city = 'Hà Nội'")]
    [InlineData("WITH t AS (SELECT 1 AS x) SELECT * FROM t")]
    [InlineData("SELECT * FROM orders;")]
    [InlineData("  \n SELECT 1")]
    public void Accepts_readonly_queries(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Forbidden_keyword_inside_string_literal_is_allowed()
    {
        var result = _validator.ValidateReadOnlySelect(
            "SELECT * FROM notes WHERE body = 'please update and delete this'");
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Forbidden_keyword_inside_comment_is_ignored()
    {
        var result = _validator.ValidateReadOnlySelect(
            "SELECT 1 -- drop table orders\n");
        Assert.True(result.Success, result.Message);
    }

    // ---------- Chặn ----------

    [Theory]
    [InlineData("INSERT INTO orders VALUES (1)")]
    [InlineData("UPDATE orders SET x = 1")]
    [InlineData("DELETE FROM orders")]
    [InlineData("DROP TABLE orders")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("ATTACH 'other.db'")]
    [InlineData("PRAGMA database_list")]
    [InlineData("COPY orders TO 'out.csv'")]
    [InlineData("CALL something()")]
    [InlineData("INSTALL httpfs")]
    public void Rejects_non_select_statements(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    [Theory]
    [InlineData("SELECT * FROM read_parquet('/etc/passwd')")]
    [InlineData("SELECT * FROM read_csv_auto('secret.csv')")]
    [InlineData("SELECT 1 UNION SELECT * FROM read_json('x.json')")]
    [InlineData("WITH t AS (SELECT 1) SELECT * FROM t; SET memory_limit='100GB'")]
    public void Rejects_dangerous_functions_even_inside_select(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
    }

    [Fact]
    public void Rejects_multiple_statements()
    {
        var result = _validator.ValidateReadOnlySelect("SELECT 1; SELECT 2");
        Assert.False(result.Success);
        Assert.Equal("INVALID_SQL", result.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void Rejects_empty_sql(string? sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
        Assert.Equal("INVALID_SQL", result.Code);
    }

    [Fact]
    public void Trailing_semicolon_is_stripped_from_returned_sql()
    {
        var result = _validator.ValidateReadOnlySelect("SELECT 1;");
        Assert.True(result.Success);
        Assert.Equal("SELECT 1", result.Sql);
    }

    // ---------- ApplyLimit ----------

    [Fact]
    public void ApplyLimit_wraps_query_without_limit()
    {
        var result = _validator.ApplyLimit("SELECT * FROM t", 100);
        Assert.Equal("SELECT * FROM (SELECT * FROM t) AS _user_query LIMIT 100", result);
    }

    [Fact]
    public void ApplyLimit_keeps_existing_top_level_limit()
    {
        var sql = "SELECT * FROM t LIMIT 5";
        Assert.Equal(sql, _validator.ApplyLimit(sql, 100));
    }

    [Fact]
    public void ApplyLimit_keeps_existing_limit_with_offset()
    {
        var sql = "SELECT * FROM t LIMIT 5 OFFSET 10";
        Assert.Equal(sql, _validator.ApplyLimit(sql, 100));
    }

    [Fact]
    public void ApplyLimit_wraps_when_limit_is_only_in_subquery()
    {
        var sql = "SELECT * FROM (SELECT * FROM t LIMIT 5) q";
        var result = _validator.ApplyLimit(sql, 100);
        Assert.EndsWith("LIMIT 100", result);
        Assert.StartsWith("SELECT * FROM (", result);
    }
}
```

- [ ] **Step 2: Chạy test**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: tất cả pass (6 test cũ + ~30 case mới). Nếu có case fail → DỪNG, báo cáo, không sửa test.

- [ ] **Step 3: Commit**

```bash
git add tests/ExcelDatasetManager.Tests/QueryValidatorTests.cs
git commit -m "test: pin down QueryValidator read-only enforcement behavior"
```

---

### Task 3: Migration framework

**Files:**
- Create: `api/Migrations/0001_baseline.sql`
- Create: `api/Services/MigrationRunner.cs`
- Modify: `api/ExcelDatasetManager.Api.csproj` (embed SQL)
- Modify: `api/Program.cs` (thay DatabaseInitializer bằng MigrationRunner)
- Delete: `api/Services/DatabaseInitializer.cs`
- Create: `tests/ExcelDatasetManager.Tests/MigrationScriptLoaderTests.cs`

**Interfaces:**
- Produces: `MigrationRunner.RunAsync(CancellationToken)` (thay thế `DatabaseInitializer.InitializeAsync`); `MigrationScriptLoader.LoadAll(Assembly)` trả `IReadOnlyList<MigrationScript>`; record `MigrationScript(int Version, string Name, string Sql)`. Các phase sau (A/B/C/D) sẽ thêm file `000N_*.sql` vào `api/Migrations/`.

- [ ] **Step 1: Tạo `api/Migrations/0001_baseline.sql`**

Nội dung = CHÍNH XÁC chuỗi SQL trong `api/Services/DatabaseInitializer.cs` hiện tại (từ `CREATE TABLE IF NOT EXISTS users` đến index cuối `idx_user_api_keys_user_id`). Copy nguyên văn phần bên trong `const string sql = """ ... """;` — không thêm bớt. Script này idempotent (toàn `IF NOT EXISTS`) nên chạy an toàn trên DB đang có dữ liệu.

- [ ] **Step 2: Embed SQL vào assembly**

Thêm vào `api/ExcelDatasetManager.Api.csproj` (trước `</Project>`):

```xml
  <ItemGroup>
    <EmbeddedResource Include="Migrations\*.sql" />
  </ItemGroup>
```

- [ ] **Step 3: Viết test cho loader — `tests/ExcelDatasetManager.Tests/MigrationScriptLoaderTests.cs`**

```csharp
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class MigrationScriptLoaderTests
{
    [Fact]
    public void Loads_baseline_migration_from_assembly()
    {
        var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);

        Assert.NotEmpty(scripts);
        Assert.Equal(1, scripts[0].Version);
        Assert.Equal("0001_baseline.sql", scripts[0].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS users", scripts[0].Sql);
    }

    [Fact]
    public void Scripts_are_ordered_by_version_without_duplicates()
    {
        var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);

        var versions = scripts.Select(s => s.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v).ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }
}
```

- [ ] **Step 4: Chạy test để thấy fail**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter MigrationScriptLoaderTests`
Expected: FAIL — compile error `MigrationScriptLoader` chưa tồn tại.

- [ ] **Step 5: Tạo `api/Services/MigrationRunner.cs`**

```csharp
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public sealed record MigrationScript(int Version, string Name, string Sql);

public static class MigrationScriptLoader
{
    private const string ResourcePrefix = "ExcelDatasetManager.Api.Migrations.";
    private static readonly Regex NamePattern = new(@"^(\d{4})_[A-Za-z0-9_]+\.sql$", RegexOptions.Compiled);

    public static IReadOnlyList<MigrationScript> LoadAll(Assembly assembly)
    {
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            var fileName = resource[ResourcePrefix.Length..];
            var match = NamePattern.Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Migration resource '{fileName}' must be named NNNN_description.sql (e.g. 0002_add_connections.sql).");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Cannot open embedded resource '{resource}'.");
            using var reader = new StreamReader(stream);

            scripts.Add(new MigrationScript(
                Version: int.Parse(match.Groups[1].Value),
                Name: fileName,
                Sql: reader.ReadToEnd()));
        }

        var ordered = scripts.OrderBy(s => s.Version).ToList();
        var duplicate = ordered.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate migration version {duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.Name))}.");
        }

        return ordered;
    }
}

public class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INT PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """);

        var applied = (await conn.QueryAsync<int>("SELECT version FROM schema_migrations")).ToHashSet();

        foreach (var script in MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly))
        {
            if (applied.Contains(script.Version)) continue;

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(script.Sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_migrations (version, name) VALUES (@Version, @Name)",
                new { script.Version, script.Name }, tx);
            await tx.CommitAsync(ct);

            logger.LogInformation("Applied migration {Name}", script.Name);
        }
    }
}
```

- [ ] **Step 6: Cập nhật `api/Program.cs`**

a) Đổi đăng ký DI: dòng `builder.Services.AddScoped<DatabaseInitializer>();` → `builder.Services.AddScoped<MigrationRunner>();`

b) Trong khối `// DB init`: dòng `await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();` → `await scope.ServiceProvider.GetRequiredService<MigrationRunner>().RunAsync();`

- [ ] **Step 7: Xoá `api/Services/DatabaseInitializer.cs`**

- [ ] **Step 8: Build + test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: build OK, tất cả test pass (gồm 2 test loader mới).

- [ ] **Step 9: Commit**

```bash
git add api/ tests/
git commit -m "feat: replace ad-hoc DatabaseInitializer with numbered SQL migration runner"
```

---

### Task 4: Xoá lỗ hổng GUID-as-API-key

**Files:**
- Modify: `api/Auth/ApiKeyAuthenticationHandler.cs` (xoá nhánh `Guid.TryParse`)
- Create: `tests/ExcelDatasetManager.Tests/ApiKeyAuthenticationHandlerTests.cs`

**Interfaces:**
- Consumes: `ApiKeyAuthenticationHandler` (ctor: `IOptionsMonitor<ApiKeyAuthenticationOptions>, ILoggerFactory, UrlEncoder, NpgsqlDataSource`).
- Produces: hành vi mới — MỌI key không bắt đầu bằng `edm_` đều bị từ chối. Task 5 (bridge) phụ thuộc hành vi này.

Bối cảnh: hiện tại handler chấp nhận một GUID trần làm API key và cấp toàn quyền user đó (claim `auth_method = "insecure_user_id"`). Đây là lỗ hổng nghiêm trọng cần xoá. Nhánh GUID chạy TRƯỚC khi chạm DB, nên test không cần database (truyền `null!` cho `NpgsqlDataSource`).

- [ ] **Step 1: Viết test TRƯỚC — `tests/ExcelDatasetManager.Tests/ApiKeyAuthenticationHandlerTests.cs`**

```csharp
using System.Text.Encodings.Web;
using ExcelDatasetManager.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")] // GUID trần — lỗ hổng cũ phải bị chặn
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("hello")]
    [InlineData("Bearer edm_pat_x")] // sai format (có prefix Bearer trong header X-API-Key)
    public async Task Rejects_keys_that_are_not_edm_prefixed(string rawKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = rawKey;

        var result = await AuthenticateAsync(context);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Returns_no_result_when_header_is_missing()
    {
        var context = new DefaultHttpContext();

        var result = await AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        var handler = new ApiKeyAuthenticationHandler(
            new OptionsMonitorStub(new ApiKeyAuthenticationOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            dataSource: null!); // các case test đều fail trước khi chạm DB

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationOptions.SchemeName,
            displayName: null,
            handlerType: typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private sealed class OptionsMonitorStub(ApiKeyAuthenticationOptions options)
        : IOptionsMonitor<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationOptions CurrentValue => options;
        public ApiKeyAuthenticationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<ApiKeyAuthenticationOptions, string?> listener) => null!;
    }
}
```

- [ ] **Step 2: Chạy test để thấy fail đúng chỗ**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter ApiKeyAuthenticationHandlerTests`
Expected: 2 case GUID FAIL (vì code hiện tại CẤP QUYỀN cho GUID — `result.Succeeded == true`). Các case còn lại pass. Nếu case GUID pass ngay từ đầu → dừng, kiểm tra lại có đang đứng đúng branch code không.

- [ ] **Step 3: Xoá nhánh GUID trong `api/Auth/ApiKeyAuthenticationHandler.cs`**

Xoá toàn bộ khối này (ngay sau `var rawKey = headerValues.ToString().Trim();`):

```csharp
        if (Guid.TryParse(rawKey, out var insecureUserId))
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, insecureUserId.ToString()),
                new Claim("sub", insecureUserId.ToString()),
                new Claim("auth_method", "insecure_user_id")
            };

            return AuthSuccess(claims);
        }
```

Không sửa gì khác trong file.

- [ ] **Step 4: Chạy test**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: tất cả pass.

- [ ] **Step 5: Commit**

```bash
git add api/Auth/ApiKeyAuthenticationHandler.cs tests/ExcelDatasetManager.Tests/ApiKeyAuthenticationHandlerTests.cs
git commit -m "fix(security): remove insecure GUID-as-API-key authentication path"
```

---

### Task 5: MCP bridge — bắt buộc PAT, bỏ /mcp/{userId}

**Files:**
- Modify: `mcp-bridge/src/index.ts`
- Modify: `mcp-bridge/tools.md` (chỉ phần mô tả — không đổi cấu trúc connection)

**Interfaces:**
- Consumes: `RequestContext { userToken, authorization, clientIp?, sessionId? }` từ `src/requestContext.ts` (không đổi); EDM API chấp nhận PAT `edm_pat_...` qua header `X-API-Key` (Task 4 giữ nguyên hành vi này).
- Produces: HTTP MCP endpoint mới là `POST/GET/DELETE /mcp`, yêu cầu header `Authorization: Bearer edm_pat_...`. `${request.user_token}` trong tools.md giờ resolve thành PAT (trước đây là userId). KHÔNG còn route `/mcp/:userId`.

Bối cảnh: hiện tại URL `/mcp/{userId}` dùng chính userId làm credential → không có xác thực thật. Sau task này client phải gửi PAT. Chấp nhận breaking change (đã chốt trong spec).

- [ ] **Step 1: Sửa `mcp-bridge/src/index.ts` — thay toàn bộ phần route HTTP**

a) Thêm helper (đặt gần `getSessionId`, thay thế hàm `getUserIdParam` và `isGuid` — XOÁ 2 hàm đó):

```typescript
const PAT_PATTERN = /^Bearer\s+(edm_pat_\S+)$/i;

function extractPat(req: Request): string | undefined {
  const raw = req.header("authorization");
  if (!raw) return undefined;
  const match = PAT_PATTERN.exec(raw.trim());
  return match ? match[1] : undefined;
}
```

b) Sửa `buildRequestContext` thành:

```typescript
function buildRequestContext(req: Request, pat: string, sessionId?: string) {
  return {
    userToken: pat,
    authorization: `Bearer ${pat}`,
    clientIp: req.ip,
    sessionId,
  };
}
```

c) Trong `runHttpServer()`:
- Đổi `activeSessions` map value type thành `{ transport: StreamableHTTPServerTransport; server: Server; pat: string }`.
- Đổi `app.options("/mcp/:userId", ...)` thành `app.options("/mcp", ...)`.
- XOÁ handler `app.all("/mcp", (_req, res) => { jsonRpcError(res, 400, -32000, "Bad Request: use /mcp/{userId}."); });`
- Thay 3 handler `app.post/get/delete("/mcp/:userId", ...)` bằng phiên bản `/mcp` dưới đây (logic giống hệt bản cũ, chỉ khác: PAT thay userId, kiểm tra PAT khớp session):

```typescript
  app.post("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const pat = extractPat(req);
    if (!pat) {
      return jsonRpcError(res, 401, -32001,
        "Unauthorized: send 'Authorization: Bearer edm_pat_...' (create a personal access token in the EDM web UI).");
    }
    const context = buildRequestContext(req, pat, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        let session = sessionId ? activeSessions.get(sessionId) : undefined;

        if (session && session.pat !== pat) {
          return jsonRpcError(res, 403, -32003, "Forbidden: session belongs to a different token.");
        }

        if (!session) {
          if (sessionId || !isInitializeRequest(req.body)) {
            return jsonRpcError(res, 400, -32000, "Bad Request: no valid MCP session. Send initialize first.");
          }

          const server = createProtocolServer(() => bridgeConfig);
          const transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: () => randomUUID(),
            onsessioninitialized: (newSessionId) => {
              activeSessions.set(newSessionId, { transport, server, pat });
              log("info", `MCP session initialized: ${newSessionId}`);
            },
          });

          transport.onclose = async () => {
            const sid = transport.sessionId;
            if (sid && activeSessions.has(sid)) {
              activeSessions.delete(sid);
              log("info", `MCP session closed: ${sid}`);
            }
            await safeCloseServer(server);
          };

          await server.connect(transport);
          await transport.handleRequest(req, res, req.body);
          return;
        }

        await session.transport.handleRequest(req, res, req.body);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });

  app.get("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const pat = extractPat(req);
    if (!pat) {
      return jsonRpcError(res, 401, -32001,
        "Unauthorized: send 'Authorization: Bearer edm_pat_...' (create a personal access token in the EDM web UI).");
    }
    const context = buildRequestContext(req, pat, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        const session = sessionId ? activeSessions.get(sessionId) : undefined;
        if (!session) {
          return jsonRpcError(res, 400, -32000, "Bad Request: invalid or missing MCP session id.");
        }
        if (session.pat !== pat) {
          return jsonRpcError(res, 403, -32003, "Forbidden: session belongs to a different token.");
        }
        await session.transport.handleRequest(req, res);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });

  app.delete("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const pat = extractPat(req);
    if (!pat) {
      return jsonRpcError(res, 401, -32001,
        "Unauthorized: send 'Authorization: Bearer edm_pat_...' (create a personal access token in the EDM web UI).");
    }
    const context = buildRequestContext(req, pat, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        const session = sessionId ? activeSessions.get(sessionId) : undefined;
        if (!session) {
          return jsonRpcError(res, 400, -32000, "Bad Request: invalid or missing MCP session id.");
        }
        if (session.pat !== pat) {
          return jsonRpcError(res, 403, -32003, "Forbidden: session belongs to a different token.");
        }
        await session.transport.handleRequest(req, res);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });
```

- Đổi log listen: `` log("info", `mcp-bridge listening on http://${host}:${port}/mcp. Config: ${configPath}`); ``

d) Cập nhật `printHelp()`: thay dòng `Start the HTTP MCP server on /mcp/{userId}.` bằng `Start the HTTP MCP server on /mcp (requires Authorization: Bearer edm_pat_...).`; thay mô tả `\${request.user_token}` thành `PAT (edm_pat_...) extracted from the Authorization header in HTTP mode.`

- [ ] **Step 2: Cập nhật mô tả trong `mcp-bridge/tools.md`**

Trong khối connection `edm`, KHÔNG đổi cấu trúc (giữ `header: X-API-Key`, `value: ${request.user_token}`). Chỉ thêm dòng comment ngay trên khối yaml connection:

```
Lưu ý: từ bản này, `${request.user_token}` là PAT (edm_pat_...) lấy từ header
`Authorization: Bearer ...` của client MCP — không còn là user id trên URL.
```

- [ ] **Step 3: Build + validate**

Run: `cd mcp-bridge && npm run build && node dist/index.js validate ./tools.md`
Expected: compile không lỗi; validate in `Validation OK.`

- [ ] **Step 4: Smoke test 401**

Run (bridge chạy nền rồi curl):
```bash
node dist/index.js & sleep 2
curl -s -X POST http://localhost:5848/mcp -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","method":"initialize","id":1}'
kill %1
```
Expected: JSON có `"code":-32001` và message chứa `edm_pat_`.
(Nếu môi trường thiếu env `EDM_API_URL`, export tạm `EDM_API_URL=http://localhost:5847` trước khi chạy.)

- [ ] **Step 5: Commit**

```bash
git add mcp-bridge/src/index.ts mcp-bridge/tools.md
git commit -m "fix(security): require PAT bearer auth on MCP HTTP endpoint, drop /mcp/{userId}"
```

---

### Task 6: SecretProtector (AES-256-GCM)

**Files:**
- Create: `api/Services/SecretProtector.cs`
- Modify: `api/Program.cs` (validate key + đăng ký DI)
- Modify: `api/appsettings.json` (thêm section Encryption)
- Modify: `docker-compose.yml` (thêm env)
- Create: `tests/ExcelDatasetManager.Tests/SecretProtectorTests.cs`

**Interfaces:**
- Produces: `SecretProtector.Protect(string plaintext) -> string` (format `v1:<base64>`), `SecretProtector.Unprotect(string protectedValue) -> string`. Phase A dùng để mã hoá connection config.

- [ ] **Step 1: Viết test TRƯỚC — `tests/ExcelDatasetManager.Tests/SecretProtectorTests.cs`**

```csharp
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SecretProtectorTests
{
    private static SecretProtector NewProtector(string key) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:MasterKey"] = key })
            .Build());

    private const string ValidKey = "unit-test-master-key-0123456789abcdef";

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = NewProtector(ValidKey);
        const string secret = """{"host":"db.example.com","password":"p@ss"}""";

        var protectedValue = protector.Protect(secret);

        Assert.StartsWith("v1:", protectedValue);
        Assert.DoesNotContain("db.example.com", protectedValue);
        Assert.Equal(secret, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Same_plaintext_produces_different_ciphertexts()
    {
        var protector = NewProtector(ValidKey);
        Assert.NotEqual(protector.Protect("secret"), protector.Protect("secret")); // nonce ngẫu nhiên
    }

    [Fact]
    public void Tampered_ciphertext_throws()
    {
        var protector = NewProtector(ValidKey);
        var value = protector.Protect("secret");
        var bytes = Convert.FromBase64String(value["v1:".Length..]);
        bytes[^1] ^= 0xFF;
        var tampered = "v1:" + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Wrong_key_cannot_unprotect()
    {
        var value = NewProtector(ValidKey).Protect("secret");
        var other = NewProtector("another-master-key-0123456789abcdefgh");

        Assert.ThrowsAny<Exception>(() => other.Unprotect(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void Constructor_rejects_missing_or_short_key(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => NewProtector(key!));
    }

    [Fact]
    public void Unprotect_rejects_values_without_version_prefix()
    {
        var protector = NewProtector(ValidKey);
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect("bm90LXZhbGlk"));
    }
}
```

- [ ] **Step 2: Chạy test để thấy fail**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter SecretProtectorTests`
Expected: FAIL — compile error `SecretProtector` chưa tồn tại.

- [ ] **Step 3: Tạo `api/Services/SecretProtector.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Encrypts small secrets (DB connection configs) at rest using AES-256-GCM.
/// Output format: "v1:" + base64(nonce[12] | ciphertext | tag[16]).
/// Master key comes from Encryption:MasterKey (env EDM_ENCRYPTION_KEY) and is
/// hashed with SHA-256 to derive the fixed-size AES key.
/// </summary>
public sealed class SecretProtector
{
    public const string VersionPrefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SecretProtector(IConfiguration configuration)
    {
        var raw = configuration["Encryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 32)
        {
            throw new InvalidOperationException(
                "Encryption:MasterKey must be a random secret of at least 32 characters. " +
                "Set it via the EDM_ENCRYPTION_KEY environment variable.");
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[NonceSize + cipher.Length + TagSize];
        nonce.CopyTo(payload, 0);
        cipher.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + cipher.Length);

        return VersionPrefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Protected value has an unknown format version.");
        }

        var payload = Convert.FromBase64String(protectedValue[VersionPrefix.Length..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("Protected value is too short to be valid.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var cipher = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
        var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
```

- [ ] **Step 4: Chạy test**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter SecretProtectorTests`
Expected: PASS toàn bộ.

- [ ] **Step 5: Wire vào app**

a) `api/appsettings.json` — thêm section (sau `"Jwt"`):
```json
  "Encryption": {
    "MasterKey": ""
  },
```

b) `api/Program.cs` — trong khối "Configuration validation" (ngay sau validate `connectionString`), thêm:
```csharp
var encryptionKey = builder.Configuration["Encryption:MasterKey"];
if (string.IsNullOrWhiteSpace(encryptionKey) || encryptionKey.Length < 32)
{
    throw new InvalidOperationException(
        "Configuration Encryption:MasterKey must be set to a random secret of at least 32 characters. " +
        "Set it via the EDM_ENCRYPTION_KEY environment variable.");
}
```

c) `api/Program.cs` — trong khối Services, thêm: `builder.Services.AddSingleton<SecretProtector>();`

d) `docker-compose.yml` — trong `api.environment`, thêm dòng: `Encryption__MasterKey: ${EDM_ENCRYPTION_KEY}`

e) `api/appsettings.Development.json` — thêm key dev để chạy local không cần env:
```json
  "Encryption": {
    "MasterKey": "dev-only-master-key-do-not-use-in-prod-0123"
  },
```

- [ ] **Step 6: Build + test toàn bộ**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add api/ tests/ docker-compose.yml
git commit -m "feat: add AES-256-GCM SecretProtector for encrypting stored secrets"
```

---

### Task 7: Forwarded headers + rate limit theo user

**Files:**
- Create: `api/Auth/RateLimitPartitionKey.cs`
- Modify: `api/Program.cs` (forwarded headers, thứ tự middleware, partition key)
- Modify: `docker-compose.yml`
- Create: `tests/ExcelDatasetManager.Tests/RateLimitPartitionKeyTests.cs`

**Interfaces:**
- Produces: `RateLimitPartitionKey.For(HttpContext) -> string` — user id claim nếu đã đăng nhập, ngược lại IP, cuối cùng "unknown".

Bối cảnh: API chạy sau Caddy nhưng không đọc `X-Forwarded-For` → `RemoteIpAddress` luôn là IP của Caddy → mọi user chung một bucket rate-limit. Sửa: bật ForwardedHeaders (chỉ khi có cờ tin proxy) và partition query-limit theo user đã xác thực.

- [ ] **Step 1: Viết test TRƯỚC — `tests/ExcelDatasetManager.Tests/RateLimitPartitionKeyTests.cs`**

```csharp
using System.Net;
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class RateLimitPartitionKeyTests
{
    [Fact]
    public void Uses_user_id_when_authenticated()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "33333333-3333-3333-3333-333333333333")],
            authenticationType: "Test"));

        Assert.Equal("33333333-3333-3333-3333-333333333333", RateLimitPartitionKey.For(context));
    }

    [Fact]
    public void Falls_back_to_ip_when_anonymous()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        Assert.Equal("203.0.113.7", RateLimitPartitionKey.For(context));
    }

    [Fact]
    public void Falls_back_to_unknown_when_no_ip()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("unknown", RateLimitPartitionKey.For(context));
    }
}
```

- [ ] **Step 2: Chạy test để thấy fail**

Run: `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj --filter RateLimitPartitionKeyTests`
Expected: FAIL — compile error `RateLimitPartitionKey` chưa tồn tại.

- [ ] **Step 3: Tạo `api/Auth/RateLimitPartitionKey.cs`**

```csharp
using System.Security.Claims;

namespace ExcelDatasetManager.Api.Auth;

public static class RateLimitPartitionKey
{
    /// <summary>
    /// Partition key for rate limiting: authenticated user id when available,
    /// otherwise the caller IP (requires ForwardedHeaders behind a proxy), otherwise "unknown".
    /// </summary>
    public static string For(HttpContext context) =>
        context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
```

- [ ] **Step 4: Sửa `api/Program.cs`**

a) Thêm using: `using Microsoft.AspNetCore.HttpOverrides;`

b) Ngay SAU dòng `app.UseMiddleware<ExceptionHandlingMiddleware>();`, thêm:

```csharp
// Behind a trusted reverse proxy (Caddy), honor X-Forwarded-* so rate limiting
// and logs see the real client IP. Only enabled explicitly via config.
if (builder.Configuration.GetValue<bool?>("Proxy:TrustForwardedHeaders") == true)
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    forwardedOptions.KnownNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    app.UseForwardedHeaders(forwardedOptions);
}
```

c) Đổi thứ tự middleware — hiện tại là `app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();`. Đổi thành:

```csharp
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
```
(Rate limiter phải chạy SAU authentication để đọc được `ctx.User`.)

d) Trong `builder.Services.AddRateLimiter(...)`, sửa policy `"query"` dùng partition key mới:

```csharp
    options.AddPolicy("query", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPartitionKey.For(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
```
Policy `"auth"` giữ nguyên theo IP (login là request chưa xác thực).

e) `docker-compose.yml` — trong `api.environment` thêm: `Proxy__TrustForwardedHeaders: "true"`

- [ ] **Step 5: Build + test**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add api/ tests/ docker-compose.yml
git commit -m "fix: honor forwarded headers behind proxy and rate-limit queries per user"
```

---

### Task 8: Tách Program.cs

**Files:**
- Create: `api/Auth/JwtCookie.cs`
- Create: `api/Endpoints/AuthEndpoints.cs`
- Create: `api/Endpoints/DatasetEndpoints.cs`
- Create: `api/Endpoints/ApiKeyEndpoints.cs`
- Create: `api/Endpoints/QueryEndpoints.cs`
- Create: `api/wwwroot/download-bridge.html`
- Modify: `api/Program.cs` (rút xuống ~250 dòng: config, DI, middleware, gọi Map*)

**Interfaces:**
- Consumes: mọi service đã đăng ký DI; `RateLimitPartitionKey`, `SecretProtector` từ task trước.
- Produces: `JwtCookie.CookieName`, `JwtCookie.Set(HttpContext, string token)`, `JwtCookie.Clear(HttpContext)`, `JwtCookie.TryGetBearerToken(HttpContext, out string)`, `JwtCookie.IsDownloadPath(PathString)`; extension `MapAuthEndpoints/MapDatasetEndpoints/MapApiKeyEndpoints/MapQueryEndpoints(this WebApplication app)`. Phase A/B/C/D sẽ thêm file endpoints mới theo đúng pattern này.

Quy tắc chung cho task này: đây là refactor DI CHUYỂN — nội dung từng endpoint copy NGUYÊN VĂN từ `api/Program.cs` hiện tại, không đổi logic, không đổi route, không đổi policy. Sau khi chuyển xong, mọi test pass và `dotnet build` sạch.

- [ ] **Step 1: Tạo `api/Auth/JwtCookie.cs`**

```csharp
namespace ExcelDatasetManager.Api.Auth;

public static class JwtCookie
{
    public const string CookieName = "edm_token";

    public static void Set(HttpContext ctx, string token)
    {
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });
    }

    public static void Clear(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/"
        });
    }

    public static bool TryGetBearerToken(HttpContext ctx, out string token)
    {
        token = "";
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        var value = authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = value[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    public static bool IsDownloadPath(PathString path)
    {
        return path.StartsWithSegments("/api/datasets", out var remaining)
               && remaining.Value?.Contains("/download/", StringComparison.OrdinalIgnoreCase) == true;
    }
}
```

- [ ] **Step 2: Tạo `api/wwwroot/download-bridge.html`**

Copy NGUYÊN VĂN chuỗi HTML trong hàm `GetDownloadBridgeHtml()` của `api/Program.cs` hiện tại (từ `<!doctype html>` đến `</html>`) vào file này.

- [ ] **Step 3: Tạo `api/Endpoints/AuthEndpoints.cs`**

```csharp
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        auth.MapPost("/register", async (RegisterRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
        {
            var result = await svc.RegisterAsync(req, ct);
            if (result.Success)
            {
                JwtCookie.Set(ctx, result.Data!.Token);
            }

            return result.Success
                ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
        {
            var result = await svc.LoginAsync(req, ct);
            if (result.Success)
            {
                JwtCookie.Set(ctx, result.Data!.Token);
            }

            return result.Success
                ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        auth.MapGet("/me", async (ClaimsPrincipal principal, AuthService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MeAsync(userId.Value, ct);
            return result.Success
                ? Results.Ok(new { success = true, user = result.Data })
                : Results.Unauthorized();
        }).RequireAuthorization();

        auth.MapPost("/logout", (HttpContext ctx) =>
        {
            JwtCookie.Clear(ctx);
            return Results.Ok(new
            {
                success = true,
                message = "Token issuance is stateless — client should discard its token."
            });
        }).RequireAuthorization();
    }
}
```

- [ ] **Step 4: Tạo `api/Endpoints/DatasetEndpoints.cs`**

Chứa extension `MapDatasetEndpoints(this WebApplication app)`. Copy NGUYÊN VĂN từ Program.cs hiện tại các endpoint sau vào trong method (giữ nguyên policy `RequireAuthorization`/`JwtOnly` từng endpoint):
- `datasets.MapGet("/", ...)` (list)
- `datasets.MapPost("/", ...)` (upload)
- `datasets.MapGet("/{datasetId:guid}", ...)` (detail)
- `datasets.MapPut("/{datasetId:guid}/business-knowledge", ...)`
- `datasets.MapDelete("/{datasetId:guid}", ...)`
- `datasets.MapGet("/{datasetId:guid}/download/original", ...)`
- `datasets.MapGet("/{datasetId:guid}/download/manifest", ...)`

Khung file:

```csharp
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class DatasetEndpoints
{
    public static void MapDatasetEndpoints(this WebApplication app)
    {
        var datasets = app.MapGroup("/api/datasets").RequireAuthorization();

        // ... 7 endpoint copy nguyên văn vào đây ...
    }
}
```

- [ ] **Step 5: Tạo `api/Endpoints/ApiKeyEndpoints.cs`**

Extension `MapApiKeyEndpoints(this WebApplication app)` chứa (copy nguyên văn):
- Nhóm `datasets.MapPost/MapGet/MapDelete .../api-keys...` — tạo lại group: `var datasets = app.MapGroup("/api/datasets").RequireAuthorization();` rồi map 3 endpoint api-keys vào đó.
- Nhóm PAT: `var userKeys = app.MapGroup("/api/user/api-keys").RequireAuthorization("JwtOnly");` với 3 endpoint.

Khung file:

```csharp
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var datasets = app.MapGroup("/api/datasets").RequireAuthorization();

        // ... 3 endpoint dataset api-keys copy nguyên văn ...

        var userKeys = app.MapGroup("/api/user/api-keys").RequireAuthorization("JwtOnly");

        // ... 3 endpoint PAT copy nguyên văn ...
    }
}
```

- [ ] **Step 6: Tạo `api/Endpoints/QueryEndpoints.cs`**

```csharp
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class QueryEndpoints
{
    public static void MapQueryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/datasets/{datasetId:guid}/query",
            async (Guid datasetId, QueryRequest req, ClaimsPrincipal principal, DuckDbQueryService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null && scopedDatasetId != datasetId)
            {
                return Results.Forbid();
            }

            var result = await svc.QueryAsync(userId.Value, datasetId, req, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");
    }
}
```

- [ ] **Step 7: Rút gọn `api/Program.cs`**

a) XOÁ khỏi Program.cs: toàn bộ các endpoint đã chuyển (auth, datasets, api-keys, user api-keys, query), các hàm `SetJwtCookie`, `ClearJwtCookie`, `IsDownloadPath`, `TryGetBearerToken`, `GetDownloadBridgeHtml`, và const `JwtCookieName`.

b) Thêm using: `using ExcelDatasetManager.Api.Endpoints;`

c) Các chỗ từng gọi helper cũ trong middleware inline sửa thành gọi `JwtCookie.*`:
- `TryGetBearerToken(ctx, out var bearerToken)` → `JwtCookie.TryGetBearerToken(ctx, out var bearerToken)`
- `SetJwtCookie(ctx, bearerToken)` → `JwtCookie.Set(ctx, bearerToken)`
- `IsDownloadPath(...)` → `JwtCookie.IsDownloadPath(...)`
- `ctx.Request.Cookies.ContainsKey(JwtCookieName)` → `ctx.Request.Cookies.ContainsKey(JwtCookie.CookieName)`
- Trong JWT bearer options: `context.Request.Cookies.TryGetValue(JwtCookieName, out var token)` → `...TryGetValue(JwtCookie.CookieName, out var token)` và `IsDownloadPath(context.Request.Path)` → `JwtCookie.IsDownloadPath(context.Request.Path)`

d) Middleware download-bridge: thay `await ctx.Response.WriteAsync(GetDownloadBridgeHtml(), ctx.RequestAborted);` bằng:

```csharp
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(
            Path.Combine(app.Environment.WebRootPath, "download-bridge.html"),
            ctx.RequestAborted);
```

e) Cuối file (trước `app.Run();`), thay các endpoint đã xoá bằng:

```csharp
app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "Excel Dataset Manager", version = "1.0" }));

app.MapAuthEndpoints();
app.MapDatasetEndpoints();
app.MapApiKeyEndpoints();
app.MapQueryEndpoints();
```

f) Record `PendingParsingJobRow` và khối DB init giữ nguyên trong Program.cs.

- [ ] **Step 8: Build + test + kiểm tra route**

Run: `dotnet build api/ExcelDatasetManager.Api.csproj && dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj`
Expected: PASS, 0 warnings mới.

Kiểm tra không sót endpoint: `grep -c "Map\(Get\|Post\|Put\|Delete\)" api/Endpoints/*.cs` — tổng phải là 17 (4 auth + 7 dataset + 6 api-key) + 1 query = 18 map calls trong 4 file Endpoints.

- [ ] **Step 9: Commit**

```bash
git add api/
git commit -m "refactor: split Program.cs endpoints into Endpoints/* and extract JwtCookie helper"
```

---

## Definition of Done (toàn Phase 0)

1. `dotnet build api/ExcelDatasetManager.Api.csproj` — 0 error.
2. `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` — tất cả pass (≥ 45 tests).
3. `cd mcp-bridge && npm run build && node dist/index.js validate ./tools.md` — OK.
4. Gửi `X-API-Key: <guid bất kỳ>` vào API → 401 (không còn `insecure_user_id`).
5. `POST /mcp` không có Authorization → 401 với hướng dẫn tạo PAT.
6. Migration: khởi động app trên DB trống tạo đủ schema; khởi động lại không lỗi (idempotent + schema_migrations ghi version 1).
