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
  "dataset": { ... cùng schema như list item },
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
    "include_sql": true
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

## Health

### GET `/health`
`{ "status": "ok", "app": "Excel Dataset Manager", "version": "1.0" }` — không cần auth.
