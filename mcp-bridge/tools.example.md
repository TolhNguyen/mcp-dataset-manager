# Tools Configuration

This file is the single source of truth for what tools the remote MCP bridge exposes to Claude.
Each fenced ` ```yaml ` block with a `type:` field is a declaration. Free text between
blocks is documentation for humans and is ignored by the parser.

**Three block kinds:**

- `type: config` — global settings
- `type: connection` — a remote endpoint + auth strategy
- `type: tool` — a callable, referencing one connection

To use this file:

1. Copy it to `tools.md` next to the bridge: `cp tools.example.md tools.md`
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

Lưu ý: `${request.user_token}` là PAT (edm_pat_...) lấy từ header
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

## Connection: Partner A (OAuth2 client credentials)

Replace with a real partner API. The bridge fetches a token once and caches it
until shortly before expiry; on 401 it transparently refreshes once and retries.

```yaml
type: connection
id: partner_a
base_url: https://api.partner-a.example.com
auth:
  type: oauth2_client_credentials
  token_url: https://auth.partner-a.example.com/oauth/token
  client_id: ${PARTNER_A_CLIENT_ID}
  client_secret: ${PARTNER_A_CLIENT_SECRET}
  scope: invoices.read
  # client_auth: body (default) | basic
timeout_ms: 45000
```

## Connection: Partner B (HTTP basic auth)

```yaml
type: connection
id: partner_b
base_url: https://api.partner-b.example.com
auth:
  type: basic
  username: ${PARTNER_B_USER}
  password: ${PARTNER_B_PASS}
```

## Connection: GitHub (bearer token)

A worked example for a real public API.

```yaml
type: connection
id: github
base_url: https://api.github.com
auth:
  type: bearer
  token: ${GITHUB_TOKEN:-}
default_headers:
  Accept: application/vnd.github+json
  X-GitHub-Api-Version: "2022-11-28"
```

---

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

## get_context

```yaml
type: tool
name: get_context
description: |
  Fetch structured schema + business-knowledge memory for one or more datasets
  BEFORE writing SQL or planning an analysis. Returns each dataset's alias, tables,
  columns (normalized SQL names + Vietnamese display names/aliases), sample rows,
  dialect, and its knowledge memory + memory_instructions. ALWAYS call this first;
  original headers are normalized and cannot be guessed. Use dataset alias.table in
  multi-dataset queries. Honor memory_instructions: save new business facts via
  save_dataset_knowledge.
connection: edm
method: GET
path: /api/context
params:
  dataset_ids:
    in: query
    type: string
    required: true
    description: One or more dataset UUIDs, comma-separated (max 3).
  tables:
    in: query
    type: string
    description: Optional comma-separated table names to include only (reduces tokens).
  detail:
    in: query
    type: string
    enum: [summary, full]
    description: full (default) = columns+aliases+sample rows+knowledge; summary = trimmed.
response_hint: |
  datasets[].tables[].qualified_name is what you reference in SQL (alias.table).
  If warning is present the payload was auto-downgraded to summary — pass tables= to
  get full detail for just the tables you need.
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

## query_datasets

```yaml
type: tool
name: query_datasets
description: |
  Run one read-only SQL query that JOINs across several FILE datasets. Each dataset
  is a schema named by its alias (from get_context): SELECT ... FROM sales.orders o
  JOIN crm.customers c ON ... Only file (uploaded) datasets are joinable; external
  database datasets are not — query those one at a time with query_dataset.
connection: edm
method: POST
path: /api/query
params:
  dataset_ids:
    in: body
    type: array
    items:
      type: string
    required: true
    description: 2-3 file dataset UUIDs to join.
  sql:
    in: body
    type: string
    required: true
    description: A single SELECT/WITH statement referencing alias.table names.
  max_rows:
    in: body
    type: integer
    default: 100
    description: 1-1000.
body_template: |
  {
    "dataset_ids": {{dataset_ids | json}},
    "sql": {{sql | json}},
    "options": { "max_rows": {{max_rows}} }
  }
response_hint: |
  Success path: result.columns + result.rows.
  On error.code=EXTERNAL_NOT_JOINABLE, one of the ids is an external DB dataset —
  query it alone with query_dataset instead.
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

## get_dataset_knowledge

```yaml
type: tool
name: get_dataset_knowledge
description: |
  Read the dataset's business-knowledge memory: notes, column meanings,
  business rules, metric definitions, join hints, and documents that the user
  or a previous conversation recorded. ALWAYS call this before analyzing a
  dataset you haven't seen yet (right after get_dataset_schema) — pinned
  entries especially may correct assumptions you'd otherwise make from column
  names alone (e.g. what a column really means, or which metric formula the
  business actually uses).
connection: edm
method: GET
path: /api/datasets/{dataset_id}/knowledge
params:
  dataset_id:
    in: path
    type: string
    required: true
    description: UUID from list_datasets.
  include_archived:
    in: query
    type: boolean
    default: false
    description: Include soft-deleted (archived) entries.
  kind:
    in: query
    type: string
    enum: [note, column_meaning, business_rule, metric_definition, join_hint, document]
    description: Filter to a single kind of entry. Omit to get all kinds.
response_hint: |
  Shape: {success, data: {entries[]}}. Each entry has kind, title, content,
  pinned, created_by, created_at, updated_at. Entries are ordered pinned-first
  then newest-first — treat pinned entries as ground truth for this dataset.
```

## save_dataset_knowledge

```yaml
type: tool
name: save_dataset_knowledge
description: |
  Khi người dùng cung cấp thông tin nghiệp vụ mới, sửa cách hiểu của bạn, định
  nghĩa metric/quy tắc, hoặc bạn phát hiện mapping cột quan trọng — lưu lại
  bằng tool này. Mỗi entry 1 fact, ngắn gọn.
connection: edm
method: POST
path: /api/datasets/{dataset_id}/knowledge
params:
  dataset_id:
    in: path
    type: string
    required: true
    description: UUID from list_datasets.
  kind:
    in: body
    type: string
    enum: [note, column_meaning, business_rule, metric_definition, join_hint, document]
    description: Defaults to 'note' if omitted.
  title:
    in: body
    type: string
    required: true
    description: Short label for this fact, e.g. "doanh_thu excludes VAT".
  content:
    in: body
    type: string
    required: true
    description: The fact itself — one idea, concise (max 4000 chars).
  pinned:
    in: body
    type: boolean
    description: Pin so this entry always surfaces first (default false).
response_hint: |
  Shape: {success, data: {id, kind, title, content, pinned, ...}}. Keep the
  returned id if you may need to update this entry later via
  update_dataset_knowledge.
  error.code=KNOWLEDGE_LIMIT_REACHED means the dataset already has 200 active
  entries — archive/consolidate before adding more.
```

## update_dataset_knowledge

```yaml
type: tool
name: update_dataset_knowledge
description: |
  Update an existing knowledge entry — e.g. when the user corrects a fact,
  extends a business rule, or asks you to pin/unpin something important. Only
  send the fields that changed; omitted fields keep their current value.
connection: edm
method: PUT
path: /api/datasets/{dataset_id}/knowledge/{entry_id}
params:
  dataset_id:
    in: path
    type: string
    required: true
    description: UUID from list_datasets.
  entry_id:
    in: path
    type: string
    required: true
    description: UUID of the knowledge entry, from get_dataset_knowledge or search_knowledge.
  title:
    in: body
    type: string
    description: New title. Omit to keep the current one.
  content:
    in: body
    type: string
    description: New content. Omit to keep the current one.
  pinned:
    in: body
    type: boolean
    description: New pinned state. Omit to keep the current one.
response_hint: |
  Shape: {success, data: {id, kind, title, content, pinned, ...}}.
  error.code=KNOWLEDGE_NOT_FOUND means the entry_id/dataset_id pair doesn't exist.
  error.code=VALIDATION_ERROR means none of title/content/pinned were provided
  — at least one is required.
```

## search_knowledge

```yaml
type: tool
name: search_knowledge
description: |
  Full-text search across knowledge entries in one or more datasets. Use this
  when the user asks something like "what do we know about X", or before
  answering a question that might already have a saved business rule / metric
  definition covering it.
connection: edm
method: GET
path: /api/knowledge/search
params:
  dataset_ids:
    in: query
    type: string
    required: true
    description: Comma-joined dataset UUIDs to search within, e.g. "id1,id2".
  q:
    in: query
    type: string
    required: true
    description: Search text (matches title + content, accent-insensitive).
  limit:
    in: query
    type: integer
    default: 5
    description: Max results, 1-20.
response_hint: |
  Shape: {success, data: {results[]}}. Each result has dataset_id, kind, title,
  content, pinned, score. Pinned entries can appear even without a strong text
  match.
```

---

# Partner tools (reconciliation examples)

The point of having multiple connections in one bridge: Claude can query EDM
and a partner API in a single conversation and reconcile the two.

## partner_a_invoices

```yaml
type: tool
name: partner_a_invoices
description: |
  Fetch invoices from Partner A within a date range. Use this to reconcile
  against the user's internal invoice data on EDM.
connection: partner_a
method: GET
path: /v1/invoices
params:
  from:
    in: query
    type: string
    required: true
    description: Start date inclusive (YYYY-MM-DD).
  to:
    in: query
    type: string
    required: true
    description: End date inclusive (YYYY-MM-DD).
  status:
    in: query
    type: string
    enum: [paid, pending, void]
    default: paid
  limit:
    in: query
    type: integer
    default: 200
response_transform: "$.invoices[*]"
response_hint: |
  After transform: an array of invoices, each {id, amount, issued_at, status, ...}.
  To reconcile with EDM, match invoice.id against the raw_invoices.invoice_id column
  in the user's EDM dataset (verify the SQL column name via get_dataset_schema).
```

## partner_a_invoice_detail

```yaml
type: tool
name: partner_a_invoice_detail
description: Fetch one invoice's full detail from Partner A by its id.
connection: partner_a
method: GET
path: /v1/invoices/{invoice_id}
params:
  invoice_id:
    in: path
    type: string
    required: true
```

## partner_b_orders

```yaml
type: tool
name: partner_b_orders
description: |
  List Partner B orders with optional filters. Used for cross-checking
  fulfillment data between the user's EDM dataset and Partner B's records.
connection: partner_b
method: GET
path: /orders
params:
  from:
    in: query
    type: string
    required: true
  to:
    in: query
    type: string
    required: true
  customer_id:
    in: query
    type: string
  tag:
    in: query
    type: array
    items:
      type: string
    description: One or more tags; repeated as ?tag=a&tag=b in the URL.
response_transform: "$.data[*]"
```

---

# Public API examples

These two work out of the box (assuming you have a GitHub token); they're here
as a sanity check that the bridge is wired up correctly.

## github_search_repos

```yaml
type: tool
name: github_search_repos
description: Search GitHub repositories by keyword. Useful for sanity-testing the bridge.
connection: github
method: GET
path: /search/repositories
params:
  q:
    in: query
    type: string
    required: true
    description: GitHub search query, e.g. "duckdb language:typescript".
  per_page:
    in: query
    type: integer
    default: 10
response_transform: "$.items[*]"
response_hint: |
  After transform: array of repos with fields like full_name, stargazers_count,
  description, html_url. Useful for showing the user a short ranked list.
```

## github_user

```yaml
type: tool
name: github_user
description: Fetch a public GitHub user profile.
connection: github
method: GET
path: /users/{username}
params:
  username:
    in: path
    type: string
    required: true
```
