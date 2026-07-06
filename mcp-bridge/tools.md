# Tools Configuration

This file is the single source of truth for what tools the remote MCP bridge exposes to Claude.
Each fenced ` ```yaml ` block with a `type:` field is a declaration. Free text between
blocks is documentation for humans and is ignored by the parser.

**Three block kinds:**

- `type: config` — global settings
- `type: connection` — a remote endpoint + auth strategy
- `type: tool` — a callable, referencing one connection

To use this file:

2. Set the server env vars referenced as `${VAR}`. Runtime vars like `${request.user_token}` are resolved from the incoming HTTP request.
3. Edit the connections + tools to match your environment.
4. Validate: `node dist/index.js validate ./tools.md`.
5. Restart the bridge container.

---

## Global settings

```yaml
type: config
log_level: info
default_timeout_ms: 30000
# 0 = unlimited. Set a cap if a partner API may return large payloads.
max_response_bytes: 0
```

---

## Connection: EDM (Excel Dataset Manager — your own server)

Lưu ý: từ bản này, `${request.user_token}` là PAT (edm_pat_...) lấy từ header
`Authorization: Bearer ...` của client MCP — không còn là user id trên URL.

```yaml
type: connection
id: edm
base_url: ${EDM_API_URL}
auth:
  type: header
  header: X-API-Key
  value: ${request.user_token}
default_headers:
  Accept: application/json
```

# EDM tools

These give Claude the same access as the old hand-crafted EDM MCP server: list
datasets, fetch schemas, run SQL, upload.

## list_datasets

```yaml
type: tool
name: list_datasets
description: |
  List every dataset the user has uploaded to Excel Dataset Manager. Returns
  dataset_id (use this for other tools), name, original_file, status, table_count
  and total_rows. Call this first when the user references their data without
  naming a specific dataset.
connection: edm
method: GET
path: /api/datasets/
response_hint: |
  Top-level shape is {success, limit, datasets[]}. Use datasets[].dataset_id
  in subsequent calls. If limit.used == limit.max_datasets the user can't upload
  new files until they delete one.
```

## get_dataset_schema

```yaml
type: tool
name: get_dataset_schema
description: |
  Fetch the manifest.md for a dataset — describes tables, normalized SQL column
  names, inferred types, sample values, and Vietnamese aliases. ALWAYS call this
  before writing SQL against a dataset you haven't queried yet; original headers
  are normalized and cannot be guessed.
connection: edm
method: GET
path: /api/datasets/{dataset_id}/download/manifest
params:
  dataset_id:
    in: path
    type: string
    required: true
    description: UUID from list_datasets.
```

## query_dataset

```yaml
type: tool
name: query_dataset
description: |
  Run a read-only SQL query (SELECT or WITH) against one EDM dataset. Use the
  normalized column names from get_dataset_schema, not the original Vietnamese
  headers. Server forbids INSERT/UPDATE/DELETE/DROP/ATTACH/COPY/PRAGMA.
connection: edm
method: POST
path: /api/datasets/{dataset_id}/query
params:
  dataset_id:
    in: path
    type: string
    required: true
  sql:
    in: body
    type: string
    required: true
    description: A single SELECT or WITH statement.
  max_rows:
    in: body
    type: integer
    default: 100
    description: 1-1000.
body_template: |
  {
    "query_type": "sql",
    "sql": {{sql | json}},
    "options": { "max_rows": {{max_rows}}, "include_sql": true }
  }
response_hint: |
  Success path: result.columns + result.rows.
  On error.code=COLUMN_NOT_FOUND, error.details.suggested_columns contains likely
  fixes — retry with the suggested name.
  On error.code=DATASET_NOT_READY, wait and retry (parsing is async).
```

## upload_dataset

```yaml
type: tool
name: upload_dataset
description: |
  Upload an Excel/CSV file to EDM. In remote MCP mode, the file path must exist inside the bridge container/server; Claude.ai cannot read arbitrary files from the user's computer. For normal use, upload files through the web UI.
  After upload, the file is processed in the background — poll get_dataset for
  the status to flip from 'processing' to 'ready'.
connection: edm
method: POST
path: /api/datasets/
content_type: multipart/form-data
params:
  file:
    in: form
    type: file
    required: true
    description: Server/container path to .xlsx/.xls/.xlsm/.csv/.tsv (≤100 MB).
  name:
    in: form
    type: string
    description: Optional human-readable dataset name; defaults to filename.
```

## get_dataset (status polling)

```yaml
type: tool
name: get_dataset
description: |
  Get the metadata for a single dataset including its current parsing status.
  Use this after upload_dataset to wait for status='ready' before querying.
connection: edm
method: GET
path: /api/datasets/{dataset_id}
params:
  dataset_id:
    in: path
    type: string
    required: true
response_hint: |
  status='processing' means parsing in progress — wait and retry.
  status='ready' means safe to query.
  status='failed' means parsing crashed; error_message has details.
```

## delete_dataset

```yaml
type: tool
name: delete_dataset
description: |
  Delete an EDM dataset PERMANENTLY (database row + parquet files + original
  upload). Only call this when the user explicitly confirms.
connection: edm
method: DELETE
path: /api/datasets/{dataset_id}
params:
  dataset_id:
    in: path
    type: string
    required: true
```
