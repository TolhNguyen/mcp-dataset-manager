# Big Update Design — External DB, Business Knowledge v2, Structured Context + Multi-dataset Join

Ngày: 2026-07-06
Trạng thái: DRAFT — chờ user duyệt

> **Giả định chưa được xác nhận** (user vắng mặt khi brainstorm — cần duyệt lại trước khi implement):
> 1. External DB dùng hướng **sync về Parquet** (không query live vào DB nguồn ở phase này).
> 2. Tri thức tìm kiếm bằng **Postgres full-text search**, chưa dùng embeddings/vector.
> 3. Thứ tự thực hiện: Phase 0 (nền tảng) → A (External DB) → B (Knowledge v2) → C (Context + Join). B và C có thể làm song song sau Phase 0.

---

## Bối cảnh & mục tiêu

EDM hiện tại: upload Excel/CSV → parse → Parquet → query qua DuckDB, expose cho AI qua MCP bridge. Ba giới hạn lớn:

1. Chỉ nhận dữ liệu từ file upload — không kết nối được MySQL/MSSQL/PostgreSQL/BigQuery.
2. `business_knowledge` là 1 field TEXT 10k ký tự, chỉ user sửa được qua web (JwtOnly) — AI qua MCP không thể bổ sung tri thức học được trong quá trình làm việc.
3. Schema/tri thức trả cho AI là nguyên file `manifest.md` — token-heavy, khó parse, và mỗi query chỉ chạm được 1 dataset nên không join dữ liệu giữa các dataset.

## Nợ kỹ thuật phải trả trước (Phase 0)

Các mục này là **điều kiện tiên quyết**, đặc biệt vì Phase A sẽ lưu credential DB thật của người dùng:

| # | Vấn đề | Cách xử lý |
|---|---|---|
| P0-1 | `ApiKeyAuthenticationHandler` chấp nhận GUID trần làm API key (`insecure_user_id`) → biết user_id = toàn quyền. MCP bridge HTTP mode dùng chính `/mcp/{userId}` làm key | Xoá nhánh GUID. MCP HTTP mode chuyển sang bắt buộc `Authorization: Bearer edm_pat_...`; bridge forward header này (`${request.authorization}`), URL còn `/mcp` (giữ `/mcp/{userId}` một thời gian, trả lỗi hướng dẫn migrate) |
| P0-2 | Không có migration framework | Thêm bảng `schema_migrations` + runner chạy script SQL đánh số trong `api/Migrations/` (giữ Dapper, không thêm EF). `DatabaseInitializer` hiện tại trở thành migration 0001 |
| P0-3 | Chưa có chỗ lưu secret | `SecretProtector` service: AES-256-GCM, master key từ env `EDM_ENCRYPTION_KEY` (bắt buộc ≥32 bytes, fail-fast như JWT_KEY). Lưu ciphertext trong Postgres |
| P0-4 | Rate limit theo IP sau Caddy → mọi user chung 1 bucket | `UseForwardedHeaders` (trust proxy nội bộ) + partition theo user id khi đã authenticate, fallback IP |
| P0-5 | `QueryValidator` không có test | Chuyển test project sang xUnit, thêm bộ test validator (injection, multi-statement, blocklist, ApplyLimit) |
| P0-6 | `Program.cs` ~640 dòng | Tách endpoint theo nhóm vào `api/Endpoints/*.cs` (extension methods `MapXxxEndpoints`) khi thêm nhóm mới; HTML download-bridge tách ra wwwroot |

Ghi nhận nhưng KHÔNG làm trong update này (tránh phình scope): JWT cookie HttpOnly, confirmation store in-memory của `AiTokenBudgetService`, streaming rows thay vì buffer toàn bộ.

---

## Sub-project A — External Database Connections

### Mục tiêu
Người dùng cấu hình kết nối MySQL / MSSQL / PostgreSQL / BigQuery trên web UI, chọn bảng/view, hệ thống đồng bộ dữ liệu về Parquet thành dataset — từ đó mọi tính năng hiện có (query, token budget, knowledge, MCP) hoạt động y hệt dataset file.

### Quyết định kiến trúc: Sync-to-Parquet (không live query)
- **Tại sao:** tái dùng toàn bộ pipeline (DuckDB, QueryValidator, manifest, token budget); join tự nhiên với dataset Excel (Phase C); AI không thể làm nghẽn DB production của khách; chỉ cần 1 lớp read-only enforcement thay vì 4 dialect.
- **Đánh đổi:** dữ liệu trễ theo chu kỳ sync. Chấp nhận được cho use case phân tích. Live query để phase tương lai.

### Data model (migration mới)
```sql
CREATE TABLE db_connections (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    provider VARCHAR(20) NOT NULL,          -- 'mysql' | 'mssql' | 'postgresql' | 'bigquery'
    encrypted_config TEXT NOT NULL,         -- AES-GCM JSON: host/port/db/user/password hoặc BigQuery service-account JSON
    last_test_status VARCHAR(20),           -- 'ok' | 'failed'
    last_test_at TIMESTAMPTZ,
    last_test_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE datasets
    ADD COLUMN source_kind VARCHAR(20) NOT NULL DEFAULT 'file',  -- 'file' | 'external_db'
    ADD COLUMN connection_id UUID REFERENCES db_connections(id),
    ADD COLUMN sync_config JSONB,           -- danh sách bảng đã chọn, filter, row limit, schedule
    ADD COLUMN last_synced_at TIMESTAMPTZ;

CREATE TABLE dataset_sync_runs (
    id UUID PRIMARY KEY,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    status VARCHAR(20) NOT NULL,            -- 'running' | 'succeeded' | 'failed'
    started_at TIMESTAMPTZ NOT NULL,
    finished_at TIMESTAMPTZ,
    rows_synced BIGINT,
    error_message TEXT
);
```

### Thành phần mới (api/Services/Connectors/)
- `IExternalDbConnector` — interface: `TestAsync(config)`, `ListTablesAsync(config)`, `GetTableSchemaAsync(config, table)`, `StreamRowsAsync(config, table, filter, ct)`.
- 4 implementation: `MySqlDbConnector` (package MySqlConnector — đặt tên class khác package để tránh trùng), `MsSqlDbConnector` (Microsoft.Data.SqlClient), `PostgresDbConnector` (Npgsql — đã có), `BigQueryDbConnector` (Google.Cloud.BigQuery.V2).
- `DbConnectionService` — CRUD + test + mã hoá config qua `SecretProtector`. Response **không bao giờ** trả password/secret về client (chỉ metadata + masked host).
- `ExternalSyncService` + `SyncJobQueue`/`SyncHostedService` (mô phỏng ParsingJobQueue): stream rows → CSV trung gian → `ParquetWriter` → cập nhật `dataset_tables`/`dataset_columns` qua đúng flow của parsing hiện tại (tái dùng `TypeInferrer`, `HeaderNormalizer` ở mức tối thiểu — tên cột từ DB đã sạch, chỉ normalize lowercase).
- Sync đè theo kiểu **full refresh** (drop + rebuild Parquet từng bảng, atomic bằng cách ghi file mới rồi swap). Incremental sync = phase sau.
- Giới hạn an toàn: `Sync:MaxRowsPerTable` (mặc định 5.000.000), `Sync:MaxTablesPerDataset` (50), timeout per-table. Query đọc nguồn luôn là `SELECT <cols> FROM <table>` do server sinh — không nhận SQL tự do từ client ở bước sync.

### API endpoints (nhóm mới `api/Endpoints/ConnectionEndpoints.cs`, tất cả JwtOnly)
```
POST   /api/connections                 — tạo (body: name, provider, config)
GET    /api/connections                 — list (masked)
PUT    /api/connections/{id}            — sửa (config chỉ ghi đè khi gửi kèm)
DELETE /api/connections/{id}            — xoá (chặn nếu còn dataset tham chiếu)
POST   /api/connections/{id}/test       — test kết nối
GET    /api/connections/{id}/tables     — browse bảng/view + schema
POST   /api/connections/{id}/datasets   — tạo dataset external (chọn bảng, schedule) → enqueue sync
POST   /api/datasets/{id}/sync          — refresh thủ công
GET    /api/datasets/{id}/sync-runs     — lịch sử sync
```

### UI (wwwroot, giữ vanilla JS như hiện tại)
- `connections.html` — danh sách connection, nút Add/Edit/Test/Delete. Form theo provider (BigQuery: paste service-account JSON + project id; còn lại: host/port/db/user/password/SSL).
- Wizard tạo dataset từ connection: chọn bảng (checkbox, hiện row count ước tính) → đặt tên dataset → chọn schedule (manual / mỗi giờ / mỗi ngày) → tạo.
- `dashboard.html` hiện badge nguồn (📄 file / 🗄 external) + nút "Sync now" + thời gian sync cuối.

### Scheduling
`SyncSchedulerHostedService` quét mỗi phút: dataset external có `sync_config.schedule` đến hạn → enqueue. Không dùng thư viện cron ngoài; chỉ hỗ trợ `manual | hourly | daily` ở phase này.

---

## Sub-project B — Business Knowledge v2 (bộ nhớ nghiệp vụ AI cập nhật được)

### Mục tiêu
Tri thức nghiệp vụ của dataset trở thành **các entry có cấu trúc, có nguồn gốc, có lịch sử**. AI (qua MCP) được phép bổ sung/cập nhật khi phát hiện thông tin mới; user và AI khác dùng chung dataset sẽ thấy tri thức này.

### Data model
```sql
CREATE TABLE dataset_knowledge_entries (
    id UUID PRIMARY KEY,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    kind VARCHAR(30) NOT NULL,       -- 'note' | 'column_meaning' | 'business_rule' | 'metric_definition' | 'join_hint' | 'document'
    title VARCHAR(255) NOT NULL,
    content TEXT NOT NULL,           -- markdown, max 4000 ký tự/entry
    source VARCHAR(10) NOT NULL,     -- 'user' | 'ai'
    created_by TEXT NOT NULL,        -- user email hoặc "ai:<tên agent/session>"
    pinned BOOLEAN NOT NULL DEFAULT FALSE,   -- pinned luôn nằm trong context trả cho AI
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE dataset_knowledge_revisions (   -- audit: mỗi lần create/update/archive ghi 1 bản
    id UUID PRIMARY KEY,
    entry_id UUID NOT NULL REFERENCES dataset_knowledge_entries(id) ON DELETE CASCADE,
    action VARCHAR(10) NOT NULL,     -- 'create' | 'update' | 'archive'
    previous_content TEXT,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```
Field `datasets.business_knowledge` cũ: migration chuyển nội dung hiện có thành 1 entry `kind='note', source='user', pinned=true`; field giữ lại read-only một thời gian rồi bỏ.

### Quyền ghi tri thức
- JWT (web UI): full CRUD.
- PAT (`edm_pat_`): full CRUD — PAT vốn đại diện user.
- Dataset API key (`edm_`): mặc định **read-only**; thêm cột `can_write_knowledge BOOLEAN DEFAULT FALSE` vào `dataset_api_keys`, user bật khi tạo key. Đây là cách "cấp quyền dataset" cho AI/người khác cùng ghi tri thức.

### Guardrail chống AI spam/ghi đè bậy
- Tối đa 200 entry active/dataset; content ≤ 4000 ký tự; rate limit ghi 30/phút/principal.
- Entry do AI tạo gắn `source='ai'` — UI hiển thị badge "AI" + màn hình review, user có thể sửa/pin/archive.
- AI **không được xoá cứng** — chỉ archive (giữ audit).
- Mọi thay đổi ghi revision.

### API + MCP tools
```
GET    /api/datasets/{id}/knowledge            — list (filter kind, include_archived)
POST   /api/datasets/{id}/knowledge            — tạo entry
PUT    /api/datasets/{id}/knowledge/{entryId}  — cập nhật
DELETE /api/datasets/{id}/knowledge/{entryId}  — archive (JWT mới được hard-delete)
```
Policy: `KnowledgeWrite` — chấp nhận JWT, PAT, dataset key có `can_write_knowledge` đúng dataset.

MCP tools mới trong `tools.md`:
- `get_dataset_knowledge` — đọc tri thức (kèm hướng dẫn: đọc trước khi phân tích).
- `save_dataset_knowledge` — description hướng dẫn AI: "Khi người dùng cung cấp thông tin nghiệp vụ mới, định nghĩa metric, quy tắc, hoặc bạn phát hiện mapping cột quan trọng — lưu lại để phiên sau và người dùng khác tái sử dụng. Ghi ngắn gọn, 1 fact/entry."
- `update_dataset_knowledge` — cập nhật entry đã có (theo id).

UI: tab "Tri thức" trong `dataset-detail.html` — list entry theo kind, badge nguồn, nút pin/sửa/archive, textarea tạo mới.

---

## Sub-project C — Structured Context API + Multi-dataset Join

### Mục tiêu
1. Thay việc AI phải tải `manifest.md` bằng **API context JSON có cấu trúc, lọc được, cap token**.
2. Cho phép 1 query SQL join dữ liệu của 2–3 dataset (file + external đều được).

### C1. Context API (thay manifest.md làm nguồn chính cho AI)
```
GET /api/context?dataset_ids={id1},{id2}&tables=orders,customers&detail=summary|full
```
Response (JSON, snake_case):
```json
{
  "datasets": [{
    "dataset_id": "…", "name": "…", "alias": "sales",     // alias dùng trong SQL multi-dataset
    "source_kind": "file", "last_synced_at": null,
    "tables": [{
      "table_name": "raw_orders", "qualified_name": "sales.raw_orders",
      "row_count": 12000,
      "columns": [{ "name": "order_id", "type": "VARCHAR", "display_name": "Mã đơn", "aliases": ["ma don"], "sample_values": ["…"] }]
    }],
    "knowledge": [{ "kind": "metric_definition", "title": "…", "content": "…", "source": "ai", "pinned": true }]
  }],
  "join_hints": [],
  "token_estimate": 1830
}
```
- `detail=summary`: bỏ sample_values + aliases, knowledge chỉ pinned → dùng cho dataset nhiều bảng (giải quyết Scenario B trong README).
- Cap: nếu ước tính vượt `Query:SafeMaxTokens`, tự hạ xuống summary + trả warning kèm hướng dẫn lọc `tables=`.
- `manifest.md` vẫn generate để download cho người — nhưng MCP tool `get_dataset_schema` chuyển sang gọi context API.

### C2. Multi-dataset query
```
POST /api/query
{ "dataset_ids": ["id1", "id2"], "sql": "SELECT … FROM sales.raw_orders o JOIN crm.raw_customers c ON …", "options": { … } }
```
- Mỗi dataset gắn 1 **alias** (slug từ tên, lưu vào `datasets.alias`, unique per user, sinh khi tạo dataset + backfill migration).
- `DuckDbQueryService` mở rộng: với mỗi dataset → `CREATE SCHEMA <alias>` + `CREATE VIEW <alias>.<table>` từ Parquet. Single-dataset giữ nguyên view không schema (backward compatible, endpoint cũ giữ nguyên).
- Giới hạn: tối đa `Query:MaxDatasetsPerQuery` (mặc định 3) dataset/query; tất cả phải thuộc user và `ready`.
- Auth: JWT/PAT như thường; dataset-scoped key **không** dùng được multi-dataset (khác scope) — trả lỗi rõ ràng.
- QueryValidator giữ nguyên (SELECT/WITH only) — không cần biết schema.
- Token budget + query_logs áp dụng y hệt (log thêm cột `dataset_ids`).

MCP tools: `get_context` (thay `get_dataset_schema`), `query_datasets` (nhận `dataset_ids[]`; giữ `query_dataset` cũ cho tương thích).

### C3. Tài liệu tri thức (knowledge documents)
- Upload file tài liệu (.md/.txt, ≤1MB) vào dataset → lưu nguyên văn + tách thành entries `kind='document'` theo heading (mỗi section 1 entry, giữ title = heading).
- MCP tool `search_knowledge(dataset_ids, query)` — Postgres full-text search (`to_tsvector('simple', …)` vì nội dung tiếng Việt) trên title+content, trả top 5 entry. Không dùng embeddings ở phase này.

---

## Thứ tự thực hiện & phạm vi PR

| Phase | Nội dung | Ước lượng |
|---|---|---|
| 0 | P0-1…P0-6 (bảo mật MCP, migrations, SecretProtector, forwarded headers, xUnit + QueryValidator tests, tách endpoints) | 1 PR nền tảng |
| A | Connections + connectors + sync pipeline + UI cấu hình | 2–3 PR (backend connectors → sync pipeline → UI) |
| B | Knowledge v2: schema + API + MCP tools + UI tab | 1–2 PR |
| C | Context API → multi-dataset query → documents + search | 2–3 PR |

## Testing
- xUnit: QueryValidator (P0), SecretProtector round-trip, connector config validation (không cần DB thật), alias/slug generator, context API shaping + token cap, multi-dataset view setup (DuckDB in-memory với Parquet fixture), knowledge guardrails.
- Integration thủ công: docker-compose + 1 MySQL container mẫu cho connector test.

## Rủi ro & câu hỏi mở (cần user xác nhận)
1. **Sync-to-Parquet vs live query** — thiết kế này chọn sync; nếu bạn cần dữ liệu real-time thì Phase A đổi hướng đáng kể.
2. BigQuery: sync full-table có thể tốn phí scan — cần bắt buộc row limit/filter cho BigQuery? (thiết kế hiện để limit mặc định 5M rows).
3. Giới hạn 10 dataset/user hiện hardcode — external datasets có tính vào limit này không? (thiết kế: có, nhưng nâng thành config `Limits:MaxDatasetsPerUser`).
4. `/mcp/{userId}` cũ sẽ bị vô hiệu — client Claude hiện có của bạn phải cấu hình lại dùng PAT. OK không?
