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
