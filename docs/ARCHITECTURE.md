# Architecture

## Tổng quan

```
            ┌──────────────┐
 Browser ──>│  ASP.NET 8   │──────> PostgreSQL (metadata, users, query logs, api keys)
            │  minimal API │
            └──┬───────────┘
               │ enqueue
               ▼
        ┌──────────────────┐
        │ ParsingHosted    │  background BackgroundService consuming a Channel<ParsingJob>
        │ Service          │
        └──┬───────────────┘
           │
           ▼
   FileParserService ──> CSV trung gian ──> ParquetWriter (DuckDB COPY) ──> Parquet
           │
           ▼
   ManifestGenerator ──> manifest.md
```

Query path:

```
Browser/Claude ──> /api/datasets/{id}/query
                          │
                          ▼
                  QueryValidator   (read-only enforcement)
                          │
                          ▼
                  DuckDbQueryService
                          │
                          ├─ in-memory DuckDB
                          ├─ CREATE VIEW per table from parquet
                          ├─ SET memory_limit, statement_timeout
                          ▼
                  ExecuteReader → JSON response (compact_table)
```

## Storage layout

```
storage/
└── users/{user_id}/
    └── datasets/{dataset_id}/
        ├── original_file.xlsx          # File gốc người dùng tải lên
        ├── manifest.md                  # Sinh sau khi parse xong
        ├── parquet/
        │   ├── raw_sheet1.parquet
        │   └── raw_sheet2.parquet
        └── _tmp/                         # CSV trung gian — bị xoá sau khi convert
            └── raw_sheet1.csv
```

Mỗi user ở một sub-tree riêng → không có ai đọc nhầm dữ liệu của ai khác (đường dẫn không thể truy ngược từ URL nếu controller không cho).

## Background processing

`POST /api/datasets`:

1. Validate file (size, extension).
2. `BEGIN TRANSACTION`
3. `SELECT pg_advisory_xact_lock(hashtextextended(user_id::text, 0))` — chỉ giữ trong transaction.
4. `SELECT COUNT(*) FROM datasets WHERE user_id = ?` — nếu ≥ 10 thì rollback, trả lỗi `DATASET_LIMIT_REACHED`.
5. Lưu file gốc xuống disk.
6. `INSERT datasets (..., status='processing')`.
7. `COMMIT`.
8. `parsingQueue.EnqueueAsync(...)` — `Channel<ParsingJob>` bounded 100, full-mode = wait.
9. Trả `202`-style response với `status: processing` về client.

`ParsingHostedService` loop:

1. `await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))`
2. Tạo scope mới (vì DI service phần lớn scoped). Lấy `NpgsqlDataSource`, `FileParserService`, ...
3. Re-read dataset row → nếu status ≠ `processing` thì bỏ qua (đề phòng job lặp).
4. `parser.ParseAsync(...)` → list `ParsedTable` + CSV trung gian.
5. Với mỗi table: `parquetWriter.WriteParquet(tempCsv, parquet)`.
6. `BEGIN`: insert `dataset_tables` + `dataset_columns`, update `datasets SET status='ready'`. `COMMIT`.
7. Generate manifest.md.
8. Dọn `_tmp/`.

Nếu bước nào throw: `UPDATE datasets SET status='failed', error_message=...`. User vẫn thấy được dataset trong list nhưng kèm thông báo lỗi.

## Type inference (TypeInferrer.cs)

Mỗi cell value được test 3 kiểu độc lập:

- **`LooksLikeNumber`**: `decimal.TryParse(InvariantCulture, NumberStyles.Number | AllowLeadingSign)`.
- **`LooksLikeDate`**: bắt buộc `length >= 8` và có separator (`-`, `/`, `.`, `:`, ` `, `T`), rồi `TryParseExact` với danh sách format cố định (`yyyy-MM-dd`, `dd/MM/yyyy`, ...). Loại được "12" hay "2024" bị nhận nhầm là date.
- **`LooksLikeBoolean`**: chỉ match tập cố định (`true/false/yes/no/y/n/có/không/co/khong`).

`Reduce(...)`:

- Trả `"boolean_candidate"` chỉ khi **mọi non-null** đều là boolean **và** không có giá trị string nào (tránh cột `0/1` numeric bị tag là boolean).
- `"number_candidate"` / `"date_candidate"` nếu ≥ 95% non-null đáp ứng.
- Còn lại → `"string"`.

## Query validation

`QueryValidator.StripStringsAndComments(sql)` trả về SQL với các vùng `'...'`, `-- ...`, `/* ... */` đã được thay bằng spaces. Sau đó:

1. Multiple statements: `;` ngoài chuỗi/comment → reject.
2. Token đầu tiên phải là `SELECT` hoặc `WITH`.
3. Token cấm: `insert`, `update`, `delete`, `drop`, `alter`, `create`, `truncate`, `attach`, `detach`, `copy`, `pragma`, `call`, `execute`, `read_csv`, `read_csv_auto`, `read_parquet`, `read_json`, `httpfs`, `install`, `load`, `set`.

`ApplyLimit(sql, max)`: nếu top-level đã có `LIMIT N [OFFSET M]` ở cuối thì giữ nguyên; còn không thì wrap `SELECT * FROM ({user_sql}) AS _user_query LIMIT N`. Cách wrap an toàn vì DuckDB push down `LIMIT` qua subquery.

## DuckDB session safety

Mỗi query mở connection mới `:memory:`:

```sql
SET memory_limit='1GB';
SET statement_timeout='30000ms';
CREATE VIEW "raw_xxx" AS SELECT * FROM read_parquet('/app/storage/.../raw_xxx.parquet');
-- ...
-- user query
```

`CommandTimeout = 30` ở .NET side đóng vai trò "lưới an toàn" thứ 2 nếu DuckDB không respect `statement_timeout` trong một số phiên bản.

## Error mapping

Khi DuckDB throw, `DuckDbErrorMapper` match message qua regex và sinh:

- `COLUMN_NOT_FOUND` + `missing_column` + suggested_columns (top 5 theo điểm: substring/alias).
- `TABLE_NOT_FOUND` + `available_tables`.
- `INVALID_SQL` cho parser errors.
- `QUERY_FAILED` cho phần còn lại.

Response trả về cả `details.suggested_columns` và `retry_hint.message` để Claude tự retry với cột đúng.

## Authentication

### JWT (user-scoped)
- 7-day exp.
- Sub claim = user id, dùng cho mọi endpoint dạng `/api/datasets`.
- Validate issuer, audience, signing key, lifetime, clock skew 1 phút.

### API key (dataset-scoped)
- Format: `edm_<base64url(32 random bytes)>`.
- Lưu SHA-256 hash trong `dataset_api_keys.key_hash` (UNIQUE INDEX).
- Handler đọc header `X-API-Key`, lookup hash, set claims `NameIdentifier=user_id` + `dataset_id=scoped`.
- Endpoint `/api/datasets/{id}/query` có policy `"QueryAccess"` (chấp nhận cả 2 scheme). Sau khi authorize:
  - Nếu principal có `dataset_id` claim mà không khớp URL → `403`.
- Quản lý key chỉ qua JWT — API key không thể tự tạo / thu hồi key khác.

## Rate limiting

- `auth` policy: 10 request / phút / IP, áp cho `/api/auth/*`.
- `query` policy: 60 request / phút / IP, áp cho query endpoint.
- Khi reject: trả JSON `{ "success": false, "error": { "code": "TOO_MANY_REQUESTS", ... } }`.

## Trade-off đã chọn

- **Channel in-memory queue** không bền: nếu app restart khi đang parse, dataset bị kẹt `processing`. Đủ cho MVP — production cần move sang Postgres-backed queue hoặc Hangfire.
- **DuckDB per-query** thay vì shared connection pool: đơn giản, tránh leak state giữa các user. Cost ~ms cho connection setup.
- **Parquet zstd**: cân bằng giữa compression và speed. Có thể đổi thành `snappy` nếu CPU thấp ưu tiên hơn dung lượng.
- **Type inference cap 5000 dòng**: Excel rất to vẫn parse được nhưng inference chỉ lấy 5000 dòng đầu để tiết kiệm CPU. Parquet vẫn chứa đủ dữ liệu — chỉ metadata trong manifest có thể không phản ánh được edge case ở cuối file.

---

# Big update: External DB · Knowledge · Context · Dashboards · OAuth

Các subsystem thêm sau MVP. Migration đánh số `0001…0006` (runner ở `MigrationRunner.cs`, chạy khi khởi động).

## External databases (live query — KHÔNG lưu dữ liệu)

```
connections (encrypted_config AES-GCM) ──> IExternalDbConnector
   ├─ PostgresDbConnector   (Npgsql, session default_transaction_read_only=on)
   ├─ MySqlDbConnector      (MySqlConnector, SET SESSION TRANSACTION READ ONLY)
   ├─ MsSqlDbConnector      (Microsoft.Data.SqlClient, dựa guard vì không có session read-only)
   └─ BigQueryDbConnector   (Google.Cloud.BigQuery.V2, MaximumBytesBilled)
```

- **Chỉ lưu**: connection config (mã hoá bằng `SecretProtector`), metadata schema (`dataset_tables`/`dataset_columns`), 2 dòng mẫu/bảng (`dataset_tables.sample_rows`, tắt được qua `include_samples`), và query logs (chỉ SQL text). Không Parquet, không rows.
- **Read-only 3 lớp**: `ExternalQueryGuard.Validate(sql, provider)` (SELECT/WITH, blocklist theo dialect, chặn `parquet_scan`/`xp_*`/`set_config`/…, single-statement) → session read-only ở driver (PG/MySQL) → khuyến cáo tài khoản read-only.
- `ExternalQueryService.QueryAsync` route theo `datasets.source_kind`, wrap row cap theo dialect (`LIMIT`/`TOP`/`FETCH`), `ConnectionConcurrencyLimiter` (≤3 query đồng thời/connection), timeout, rồi trả compact_table đúng shape như DuckDB path. Exception được `config.Scrub()` (không lộ password) trước khi trả/log.
- Sample rows + schema lấy khi tạo dataset / `refresh-schema` (bootstrap). Không lấy `COUNT(*)` (dữ liệu đổi liên tục).

## Knowledge memory

- `dataset_knowledge_entries` (entry nhỏ, 1 fact) + `dataset_knowledge_revisions` (audit mọi create/update/archive trong cùng transaction).
- Guardrail race-safe bằng `pg_advisory_xact_lock` per-dataset: ≤200 entry active, content ≤4000, title ≤255.
- Search chi phí 0: `unaccent(lower(title||content)) % unaccent(lower(q))` (pg_trgm) + ưu tiên khớp title + pinned; GIN index `lower()`-only (unaccent không IMMUTABLE nên chỉ prefilter, selectivity thật từ index `dataset_id`).
- AI không xoá cứng — chỉ archive. Quyền ghi: JWT/PAT full; dataset key cần `can_write` (policy `KnowledgeWrite` = scheme + `RequireAssertion(CanWriteKnowledge)`).

## Context API (thay manifest.md)

- `ContextService` load datasets (owned + ready) + tables/columns/sample_rows + knowledge (`GetPinnedAsync`), rồi `ContextShaper.Shape(...)` (pure, testable) dựng payload.
- `detail=full` gồm aliases + sample_rows + knowledge; `summary` rút gọn. Nếu `AiTokenBudgetService.EstimateTokens` > `Query:SafeMaxTokens` → tự hạ summary + `warning`.
- `memory_instructions` nhắc agent lưu tri thức mới ở mỗi response.

## Multi-dataset query

- `datasets.alias` (slug unique/user, `AliasGenerator` fold dấu + cap 55 ký tự, gán trong transaction advisory-lock lúc tạo dataset).
- `DuckDbQueryService.QueryMultiAsync`: mỗi dataset → `CREATE SCHEMA "alias"` + view per table từ Parquet → chạy SQL người dùng (tham chiếu `alias.table`). File-only; external → `EXTERNAL_NOT_JOINABLE`; dataset-scoped key → 403. Single-dataset path giữ nguyên.

## Dashboards (widget SQL đóng băng)

- `dashboards` + `dashboard_widgets` (sql đóng băng, chart_type/config, refresh_interval_sec, source, archived_at).
- **Validate 2 lần**: lúc lưu (`ValidateByDialect` + trial `LIMIT 1`) và lúc mỗi lần thực thi (re-validate row đọc từ DB — chống sửa tay). Browser chỉ gọi `.../data` (không gửi SQL).
- `GetWidgetDataAsync`: ownership → cache key `widget:{id}:{updated_at.Ticks}` (edit tự bust) → `IMemoryCache` TTL = refresh interval → execute qua DuckDb/External service với row cap 1000 → trả compact_table. **Bypass token budget** qua `QueryOptions.BypassAiBudget` (chỉ set được trong C#, có `[JsonIgnore]` nên không bind từ body — bảo vệ token budget cho các caller AI thường).

## OAuth 2.1 cho MCP (Phase 0b)

- `oauth_clients` + `oauth_authorization_codes` (hash, single-use, TTL 5 phút, PKCE S256 bắt buộc).
- Access token phát ra **chính là một PAT** `edm_pat_…` (tự tạo, hiện + thu hồi được trong UI). Không refresh token.
- Bridge trả `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"` trên 401 để Claude tự khám phá. Endpoint HTTP là `/mcp` (bắt buộc `Authorization: Bearer edm_pat_…`); nhánh GUID-làm-key cũ đã xoá.

## Cấu hình (appsettings / env)

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `Encryption:MasterKey` (`EDM_ENCRYPTION_KEY`) | — (bắt buộc ≥32) | Mã hoá connection config |
| `Oauth:PublicUrl` (`EDM_PUBLIC_URL`) | `http://localhost` | Issuer + resource metadata (production: https) |
| `Query:MaxDatasetsPerQuery` | 3 | Trần dataset/multi-query |
| `Query:SafeMaxTokens` / `HardMaxTokens` | 32000 / 512000 | Token budget AI |
| `ExternalQuery:TimeoutSeconds` / `MaxConcurrentPerConnection` / `MaxTablesPerDataset` | 30 / 3 / 50 | Bảo vệ DB nguồn |
| `Dashboard:MaxRowsPerWidget` | 1000 | Row cap widget |
| `Proxy:TrustForwardedHeaders` | (compose: true) | Rate-limit theo user thật sau Caddy |

Giới hạn dataset/user: cột `users.max_datasets` (mặc định 10), chỉnh trực tiếp cho từng user.
