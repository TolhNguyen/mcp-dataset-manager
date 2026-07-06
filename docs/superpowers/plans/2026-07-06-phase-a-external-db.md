# Phase A — External Database Connections (Live Query) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Người dùng cấu hình kết nối MySQL/MSSQL/PostgreSQL/BigQuery, chọn bảng expose cho AI; hệ thống chỉ lưu metadata schema + 2 dòng mẫu/bảng; mọi query của AI chạy LIVE trên DB nguồn qua lớp read-only enforcement.

**Architecture:** `IExternalDbConnector` + 4 implementation. Dataset external tái dùng `datasets`/`dataset_tables`/`dataset_columns` (không Parquet). Query endpoint route theo `datasets.source_kind`: `file` → DuckDbQueryService (không đổi), `external_db` → ExternalQueryService (validator theo dialect → row cap → connector → token budget + query_logs như cũ). Connection config mã hoá bằng SecretProtector (Phase 0).

**Tech Stack:** .NET 8, Dapper, NuGet mới: `MySqlConnector`, `Microsoft.Data.SqlClient`, `Google.Cloud.BigQuery.V2`. UI vanilla JS.

**Spec:** `docs/superpowers/specs/2026-07-06-big-update-design.md` mục "Sub-project A".

## Global Constraints

- **TUYỆT ĐỐI KHÔNG lưu dữ liệu người dùng** ngoài: config mã hoá, metadata schema, 2 dòng mẫu/bảng (mỗi cell cắt ≤200 ký tự), query logs (chỉ SQL text). Không ghi kết quả query xuống đĩa/DB.
- Response API **không bao giờ** chứa password/service-account JSON — kể cả dạng mã hoá.
- Mặc định an toàn: timeout query 30s (`ExternalQuery:TimeoutSeconds`), row cap dùng chung `Query:DefaultLimit`/`Query:HardMaxRows`, tối đa 3 query đồng thời/connection (`ExternalQuery:MaxConcurrentPerConnection`).
- Sau MỖI task: `dotnet build api/ExcelDatasetManager.Api.csproj` và `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` pass rồi mới commit. Commit message prefix `feat:/fix:/test:/refactor:`.
- KHÔNG đụng Phase B/C/D (knowledge, context API, dashboards, multi-dataset join).
- Provider string chuẩn (dùng nhất quán mọi nơi): `"postgresql" | "mysql" | "mssql" | "bigquery"`.

---

### Task 1: Migration 0003 + giới hạn dataset per-user

**Files:**
- Create: `api/Migrations/0003_external_connections.sql`
- Modify: `api/Services/DatasetService.cs` (per-user max_datasets)
- Test: cập nhật `tests/ExcelDatasetManager.Tests/MigrationScriptLoaderTests.cs`

**Interfaces:**
- Produces: schema mới (bảng `db_connections`; cột `datasets.source_kind/connection_id/external_tables/include_samples/schema_refreshed_at`; `dataset_tables.sample_rows`; `users.max_datasets`). `DatasetService.GetMaxDatasetsAsync(conn, userId)` (private helper) — limit đọc từ `users.max_datasets`.

**Step 1.1 — Tạo `api/Migrations/0003_external_connections.sql`:**

```sql
CREATE TABLE IF NOT EXISTS db_connections (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    provider VARCHAR(20) NOT NULL,
    encrypted_config TEXT NOT NULL,
    last_test_status VARCHAR(20),
    last_test_at TIMESTAMPTZ,
    last_test_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_db_connections_user ON db_connections(user_id);

ALTER TABLE datasets
    ADD COLUMN IF NOT EXISTS source_kind VARCHAR(20) NOT NULL DEFAULT 'file',
    ADD COLUMN IF NOT EXISTS connection_id UUID REFERENCES db_connections(id),
    ADD COLUMN IF NOT EXISTS external_tables JSONB,
    ADD COLUMN IF NOT EXISTS include_samples BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS schema_refreshed_at TIMESTAMPTZ;

ALTER TABLE dataset_tables
    ADD COLUMN IF NOT EXISTS sample_rows JSONB;

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS max_datasets INT NOT NULL DEFAULT 10;
```

**Step 1.2 — `DatasetService`:** thay mọi chỗ dùng const `MaxDatasetsPerUser` bằng giá trị đọc từ DB:
- Thêm helper: `private static Task<int> GetMaxDatasetsAsync(NpgsqlConnection conn, Guid userId) => conn.ExecuteScalarAsync<int>("SELECT max_datasets FROM users WHERE id = @UserId", new { UserId = userId });`
- `ListAsync`: đọc max trước khi build `DatasetLimit` (thay `MaxDatasetsPerUser`).
- `UploadAsync`: trong transaction (sau advisory lock), đọc max rồi so `count >= max`; message lỗi dùng giá trị max động.
- Giữ const `MaxDatasetsPerUser = 10` làm fallback KHÔNG dùng nữa → xoá const, sửa chỗ tham chiếu.

**Step 1.3 — Test:** trong `MigrationScriptLoaderTests` thêm:

```csharp
[Fact]
public void Loads_external_connections_migration()
{
    var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);
    var m3 = scripts.Single(s => s.Version == 3);
    Assert.Contains("CREATE TABLE IF NOT EXISTS db_connections", m3.Sql);
    Assert.Contains("max_datasets", m3.Sql);
}
```

**Step 1.4 — Build + test → PASS. Commit:** `feat: add external connections schema and per-user dataset limit`

---

### Task 2: Connector abstractions + config model

**Files:**
- Create: `api/Services/Connectors/ExternalDbTypes.cs`
- Create: `api/Services/Connectors/DbConnectionConfig.cs`
- Create: `api/Services/Connectors/IExternalDbConnector.cs`
- Test: `tests/ExcelDatasetManager.Tests/DbConnectionConfigTests.cs`

**Interfaces (Produces — mọi task sau dùng đúng các kiểu này):**

```csharp
// ExternalDbTypes.cs
namespace ExcelDatasetManager.Api.Services.Connectors;

public static class ExternalDbProviders
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string MsSql = "mssql";
    public const string BigQuery = "bigquery";
    public static readonly string[] All = [PostgreSql, MySql, MsSql, BigQuery];
    public static bool IsValid(string? provider) => provider is not null && All.Contains(provider);
}

public record ExternalColumnInfo(string Name, string DataType, bool IsNullable);
public record ExternalTableInfo(string QueryableName, string SourceLabel, List<ExternalColumnInfo> Columns);
// QueryableName = tên AI dùng trực tiếp trong SQL (vd: "public.orders", "dbo.Orders", "orders", "my_dataset.orders").
// SourceLabel   = nhãn hiển thị cho user (schema.table gốc).

public record ExternalQueryResult(
    List<(string Name, string Type)> Columns,
    List<object?[]> Rows,
    bool Truncated);

public record ConnectorTestResult(bool Success, string? Error, string? Warning);
// Warning: vd "Tài khoản có vẻ có quyền ghi — nên dùng tài khoản chỉ SELECT."
```

```csharp
// DbConnectionConfig.cs — parse/validate JSON config theo provider
namespace ExcelDatasetManager.Api.Services.Connectors;

public record DbConnectionConfig(
    string Provider,
    string? Host, int? Port, string? Database, string? Username, string? Password, bool Ssl,
    // BigQuery:
    string? ProjectId, string? BigQueryDataset, string? ServiceAccountJson, long? MaxBytesBilled)
{
    public static (DbConnectionConfig? Config, string? Error) Parse(string provider, JsonElement raw);
    public string ToJson();                       // serialize để mã hoá
    public static DbConnectionConfig FromJson(string json);
}
```

Quy tắc validate trong `Parse` (trả Error mô tả field thiếu):
- `postgresql`/`mysql`/`mssql`: bắt buộc `host`, `database`, `username`, `password`; `port` mặc định 5432/3306/1433; `ssl` mặc định true.
- `bigquery`: bắt buộc `project_id`, `dataset`, `service_account_json` (phải là JSON hợp lệ có field `client_email`); `max_bytes_billed` mặc định 1_073_741_824 (1GB).
- Provider ngoài danh sách → Error.

```csharp
// IExternalDbConnector.cs
namespace ExcelDatasetManager.Api.Services.Connectors;

public interface IExternalDbConnector
{
    string Provider { get; }
    Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct);
    Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct);
    /// <summary>2 dòng mẫu của 1 bảng; mỗi cell ToString() cắt 200 ký tự; lỗi → trả list rỗng, không throw.</summary>
    Task<List<object?[]>> GetSampleRowsAsync(DbConnectionConfig config, string queryableName, CancellationToken ct);
    /// <summary>Chạy SQL ĐÃ validate + ĐÃ wrap row cap. Session read-only nếu provider hỗ trợ.</summary>
    Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct);
}
```

**Steps:** viết test validate config trước (case: thiếu host, thiếu password, bigquery thiếu project_id, service_account_json không phải JSON, provider lạ, port mặc định đúng theo provider, round-trip ToJson/FromJson) → fail → implement → pass → commit `feat: add external connector abstractions and config validation`.

---

### Task 3: ExternalQueryGuard — validator theo dialect + row cap (LỚP AN NINH — task quan trọng nhất)

**Files:**
- Create: `api/Services/Connectors/ExternalQueryGuard.cs`
- Test: `tests/ExcelDatasetManager.Tests/ExternalQueryGuardTests.cs`

**Interfaces:**
- Consumes: `QueryValidator.StripStringsAndComments` (đổi từ `internal static` thành `public static` — sửa 1 từ khoá trong `api/Services/QueryValidator.cs`).
- Produces:
  - `ExternalQueryGuard.Validate(string? sql, string provider) -> QueryValidationResult` (tái dùng record của QueryValidator)
  - `ExternalQueryGuard.ApplyRowCap(string sql, string provider, int maxRows) -> string`

**Logic Validate** (giống QueryValidator: strip strings/comments → single statement → token đầu SELECT/WITH → blocklist token theo provider):

```csharp
private static readonly string[] CommonForbidden =
{
    "insert", "update", "delete", "drop", "alter", "create", "truncate",
    "grant", "revoke", "merge", "call", "execute", "exec", "replace"
};

private static readonly Dictionary<string, string[]> ProviderForbidden = new()
{
    [ExternalDbProviders.PostgreSql] = ["copy", "do", "set", "lock", "listen", "notify", "vacuum", "reindex", "cluster", "prepare", "deallocate", "pg_read_file", "pg_sleep", "dblink", "lo_import", "lo_export"],
    [ExternalDbProviders.MySql]      = ["set", "use", "load", "outfile", "dumpfile", "handler", "lock", "unlock", "install", "uninstall", "benchmark", "sleep", "load_file"],
    [ExternalDbProviders.MsSql]      = ["set", "use", "into", "bulk", "openrowset", "openquery", "opendatasource", "waitfor", "dbcc", "shutdown", "kill", "reconfigure", "backup", "restore", "xp_cmdshell", "sp_executesql"],
    [ExternalDbProviders.BigQuery]   = ["export", "load", "assert", "begin", "commit", "rollback", "declare", "set"]
};
```
Thêm rule riêng MSSQL: reject nếu scrubbed SQL match regex `\b(xp|sp)_[a-z0-9_]+` (mọi extended/system proc).

**Logic ApplyRowCap:**
- `postgresql` / `mysql` / `bigquery`: nếu scrubbed SQL kết thúc bằng `\blimit\s+\d+\s*(offset\s+\d+\s*)?$` (case-insensitive) → giữ nguyên; ngược lại wrap: `SELECT * FROM (  {sql}  ) AS _edm_q LIMIT {maxRows}` (MySQL yêu cầu alias — đã có).
- `mssql`:
  - Nếu scrubbed có `\bselect\s+top\b` ngay đầu hoặc kết thúc `fetch\s+next\s+\d+\s+rows\s+only\s*$` → giữ nguyên.
  - Nếu kết thúc bằng mệnh đề `order by` top-level (regex `\border\s+by\s+[^)]+$` trên scrubbed) → APPEND `` OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY`` (T-SQL không cho ORDER BY trong derived table).
  - Ngược lại → wrap `SELECT TOP ({maxRows}) * FROM (  {sql}  ) AS _edm_q`.

**Test bắt buộc (viết TRƯỚC, tối thiểu):**

```csharp
// Cho phép: SELECT thường, WITH, keyword cấm trong string literal, trailing semicolon — cho cả 4 provider (Theory lồng provider)
// Chặn (mỗi provider): INSERT/UPDATE/DELETE/DROP/CREATE/MERGE, 2 statements
// Chặn riêng postgresql: "COPY t TO '/tmp/x'", "SELECT pg_sleep(10)", "DO $$ ... $$", "SET work_mem='1GB'"
// Chặn riêng mysql:      "SELECT * FROM t INTO OUTFILE '/tmp/x'", "LOAD DATA INFILE ...", "SELECT load_file('/etc/passwd')", "SELECT benchmark(1000000, sha1('x'))"
// Chặn riêng mssql:      "SELECT * INTO newtable FROM t", "EXEC xp_cmdshell 'dir'", "SELECT * FROM OPENROWSET(...)", "WAITFOR DELAY '0:0:10'", "EXEC sp_executesql N'...'"
// Chặn riêng bigquery:   "EXPORT DATA OPTIONS(...) AS SELECT 1", "BEGIN TRANSACTION", "DECLARE x INT64"
// RowCap:
//   pg/mysql/bq: wrap khi không có LIMIT; giữ nguyên khi có "LIMIT 5" / "LIMIT 5 OFFSET 2" cuối; wrap khi LIMIT chỉ nằm trong subquery
//   mssql: "SELECT * FROM t" -> "SELECT TOP (100) * FROM ( SELECT * FROM t ) AS _edm_q"
//          "SELECT * FROM t ORDER BY x" -> kết thúc bằng "OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY"
//          "SELECT TOP 5 * FROM t" -> giữ nguyên; "... FETCH NEXT 5 ROWS ONLY" -> giữ nguyên
```

**Commit:** `feat: add dialect-aware read-only SQL guard for external databases` (kèm sửa `StripStringsAndComments` thành public).

---

### Task 4: PostgresDbConnector + MySqlDbConnector

**Files:**
- Modify: `api/ExcelDatasetManager.Api.csproj` — thêm `<PackageReference Include="MySqlConnector" Version="2.3.7" />`
- Create: `api/Services/Connectors/PostgresDbConnector.cs`
- Create: `api/Services/Connectors/MySqlDbConnector.cs`

**Interfaces:** implement `IExternalDbConnector` (Task 2). Verify bằng build + e2e Task 10 (không unit test được vì cần DB thật).

**Yêu cầu hành vi chung cả 2 connector:**
- Connection string build từ config; `Timeout`/`ConnectionTimeout` 10s; KHÔNG pooling tuỳ chỉnh (mặc định).
- `TestAsync`: mở connection + `SELECT 1`. Kiểm tra quyền ghi best-effort → set `Warning` nếu phát hiện có quyền ghi:
  - PG: `SELECT bool_or(privilege_type IN ('INSERT','UPDATE','DELETE')) FROM information_schema.role_table_grants WHERE grantee = current_user` (null → false).
  - MySQL: `SHOW GRANTS` → nếu chuỗi chứa `ALL PRIVILEGES` / `INSERT` / `UPDATE` / `DELETE` → warning.
- `ListTablesAsync`:
  - PG: `information_schema.tables` where `table_type IN ('BASE TABLE','VIEW') AND table_schema NOT IN ('pg_catalog','information_schema')`; QueryableName = `table_schema='public' ? table_name : $"{table_schema}.{table_name}"`; columns từ `information_schema.columns` (data_type, is_nullable).
  - MySQL: `information_schema.tables WHERE table_schema = DATABASE()`; QueryableName = table_name.
- `GetSampleRowsAsync`: `SELECT * FROM {queryableName} LIMIT 2` — queryableName phải quote-safe: từng phần identifier quote bằng `"` (PG) / `` ` `` (MySQL), reject nếu chứa ký tự ngoài `[A-Za-z0-9_.$]`. Cell → `value?.ToString()`, cắt 200 ký tự. Mọi exception → log warning + trả `[]`.
- `ExecuteQueryAsync`:
  - PG: connection string thêm `Options=-c default_transaction_read_only=on`; đọc reader → columns (`GetName`, `GetDataTypeName`), rows (DBNull → null; DateTime → `ToString("O")`; decimal giữ nguyên), dừng ở maxRows+ đặt `Truncated = rows.Count >= maxRows`; `cmd.CommandTimeout = timeoutSeconds`.
  - MySQL: sau khi mở, `SET SESSION TRANSACTION READ ONLY;` rồi chạy query tương tự.

**Commit:** `feat: add PostgreSQL and MySQL live-query connectors`

---

### Task 5: MsSqlDbConnector + BigQueryDbConnector

**Files:**
- Modify: `api/ExcelDatasetManager.Api.csproj` — thêm `Microsoft.Data.SqlClient` `5.2.2`, `Google.Cloud.BigQuery.V2` `3.9.0`
- Create: `api/Services/Connectors/MsSqlDbConnector.cs`
- Create: `api/Services/Connectors/BigQueryDbConnector.cs`

**MSSQL:** connection string `Encrypt` theo `Ssl`, `TrustServerCertificate=true` (self-host phổ biến); `ListTables`: `INFORMATION_SCHEMA.TABLES`, QueryableName = `$"{TABLE_SCHEMA}.{TABLE_NAME}"`; sample: `SELECT TOP 2 * FROM {name}`; không có session read-only → chỉ dựa guard (ghi rõ comment). Identifier quote `[]`, cùng quy tắc ký tự như Task 4.

**BigQuery:** `BigQueryClient.Create(projectId, GoogleCredential.FromJson(serviceAccountJson))`; `ListTablesAsync`: `client.ListTables(BigQueryDataset)` + `GetTable` lấy schema fields (type, mode → nullable khi mode != "REQUIRED"); QueryableName = `$"{BigQueryDataset}.{tableId}"`; sample: query `SELECT * FROM {name} LIMIT 2`; `ExecuteQueryAsync`: `QueryOptions { MaximumBytesBilled = config.MaxBytesBilled, UseLegacySql = false }`; timeout qua `GetQueryResultsOptions`. Test connection = chạy `SELECT 1`.

**Commit:** `feat: add SQL Server and BigQuery live-query connectors`

---

### Task 6: DbConnectionService + SchemaBootstrap + manifest cho dataset external

**Files:**
- Create: `api/Services/DbConnectionService.cs`
- Create: `api/Services/ExternalSchemaService.cs`
- Test: `tests/ExcelDatasetManager.Tests/ExternalManifestTests.cs`

**Interfaces:**
- Consumes: `SecretProtector`, connectors (resolve qua DI: đăng ký cả 4 connector là singleton `IExternalDbConnector`, service nhận `IEnumerable<IExternalDbConnector>` và chọn theo `Provider`).
- Produces:
  - `DbConnectionService`: `CreateAsync(userId, name, provider, JsonElement config)`, `ListAsync(userId)`, `UpdateAsync(userId, id, name?, JsonElement? config)`, `DeleteAsync(userId, id)` (fail `CONNECTION_IN_USE` nếu còn dataset tham chiếu), `TestAsync(userId, id)` (lưu last_test_*), `GetConfigAsync(userId, id) -> DbConnectionConfig?` (dùng nội bộ, giải mã), `ListRemoteTablesAsync(userId, id)`.
  - `ExternalSchemaService`: `CreateExternalDatasetAsync(userId, connectionId, name, string[] tables, bool includeSamples)` và `RefreshSchemaAsync(userId, datasetId)`.
  - `ExternalManifestBuilder.Build(datasetName, provider, tables) -> string` (static, pure — testable).

**Masking (bắt buộc test):** response list/get connection trả `new { id, name, provider, host_masked, database, username, last_test_status, last_test_at, last_test_error, created_at }`; `host_masked` = 3 ký tự đầu + `"***"` (BigQuery: `project_id` 3 ký tự đầu + `***`). KHÔNG field nào chứa password/serviceAccountJson.

**CreateExternalDatasetAsync flow:**
1. Đọc config (giải mã), check limit `users.max_datasets` (advisory lock giống UploadAsync).
2. `ListTablesAsync` từ connector → lọc theo `tables` user chọn (validate tên có tồn tại; giới hạn `ExternalQuery:MaxTablesPerDataset` mặc định 50).
3. Insert `datasets` (source_kind='external_db', connection_id, external_tables=JSONB danh sách tên, status='ready', file_type=provider, original_file_name=connection name, stored_file_name='', file_size_bytes=0, manifest_file_name='manifest.md').
4. Với mỗi bảng: insert `dataset_tables` (table_name=QueryableName, source_name=SourceLabel, source_type='external_'+provider, data_file_name='', row_count=0, sample_rows JSONB nếu includeSamples) + `dataset_columns` (normalized_name=tên cột gốc, inferred_type=DataType, ordinal).
5. Sinh manifest: `ExternalManifestBuilder.Build(...)` → ghi file qua `FileStorageService.GetManifestPath` (EnsureDatasetDirectories trước).
6. `schema_refreshed_at = NOW()`.

`RefreshSchemaAsync`: xoá `dataset_tables` của dataset (cascade columns) → chạy lại bước 2/4/5/6 với danh sách `external_tables` đã lưu.

**Manifest external (Build) phải chứa:** tên dataset, provider + **dialect ghi rõ** (vd "Write MySQL dialect SQL"), câu cảnh báo "Queries run LIVE against the source database — always prefer aggregates and LIMIT", danh sách bảng: QueryableName (ghi rõ "use this exact name in SQL"), cột (tên, kiểu, nullable), 2 sample rows dạng bảng MD. Test: build với 1 bảng 2 cột + samples → assert chứa các phần trên.

**Commit:** `feat: add connection management, schema bootstrap and external dataset creation`

---

### Task 7: ConnectionEndpoints + wiring

**Files:**
- Create: `api/Endpoints/ConnectionEndpoints.cs`
- Modify: `api/Program.cs` (DI + Map), `api/Models/Contracts.cs` (DTOs), `api/appsettings.json` (section `ExternalQuery`)

**Routes (tất cả `RequireAuthorization("JwtOnly")`):**
```
POST   /api/connections                    body {name, provider, config{...}}
GET    /api/connections
PUT    /api/connections/{id}               body {name?, config?}   (config gửi kèm mới ghi đè)
DELETE /api/connections/{id}
POST   /api/connections/{id}/test
GET    /api/connections/{id}/tables
POST   /api/connections/{id}/datasets      body {name, tables[], include_samples}
POST   /api/datasets/{id}/refresh-schema
```
DTOs: `CreateConnectionRequest(string? Name, string? Provider, JsonElement? Config)`, `UpdateConnectionRequest(string? Name, JsonElement? Config)`, `CreateExternalDatasetRequest(string? Name, string[]? Tables, bool? IncludeSamples)`.

DI trong Program.cs:
```csharp
builder.Services.AddSingleton<IExternalDbConnector, PostgresDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, MySqlDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, MsSqlDbConnector>();
builder.Services.AddSingleton<IExternalDbConnector, BigQueryDbConnector>();
builder.Services.AddScoped<DbConnectionService>();
builder.Services.AddScoped<ExternalSchemaService>();
builder.Services.AddScoped<ExternalQueryService>();   // Task 8 — thêm ở Task 8 nếu chưa có
app.MapConnectionEndpoints();
```
appsettings `ExternalQuery`: `{ "TimeoutSeconds": 30, "MaxConcurrentPerConnection": 3, "MaxTablesPerDataset": 50 }`.

**Commit:** `feat: add connection management endpoints`

---

### Task 8: ExternalQueryService + route query theo source_kind

**Files:**
- Create: `api/Services/ExternalQueryService.cs`
- Modify: `api/Endpoints/QueryEndpoints.cs`
- Test: `tests/ExcelDatasetManager.Tests/ConnectionConcurrencyLimiterTests.cs`

**Interfaces:**
- Produces: `ExternalQueryService.QueryAsync(Guid userId, DatasetRecord dataset, QueryRequest request, CancellationToken) -> Task<object>` — response shape GIỐNG HỆT DuckDbQueryService (success/dataset_id/query_id/status/result compact_table/execution/sql/ai_budget/error) nhưng `execution.engine = provider`. Token budget (`AiTokenBudgetService.Decide`) + `query_logs` + summary mode: copy pattern từ `DuckDbQueryService` (được phép trích các private helper thành shared nếu gọn).
- `ConnectionConcurrencyLimiter` (static class, testable): `TryEnterAsync(Guid connectionId, int max, TimeSpan wait) -> Task<IDisposable?>` — `ConcurrentDictionary<Guid, SemaphoreSlim>`; null khi hết slot → error `TOO_MANY_CONCURRENT_QUERIES` (retryable).

**Flow QueryAsync:** `ExternalQueryGuard.Validate(sql, provider)` → fail: log + error response. → `ApplyRowCap` với `Math.Clamp(request.Options?.MaxRows ?? DefaultLimit, 1, HardMaxRows)` → limiter → giải mã config → `connector.ExecuteQueryAsync` → build result + budget + log. Exception từ connector: map `error.code = "EXTERNAL_QUERY_FAILED"`, message = exception message (KHÔNG kèm connection string), retryable=false.

**QueryEndpoints:** load `DatasetRecord` (đã có `GetDatasetRecordAsync`; cần SELECT thêm cột mới — cập nhật `DatasetService.SelectDatasetSql` + record `DatasetRecord` thêm `SourceKind`, `ConnectionId`) → `dataset.SourceKind == "external_db" ? externalSvc.QueryAsync(...) : duckDbSvc.QueryAsync(...)`. GIỮ nguyên check scoped dataset key.

**Test limiter:** max 2 → 2 lần Enter ok, lần 3 (wait 100ms) null; dispose 1 → Enter lại ok.

**Commit:** `feat: route dataset queries to live external databases with concurrency guard`

---

### Task 9: UI — connections + wizard + badge

**Files:**
- Create: `api/wwwroot/connections.html`, `api/wwwroot/js/connections.js`
- Modify: `api/wwwroot/dashboard.html` (nav link "Kết nối DB"), `api/wwwroot/js/dashboard.js` (badge nguồn), `api/Services/DatasetService.cs` (`ListAsync` trả thêm `source_kind`)

**connections.html** (theo pattern dashboard.html hiện có: cùng css/style.css, auth.js check login):
- Bảng connection: tên, provider, host masked, trạng thái test (badge xanh/đỏ + lỗi), nút Test / Sửa / Xoá.
- Nút "+ Thêm kết nối" → modal form: chọn provider (4 nút radio) → field theo provider (pg/mysql/mssql: host, port, database, username, password, checkbox SSL; bigquery: project_id, dataset, textarea service_account_json, max_bytes_billed). **Khuyến cáo đậm đầu form:** "⚠️ Hãy dùng tài khoản CHỈ có quyền SELECT (read-only). EDM không bao giờ ghi vào DB của bạn, nhưng tài khoản read-only là lớp bảo vệ quan trọng nhất."
- Sau khi tạo → tự gọi test → hiện kết quả (kèm warning quyền ghi nếu có).
- Nút "Tạo dataset từ kết nối" trên mỗi row → wizard: gọi `GET /api/connections/{id}/tables` → checkbox list (tên + số cột), input tên dataset, checkbox "Lưu 2 dòng dữ liệu mẫu/bảng làm gợi ý cho AI (tắt nếu dữ liệu nhạy cảm)" mặc định bật → POST tạo → chuyển về dashboard.
- Ghi chú dưới wizard: "EDM chỉ lưu cấu trúc bảng và 2 dòng mẫu. Dữ liệu của bạn không được sao chép — AI truy vấn trực tiếp DB nguồn."

**dashboard.js:** cột nguồn: dataset.source_kind === 'external_db' → badge `🗄 {file_type}` (file_type = provider), ngược lại `📄 {file_type}`.

**Commit:** `feat: add connections management UI and external dataset wizard`

---

### Task 10: E2E smoke với MySQL + PostgreSQL thật (verification, không commit code)

Dựng nguồn dữ liệu giả lập + chạy full flow:

```bash
# 1. Nguồn: MySQL + Postgres chứa bảng mẫu
docker run -d --rm --name edm-src-mysql -e MYSQL_ROOT_PASSWORD=src -e MYSQL_DATABASE=shop -p 53306:3306 mysql:8
docker run -d --rm --name edm-src-pg -e POSTGRES_PASSWORD=src -e POSTGRES_DB=shop -p 55434:5432 postgres:16-alpine
# 2. Postgres metadata của EDM + API (như smoke Phase 0b, port 55853)
# 3. Seed nguồn:
docker exec edm-src-mysql mysql -uroot -psrc shop -e "CREATE TABLE orders(id INT PRIMARY KEY, revenue DECIMAL(12,2), created DATE); INSERT INTO orders VALUES (1, 1200000, '2026-01-03'),(2, 450000, '2026-01-04'),(3, 900000, '2026-02-01');"
docker exec edm-src-pg psql -U postgres -d shop -c "CREATE TABLE customers(id INT PRIMARY KEY, name TEXT); INSERT INTO customers VALUES (1,'Anh Ba'),(2,'Chị Tư');"
# 4. Flow qua curl (JWT từ /api/auth/register):
#    - POST /api/connections (mysql, host=localhost port=53306...) → test → 200 (chấp nhận warning quyền ghi vì dùng root)
#    - GET tables → thấy "orders"
#    - POST datasets từ connection (tables=["orders"], include_samples=true) → dataset ready
#    - GET /api/datasets/{id}/download/manifest → chứa "mysql", "orders", sample rows
#    - POST query: "SELECT SUM(revenue) AS total, DATE_FORMAT(created,'%Y-%m') AS month FROM orders GROUP BY month ORDER BY month" → success, 2 rows
#    - POST query "INSERT INTO orders VALUES (9,1,'2026-01-01')" → error NON_READONLY_SQL
#    - POST query "SELECT * FROM orders" không LIMIT → executed SQL có LIMIT wrap, row_count=3
#    - Lặp tương tự cho postgres connection (customers, dialect postgresql)
# 5. Kiểm chứng KHÔNG lưu dữ liệu: psql vào DB metadata — bảng dataset_tables chỉ có sample_rows 2 dòng; không bảng nào chứa rows nguồn; thư mục storage của dataset external không có parquet.
```

**Definition of Done (toàn Phase A):**
1. Build 0 error; test suite pass (≥95 tests — thêm ~25 test guard/config/limiter/manifest).
2. E2E MySQL + PostgreSQL: tạo connection → test → wizard → query live thành công.
3. INSERT/UPDATE/DELETE/COPY/OUTFILE/xp_ bị chặn ở validator cho cả 4 dialect (qua unit tests) và e2e (mysql+pg).
4. Query không LIMIT bị wrap row cap đúng dialect.
5. Response connection không chứa bất kỳ secret nào (grep response JSON không có password).
6. Manifest external chứa dialect + QueryableName + sample rows; toggle include_samples=false → không có sample nào trong DB/manifest.
7. Storage: dataset external KHÔNG có file parquet; chỉ manifest.md.
8. Xoá connection đang có dataset → lỗi CONNECTION_IN_USE; xoá dataset external → không đụng DB nguồn.
