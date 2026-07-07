# API Reference

Base URL: `http://localhost:8080`

Format: JSON, snake_case. Tất cả response có dạng:

```json
{ "success": true,  "<payload-key>": ... }
{ "success": false, "error": { "code": "...", "message": "...", "details": ..., "retryable": true } }
```

Auth header: `Authorization: Bearer <jwt>` cho hầu hết endpoint. Endpoint query thêm option `X-API-Key: edm_...`.

## Auth

### POST `/api/auth/register`
Body:
```json
{ "email": "user@example.com", "password": "min8chars" }
```
Response (200):
```json
{
  "success": true,
  "user": { "id": "uuid", "email": "user@example.com" },
  "token": "<jwt>"
}
```
Lỗi: `EMAIL_EXISTS`, `PASSWORD_TOO_SHORT`, `VALIDATION_ERROR`, `TOO_MANY_REQUESTS`.

### POST `/api/auth/login`
Body: như trên.
Lỗi: `INVALID_CREDENTIALS`, `TOO_MANY_REQUESTS`.

### GET `/api/auth/me`
Headers: `Authorization: Bearer ...`
Response: `{ "success": true, "user": { ... } }`

## Datasets

### GET `/api/datasets/`
Response:
```json
{
  "success": true,
  "limit": { "max_datasets": 10, "used": 2, "remaining": 8, "can_upload": true },
  "datasets": [
    {
      "dataset_id": "uuid",
      "name": "Sales 2024",
      "original_file_name": "sales.xlsx",
      "file_type": "xlsx",
      "file_size_bytes": 524288,
      "table_count": 3,
      "total_rows": 12345,
      "status": "ready",
      "error_message": null,
      "created_at": "2026-05-26T03:00:00Z",
      "actions": {
        "download_original_url": "/api/datasets/uuid/download/original",
        "download_manifest_url": "/api/datasets/uuid/download/manifest",
        "query_url": "/api/datasets/uuid/query",
        "detail_url": "/api/datasets/uuid",
        "delete_url": "/api/datasets/uuid"
      }
    }
  ]
}
```

### POST `/api/datasets/`
Content-Type: `multipart/form-data`
Fields:
- `file` (bắt buộc) — `.xlsx`, `.xls`, `.xlsm`, `.csv`, `.tsv`, tối đa 100 MB.
- `name` (tuỳ chọn) — mặc định lấy filename.

Response (200) — **trả về ngay**, parsing chạy nền:
```json
{
  "success": true,
  "dataset": {
    "dataset_id": "uuid",
    "name": "...",
    "status": "processing",
    ...
  },
  "message": "Dataset uploaded successfully and is being processed."
}
```

Client nên poll `/api/datasets/{id}` mỗi vài giây cho tới khi `status` chuyển `ready` hoặc `failed`.

Lỗi: `INVALID_FILE`, `INVALID_FILE_TYPE`, `FILE_TOO_LARGE`, `DATASET_LIMIT_REACHED`, `STORAGE_ERROR`.

### GET `/api/datasets/{dataset_id}`
Response:
```json
{
  "success": true,
  "dataset": {
    "...": "...",
    "business_knowledge": "Chỉ tính doanh thu với đơn Completed...",
    "business_knowledge_updated_at": "2026-05-29T08:00:00Z"
  },
  "tables": [
    {
      "table_name": "raw_sheet1",
      "source_name": "Sheet1",
      "source_type": "sheet",
      "row_count": 1000,
      "column_count": 12,
      "columns": [
        {
          "ordinal_position": 1,
          "original_header": "Mã sản phẩm",
          "normalized_name": "ma_san_pham",
          "display_name": "Mã sản phẩm",
          "inferred_type": "string",
          "semantic_type": "identifier",
          "null_count": 0,
          "distinct_count": 856,
          "sample_values": ["SP001", "SP002", "SP003"]
        }
      ]
    }
  ]
}
```

### PUT `/api/datasets/{dataset_id}/business-knowledge`
Auth: Bearer JWT only.

Body:
```json
{ "business_knowledge": "Chỉ tính doanh thu với đơn Completed..." }
```

Rules:
- `null` becomes an empty string.
- Trailing whitespace is trimmed.
- Max length is 10,000 characters.
- Saving regenerates `manifest.md` when metadata is available.

Response:
```json
{
  "success": true,
  "data": {
    "dataset_id": "uuid",
    "business_knowledge": "...",
    "business_knowledge_updated_at": "2026-05-29T08:00:00Z",
    "manifest_updated": true
  }
}
```

### DELETE `/api/datasets/{dataset_id}`
Response: `{ "success": true, "data": { "deleted": true, "dataset_id": "uuid" } }`

Xoá DB row + thư mục storage. Idempotent: chạy lần 2 → `DATASET_NOT_FOUND`.

### GET `/api/datasets/{dataset_id}/download/original`
Trả file gốc với header `Content-Disposition: attachment`.

### GET `/api/datasets/{dataset_id}/download/manifest`
Trả `manifest.md` (text/markdown). Chỉ có khi dataset đã `ready`.

## API keys (dataset-scoped)

### POST `/api/datasets/{dataset_id}/api-keys`
Body: `{ "name": "claude-desktop" }`

Response (200) — `api_key` chỉ hiển thị **một lần**:
```json
{
  "success": true,
  "data": {
    "api_key_id": "uuid",
    "dataset_id": "uuid",
    "name": "claude-desktop",
    "api_key": "edm_xxx...",
    "usage": "Send via the X-API-Key header to authorize query requests to this dataset."
  }
}
```

### GET `/api/datasets/{dataset_id}/api-keys`
Liệt kê (không trả raw key — chỉ metadata).

### DELETE `/api/datasets/{dataset_id}/api-keys/{key_id}`
Thu hồi key. Lần dùng sau sẽ `401`.

## Query

### POST `/api/datasets/{dataset_id}/query`
Auth: Bearer JWT **hoặc** `X-API-Key: edm_...` (key phải khớp `dataset_id` trong URL).

Body:
```json
{
  "query_type": "sql",
  "sql": "SELECT region, SUM(revenue) AS total FROM raw_sales GROUP BY region ORDER BY total DESC",
  "options": {
    "max_rows": 100,
    "return_format": "compact",
    "include_sql": true,
    "response_mode": "ai_safe"
  }
}
```

Response (200, success):
```json
{
  "success": true,
  "dataset_id": "uuid",
  "query_id": "uuid",
  "status": "completed",
  "result": {
    "format": "compact_table",
    "columns": [
      { "name": "region", "type": "VARCHAR" },
      { "name": "total",  "type": "DOUBLE" }
    ],
    "rows": [
      ["Hà Nội", 1234567.0],
      ["TP.HCM", 987654.0]
    ],
    "row_count": 2,
    "truncated": false,
    "next_cursor": null
  },
  "execution": { "engine": "duckdb", "elapsed_ms": 42, "max_rows": 100 },
  "sql": {
    "submitted": "SELECT region, SUM(revenue) ...",
    "executed":  "SELECT * FROM (SELECT region, SUM(revenue) ...) AS _user_query LIMIT 100"
  },
  "warnings": [],
  "ai_budget": {
    "estimated_tokens": 12000,
    "safe_max_tokens": 32000,
    "hard_max_tokens": 512000,
    "requires_confirmation": false,
    "blocked": false
  },
  "error": null
}
```

Response (200, failure — body status không đổi vì lỗi là về SQL chứ không phải HTTP):
```json
{
  "success": false,
  "dataset_id": "uuid",
  "query_id": "uuid",
  "status": "failed",
  "result": null,
  "execution": { "engine": "duckdb", "elapsed_ms": 5 },
  "sql": { "submitted": "...", "executed": "..." },
  "warnings": [],
  "error": {
    "code": "COLUMN_NOT_FOUND",
    "message": "Referenced column \"revanue\" not found in FROM clause",
    "details": {
      "missing_column": "revanue",
      "table": "raw_sales",
      "suggested_columns": ["revenue", "revenue_vnd", "net_revenue"]
    },
    "retryable": true
  },
  "retry_hint": {
    "message": "Try `revenue` instead of `revanue`.",
    "suggested_column": "revenue"
  }
}
```

Các error code SQL: `INVALID_SQL`, `NON_READONLY_SQL`, `COLUMN_NOT_FOUND`, `TABLE_NOT_FOUND`, `QUERY_TIMEOUT`, `QUERY_FAILED`, `DATASET_NOT_READY`.

### Rule bắt buộc cho client / Claude

- Chỉ `SELECT` hoặc `WITH`.
- Một câu lệnh / request, không `;` cuối (server sẽ chặn).
- Dùng `normalized_name` từ manifest, không dùng header gốc tiếng Việt.
- Không dùng `read_csv`, `read_parquet`, `attach`, ... — bị blacklist.
- Mặc định server thêm `LIMIT 100`; muốn nhiều hơn thì chỉ định `options.max_rows` (hard cap 1000).

### AI token budget

- Safe results return the normal `compact_table` response with `ai_budget`.
- Results above the safe budget return `TOKEN_BUDGET_CONFIRMATION_REQUIRED` with `summary.preview_rows`, suggestions, and a `confirmation_id`.
- Retry with `options.allow_large_result = true`, the returned `confirmation_id`, and `response_mode = "raw"` to receive raw rows when still under the hard limit.
- Results above the hard budget return `TOKEN_BUDGET_HARD_LIMIT_EXCEEDED` and never return raw rows.
- Claude/MCP clients should refine SQL by selecting fewer columns, adding filters, aggregating with `GROUP BY`, or using `response_mode = "summary"`.

## External database connections (live query)

Tất cả `JwtOnly`. Config connection được **mã hoá AES-256-GCM** trước khi lưu; response **không bao giờ** chứa password/service-account (host được mask).

### POST `/api/connections`
```json
{ "name": "Shop PG", "provider": "postgresql",
  "config": { "host": "db", "port": 5432, "database": "shop", "username": "ro", "password": "…", "ssl": true } }
```
BigQuery config: `{ "project_id": "…", "dataset": "…", "service_account_json": "<paste JSON string>", "max_bytes_billed": 1073741824 }`.
Response (masked): `{ success, connection: { id, name, provider, host_masked, database, username, … } }`.

### GET `/api/connections` · PUT `/api/connections/{id}` · DELETE `/api/connections/{id}`
List (masked) / cập nhật (config chỉ ghi đè khi gửi kèm) / xoá. Xoá khi còn dataset tham chiếu → `CONNECTION_IN_USE`.

### POST `/api/connections/{id}/test`
Mở kết nối + `SELECT 1`; cảnh báo nếu tài khoản có quyền ghi. `{ success, data: { connection_id, success, warning, last_test_at } }`.

### GET `/api/connections/{id}/tables`
`{ success, tables: [ { queryable_name, source_label, column_count } ] }`.

### POST `/api/connections/{id}/datasets`
```json
{ "name": "Khach hang", "tables": ["public.customers"], "include_samples": true }
```
Tạo dataset external (`source_kind='external_db'`, ready ngay). Chỉ lưu schema + 2 dòng mẫu/bảng (nếu `include_samples`).

### POST `/api/datasets/{id}/refresh-schema`
Đọc lại schema + sample rows từ nguồn.

> Query dataset external dùng chính `POST /api/datasets/{id}/query` — server route theo `source_kind`, validate theo dialect, wrap row cap, chạy live. Lỗi: `EXTERNAL_QUERY_FAILED`, `TOO_MANY_CONCURRENT_QUERIES`.

## Knowledge memory

### GET `/api/datasets/{id}/knowledge?include_archived=&kind=` — `QueryAccess`
`{ success, data: { entries: [ { id, kind, title, content, source, created_by, pinned, … } ] } }`.

### POST `/api/datasets/{id}/knowledge` — `KnowledgeWrite`
```json
{ "kind": "metric_definition", "title": "Doanh thu thuần", "content": "…", "pinned": true }
```
`kind` ∈ note|column_meaning|business_rule|metric_definition|join_hint|document. Guardrail: ≤200 entry active/dataset, content ≤4000, title ≤255. Lỗi: `VALIDATION_ERROR`, `KNOWLEDGE_LIMIT_REACHED`.

### PUT `/api/datasets/{id}/knowledge/{entryId}` — `KnowledgeWrite`
### DELETE `/api/datasets/{id}/knowledge/{entryId}` — `KnowledgeWrite` (archive mềm)
### DELETE `/api/datasets/{id}/knowledge/{entryId}/hard` — `JwtOnly` (xoá cứng)
### POST `/api/datasets/{id}/knowledge/documents` — `KnowledgeWrite`
Multipart `file` (.md/.txt ≤1MB) → tách theo heading thành entry `kind=document`. `{ success, data: { imported, skipped } }`.

### GET `/api/knowledge/search?dataset_ids=a,b&q=…&limit=5` — `QueryAccess`
Tìm accent-insensitive (unaccent + pg_trgm). Mọi thay đổi ghi `dataset_knowledge_revisions` (audit).

Quyền ghi: JWT/PAT full; dataset-scoped key cần cột `can_write=true` (bật khi tạo key). Entry do dataset-key tạo có `source=ai`, `created_by="ai:<key name>"`.

## Context (thay manifest.md làm nguồn schema cho AI)

### GET `/api/context?dataset_ids=a,b&tables=orders&detail=summary|full` — `QueryAccess`
```json
{ "success": true,
  "datasets": [ { "dataset_id","name","alias","source_kind","provider","dialect",
    "tables": [ { "table_name","qualified_name":"alias.table","columns":[…],"sample_rows":[…] } ],
    "knowledge": [ … ] } ],
  "memory_instructions": "Bộ nhớ dataset có N entries. …",
  "token_estimate": 1830, "warning": null }
```
`detail=full` = columns+aliases+sample_rows+knowledge; `summary` = rút gọn. Nếu vượt `Query:SafeMaxTokens` tự hạ summary + `warning`. Dataset-scoped key chỉ xin được đúng dataset của nó.

## Multi-dataset query (file datasets)

### POST `/api/query` — `QueryAccess`
```json
{ "dataset_ids": ["id1","id2"],
  "sql": "SELECT … FROM sales.orders o JOIN crm.customers c ON …",
  "options": { "max_rows": 100 } }
```
Mỗi dataset → schema DuckDB theo `alias`. Tối đa `Query:MaxDatasetsPerQuery` (3), tất cả `source_kind='file'`, thuộc user, `ready`. External → `EXTERNAL_NOT_JOINABLE`. Dataset-scoped key → 403 (không cross-dataset).

## Dashboards

### POST/GET `/api/dashboards` · GET/DELETE `/api/dashboards/{id}` — `JwtOnly`
GET `{id}` trả dashboard + widget active. Tối đa 10 dashboard/user.

### POST `/api/dashboards/{id}/widgets` — `KnowledgeWrite`
```json
{ "dataset_id":"…","title":"…","sql":"SELECT …","chart_type":"bar",
  "chart_config":{…},"refresh_interval_sec":60 }
```
SQL **đóng băng**, validate lúc lưu (theo dialect) + chạy thử `LIMIT 1`. `chart_type` ∈ table|line|bar|pie|stat. `refresh_interval_sec` clamp ≥30. Tối đa 20 widget/dashboard. Dataset-scoped key chỉ tạo widget cho đúng dataset của nó.

### PUT `.../widgets/{wid}` · DELETE `.../widgets/{wid}` (archive) — `KnowledgeWrite`
### DELETE `.../widgets/{wid}/hard` — `JwtOnly`
### GET `/api/dashboards/{id}/widgets/{wid}/data` — `JwtOnly`, rate-limited
Chạy lại SQL đã đóng băng (re-validate + row cap `Dashboard:MaxRowsPerWidget` mặc định 1000 + timeout), cache in-memory theo TTL = refresh interval. Trả compact_table `{ columns, rows, row_count }` — **không** qua token budget (data đi ra browser).

### POST `/api/dashboards/widgets` — `KnowledgeWrite` (MCP tiện lợi)
Body kèm `dashboard_name` → tự tạo dashboard theo tên nếu chưa có, rồi tạo widget.

## OAuth 2.1 cho MCP

Không cần dán token — Claude tự khám phá và mở trang đăng nhập.

- `GET /.well-known/oauth-authorization-server` · `GET /.well-known/oauth-protected-resource[/mcp]` — metadata (anonymous).
- `POST /api/oauth/register` — Dynamic Client Registration (public client, PKCE).
- `GET /oauth/authorize?response_type=code&client_id=…&redirect_uri=…&code_challenge=…&code_challenge_method=S256&state=…` — trang consent (đăng nhập + Cho phép).
- `POST /api/oauth/authorize/approve` — (JWT) tạo authorization code, redirect về client.
- `POST /api/oauth/token` — đổi code (PKCE `code_verifier`) lấy `{ access_token: "edm_pat_…", token_type: "Bearer" }`. Code single-use, TTL 5 phút. Access token chính là một PAT — hiển thị + thu hồi được trong UI.

## Health

### GET `/health`
`{ "status": "ok", "app": "Excel Dataset Manager", "version": "1.0" }` — không cần auth.
