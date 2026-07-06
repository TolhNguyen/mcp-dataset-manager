# Big Update Design — External DB (live query), Knowledge Memory, Structured Context + Join, AI Dashboards

Ngày: 2026-07-06 (bản 3 — thêm Sub-project D: dashboard + widget do AI tạo; bổ sung flow schema bootstrap)
Trạng thái: chờ user duyệt lần cuối

## Các quyết định đã chốt

| Quyết định | Lựa chọn |
|---|---|
| Kết nối DB ngoài | **Live query** — tuyệt đối không lưu dữ liệu người dùng (bảo mật, tính đúng đắn, chi phí). Chỉ lưu metadata schema |
| Tri thức nghiệp vụ | Memory-style: AI agent tự lưu qua MCP tool khi user cung cấp/sửa thông tin; hướng dẫn nằm trong tool description + `memory_instructions` |
| Tìm kiếm tri thức | Postgres `unaccent` + `pg_trgm` + full-text — chi phí 0, không embeddings |
| Tương thích ngược | Không cần — bỏ `/mcp/{userId}`, bỏ field `business_knowledge` cũ, user/dataset cũ chấp nhận mất |
| Giới hạn dataset | Per-user, cột `users.max_datasets` DEFAULT 10, operator chỉnh trực tiếp |
| Cross-source join (Excel × DB ngoài, DB × DB khác connection) | **Phase sau.** Update này: join trong cùng connection (DB nguồn tự join) + join giữa các dataset Excel (DuckDB) |
| Dashboard | AI tạo widget (saved query + chart) qua MCP; dashboard user tự chạy lại query — không cần chat lại. Xem dashboard yêu cầu JWT; share link công khai để phase sau |
| Schema bootstrap | Khi kết nối DB: đọc toàn bộ bảng + cột + kiểu + **2 dòng mẫu/bảng** làm gợi ý cho AI trước khi lập plan/viết SQL |

---

## Bối cảnh

EDM hiện tại: upload Excel/CSV → parse → Parquet → query qua DuckDB, expose cho AI qua MCP bridge. Ba giới hạn:

1. Không kết nối được MySQL/MSSQL/PostgreSQL/BigQuery.
2. `business_knowledge` là 1 field TEXT 10k, chỉ sửa được qua web (JwtOnly) — AI qua MCP không bổ sung được tri thức.
3. Schema trả cho AI là nguyên file `manifest.md` — token-heavy, khó parse; mỗi query chỉ 1 dataset, không join.

## Phase 0 — Nợ kỹ thuật phải trả trước

Bắt buộc vì Phase A lưu credential DB thật của người dùng:

| # | Vấn đề | Cách xử lý |
|---|---|---|
| P0-1 | `ApiKeyAuthenticationHandler` chấp nhận GUID trần làm key (`insecure_user_id`) → biết user_id = toàn quyền. Bridge dùng `/mcp/{userId}` làm key | **Xoá nhánh GUID.** MCP HTTP mode: endpoint `/mcp`, bắt buộc `Authorization: Bearer edm_pat_...`, bridge forward `${request.authorization}`. Xoá hẳn `/mcp/{userId}` (không cần back-compat) |
| P0-2 | Không có migration framework | Bảng `schema_migrations` + runner chạy script SQL đánh số trong `api/Migrations/`. Schema hiện tại = migration 0001 |
| P0-3 | Chưa có chỗ lưu secret | `SecretProtector`: AES-256-GCM, master key từ env `EDM_ENCRYPTION_KEY` (≥32 bytes, fail-fast như JWT_KEY) |
| P0-4 | Rate limit theo IP sau Caddy → mọi user chung 1 bucket | `UseForwardedHeaders` + partition theo user id khi đã authenticate, fallback IP |
| P0-5 | `QueryValidator` không có test | Chuyển test project sang xUnit; test validator (injection, multi-statement, blocklist, ApplyLimit) |
| P0-6 | `Program.cs` ~640 dòng | Tách endpoints vào `api/Endpoints/*.cs`; HTML download-bridge ra wwwroot |

Ghi nhận, KHÔNG làm đợt này: JWT cookie HttpOnly, confirmation store in-memory, streaming rows.

---

## Phase 0b — OAuth authorization cho MCP (UX như Pancake POS, không bắt user dán token)

### Vấn đề
Sau Phase 0, client MCP phải cấu hình thủ công `Authorization: Bearer edm_pat_...` — khó dùng với người dùng phổ thông. Chuẩn MCP hỗ trợ OAuth 2.1: Claude.ai tự mở trang authorize của server, user bấm cho phép, token được cấp tự động (như màn hình authorize của Pancake POS MCP).

### Thiết kế: OAuth là lớp UX tự động phát PAT
Access token phát ra qua OAuth **chính là một PAT `edm_pat_...`** được tạo tự động (tên: `Claude MCP (OAuth) <ngày>`). Nhờ đó:
- Bridge/API giữ nguyên toàn bộ cơ chế bearer PAT của Phase 0.
- User thấy và thu hồi được token trong màn hình quản lý PAT hiện có.
- Không cần refresh token (PAT không hết hạn, thu hồi thủ công) — YAGNI.

Hơn Pancake một bậc: trang authorize KHÔNG bắt dán API key — user đăng nhập bằng tài khoản EDM (hoặc dùng session cookie sẵn có) rồi bấm **Cho phép**.

### Flow chuẩn MCP (OAuth 2.1)
1. Claude gọi `/mcp` không token → bridge trả 401 kèm `WWW-Authenticate: Bearer resource_metadata="https://<domain>/.well-known/oauth-protected-resource"`.
2. Claude đọc Protected Resource Metadata (RFC 9728) → biết authorization server = cùng domain.
3. Claude đọc `/.well-known/oauth-authorization-server` (RFC 8414) → biết các endpoint.
4. Dynamic Client Registration (RFC 7591): `POST /api/oauth/register` → cấp `client_id` (public client, không secret).
5. Mở `GET /oauth/authorize?response_type=code&client_id=…&redirect_uri=…&code_challenge=…&code_challenge_method=S256&state=…` → user đăng nhập + bấm Cho phép → redirect về Claude kèm `code`.
6. `POST /api/oauth/token` (form-urlencoded, kèm `code_verifier` PKCE) → trả `{access_token: "edm_pat_…", token_type: "Bearer"}`.

### Bảo mật
- **PKCE S256 bắt buộc** (từ chối `plain` và thiếu challenge). Public client, không client_secret.
- Authorization code: random 256-bit, lưu **SHA-256 hash**, TTL 5 phút, **dùng 1 lần**, gắn chặt (client_id, redirect_uri, code_challenge, user_id).
- `redirect_uri` phải khớp CHÍNH XÁC URI đã đăng ký; chỉ nhận `https://` (và `http://localhost`/`127.0.0.1` cho dev).
- Endpoint register/token nằm sau rate limit `auth` (10/phút/IP).
- Bảng mới: `oauth_clients`, `oauth_authorization_codes` (migration 0002).
- Cấu hình public URL qua env `EDM_PUBLIC_URL` (issuer trong metadata; bridge dùng cho WWW-Authenticate).

Routing (Caddy hiện có, không đổi): `/mcp*` → bridge; `/.well-known/*`, `/oauth/authorize`, `/api/oauth/*` → API.

---

## Sub-project A — External Database Connections (live query)

### Nguyên tắc thiết kế
1. **Không lưu dữ liệu người dùng.** Chỉ lưu: connection config (mã hoá), metadata schema (tên bảng/cột/kiểu — cần cho AI viết SQL), query logs (SQL text, không lưu kết quả).
2. **AI viết SQL theo dialect gốc** (MySQL / T-SQL / PostgreSQL / BigQuery SQL). Context API ghi rõ `dialect` để AI biết. Model hiện nay viết tốt cả 4 dialect.
3. **Read-only 3 lớp:**
   - Lớp 1 — `QueryValidator` mở rộng theo dialect: giữ nguyên tắc strip strings/comments → token đầu phải SELECT/WITH, blocklist theo dialect (thêm `EXEC`, `MERGE`, `BULK`, `OPENROWSET`, `xp_` cho MSSQL; `LOAD DATA`, `OUTFILE`, `HANDLER` cho MySQL; DML/DDL cho BigQuery…).
   - Lớp 2 — session read-only ở driver khi provider hỗ trợ: PostgreSQL `default_transaction_read_only=on`; MySQL `SET SESSION TRANSACTION READ ONLY`; BigQuery job config không cho DML; MSSQL không có session-level → dựa lớp 1 + 3.
   - Lớp 3 — UI bắt buộc hiển thị khuyến cáo đậm: "Hãy cấp tài khoản DB chỉ có quyền SELECT". Nút test connection kiểm tra và cảnh báo nếu account có quyền ghi (thử `SELECT` các bảng quyền hệ thống tuỳ provider, best-effort).
4. **Bảo vệ DB của khách:** timeout per-query (mặc định 30s), row cap áp bằng cách wrap theo dialect (`LIMIT n` / `SELECT TOP n` / OFFSET-FETCH), giới hạn concurrent query per-connection (mặc định 3), connection pool nhỏ.

### Data model (migrations mới)
```sql
CREATE TABLE db_connections (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    provider VARCHAR(20) NOT NULL,          -- 'mysql' | 'mssql' | 'postgresql' | 'bigquery'
    encrypted_config TEXT NOT NULL,         -- AES-GCM JSON: host/port/db/user/password/ssl hoặc BigQuery service-account JSON + project
    last_test_status VARCHAR(20),
    last_test_at TIMESTAMPTZ,
    last_test_error TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE datasets
    ADD COLUMN source_kind VARCHAR(20) NOT NULL DEFAULT 'file',  -- 'file' | 'external_db'
    ADD COLUMN connection_id UUID REFERENCES db_connections(id),
    ADD COLUMN external_tables JSONB,       -- danh sách bảng user đã chọn expose cho AI
    ADD COLUMN include_samples BOOLEAN NOT NULL DEFAULT TRUE,  -- tắt cho dữ liệu nhạy cảm
    ADD COLUMN schema_refreshed_at TIMESTAMPTZ;

ALTER TABLE dataset_tables ADD COLUMN sample_rows JSONB;  -- 2 dòng mẫu/bảng (gợi ý cho AI)

ALTER TABLE users ADD COLUMN max_datasets INT NOT NULL DEFAULT 10;
```
Dataset external tái dùng `dataset_tables` + `dataset_columns` để lưu **metadata schema** (không có parquet — `data_file_name` để rỗng). Nút "Refresh schema" đọc lại metadata từ DB nguồn.

### Flow schema bootstrap (khi tạo connection / dataset external)
Ngay khi kết nối thành công, hệ thống tự động đọc và lưu làm gợi ý cho AI:
1. **Toàn bộ bảng/view** trong database (qua `information_schema` / `INFORMATION_SCHEMA.TABLES` / BigQuery dataset listing).
2. **Toàn bộ cột + kiểu dữ liệu + nullable** mỗi bảng.
3. **2 dòng dữ liệu mẫu mỗi bảng** (`SELECT * … LIMIT 2` theo dialect) → lưu vào `dataset_tables.sample_rows`.

Không lấy row count (dữ liệu nguồn thay đổi liên tục, con số không có ý nghĩa) — `dataset_tables.row_count` để 0 và Context API bỏ field này với dataset external.

Bộ metadata này được Context API (C1) trả cho AI **trước khi AI lập plan hoặc viết SQL** — tool description bắt buộc AI gọi `get_context` trước query đầu tiên. Sample rows có toggle `include_samples=false` per dataset cho dữ liệu nhạy cảm. Đây là dữ liệu duy nhất được lưu (2 dòng/bảng) — chấp nhận được vì mục đích gợi ý, user kiểm soát được bằng toggle.

### Thành phần mới (`api/Services/Connectors/`)
- `IExternalDbConnector`: `TestAsync`, `ListTablesAsync`, `GetTableSchemaAsync`, `ExecuteQueryAsync(config, sql, maxRows, timeout, ct)` — trả reader stream, map về compact_table như DuckDB path.
- 4 implementation: `MySqlDbConnector` (MySqlConnector), `MsSqlDbConnector` (Microsoft.Data.SqlClient), `PostgresDbConnector` (Npgsql), `BigQueryDbConnector` (Google.Cloud.BigQuery.V2).
- `DbConnectionService`: CRUD + test; config mã hoá qua `SecretProtector`; response không bao giờ chứa secret (host masked một phần).
- `ExternalQueryService`: đối trọng của `DuckDbQueryService` — validate theo dialect → wrap row cap → execute qua connector → token budget + query_logs y hệt path DuckDB. `POST /api/datasets/{id}/query` route theo `source_kind`.
- Sample data cho external: theo flow schema bootstrap ở trên (2 dòng mẫu/bảng), refresh cùng lúc với schema.

### API endpoints (`api/Endpoints/ConnectionEndpoints.cs`, JwtOnly)
```
POST   /api/connections                 GET    /api/connections
PUT    /api/connections/{id}            DELETE /api/connections/{id}   (chặn nếu còn dataset tham chiếu)
POST   /api/connections/{id}/test       GET    /api/connections/{id}/tables
POST   /api/connections/{id}/datasets   — tạo dataset external (chọn bảng) → đọc schema metadata → status 'ready' ngay
POST   /api/datasets/{id}/refresh-schema
```

### UI (vanilla JS như hiện tại)
- `connections.html`: list + form theo provider (BigQuery: paste service-account JSON; còn lại: host/port/db/user/password/SSL). Khuyến cáo read-only account hiển thị đậm.
- Wizard tạo dataset: chọn connection → tick bảng/view muốn expose → đặt tên → tạo (ready ngay, không cần chờ parse).
- `dashboard.html`: badge nguồn (📄 file / 🗄 mysql / …).

---

## Sub-project B — Knowledge Memory (bộ nhớ nghiệp vụ AI tự cập nhật)

### Concept
Giống memory của AI assistant: mỗi dataset có một bộ nhớ gồm các **entry nhỏ, mỗi entry 1 fact**. Khi user làm việc với AI và cung cấp thông tin mới / sửa sai / định nghĩa metric, AI agent gọi MCP tool lưu lại. AI khác hoặc user khác được cấp quyền dataset sẽ dùng chung bộ nhớ này.

Cơ chế "dạy" agent lưu memory (không kiểm soát được client AI nên dùng 2 tầng):
1. **Tool description của `save_dataset_knowledge`** — chính là prompt: "Khi người dùng cung cấp thông tin nghiệp vụ, sửa cách hiểu của bạn, định nghĩa metric/quy tắc, hoặc bạn phát hiện mapping cột quan trọng — hãy lưu lại bằng tool này. 1 fact/entry, ngắn gọn, tiếng Việt."
2. **`memory_instructions` trong response context API** — mỗi lần AI lấy context đều thấy nhắc: bộ nhớ hiện có N entries + hướng dẫn cập nhật khi có thông tin mới.

### Data model
```sql
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE dataset_knowledge_entries (
    id UUID PRIMARY KEY,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    kind VARCHAR(30) NOT NULL,       -- 'note' | 'column_meaning' | 'business_rule' | 'metric_definition' | 'join_hint' | 'document'
    title VARCHAR(255) NOT NULL,
    content TEXT NOT NULL,           -- markdown, ≤ 4000 ký tự
    source VARCHAR(10) NOT NULL,     -- 'user' | 'ai'
    created_by TEXT NOT NULL,        -- email hoặc "ai:<key name>"
    pinned BOOLEAN NOT NULL DEFAULT FALSE,   -- pinned luôn có mặt trong context
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_knowledge_search ON dataset_knowledge_entries
    USING gin ((title || ' ' || content) gin_trgm_ops);

CREATE TABLE dataset_knowledge_revisions (
    id UUID PRIMARY KEY,
    entry_id UUID NOT NULL REFERENCES dataset_knowledge_entries(id) ON DELETE CASCADE,
    action VARCHAR(10) NOT NULL,     -- 'create' | 'update' | 'archive'
    previous_content TEXT,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```
Field `datasets.business_knowledge` cũ: **xoá** (không cần back-compat). Migration best-effort: nếu có nội dung thì chuyển thành 1 entry `note` pinned.

### Tìm kiếm (chi phí 0)
`search_knowledge(dataset_ids, query)`:
- Chuẩn hoá query + nội dung qua `unaccent` (gõ "doanh thu" khớp "Doanh Thu", "doanh-thu", không dấu).
- Xếp hạng: `similarity()` của pg_trgm + ưu tiên khớp title + ưu tiên pinned. Trả top 5.
- Không embeddings/vector — với ≤200 entries/dataset, trigram đủ chính xác và miễn phí. Nếu sau này cần semantic search thật, nâng cấp lên pgvector (đã chừa chỗ: entry độc lập, chỉ thêm cột embedding).

### Quyền ghi
- JWT / PAT: full CRUD.
- Dataset API key: read mặc định; cột mới `dataset_api_keys.can_write BOOLEAN DEFAULT FALSE` — user bật khi tạo key để cấp quyền ghi cho agent (phạm vi: knowledge entries + dashboard widgets của đúng dataset đó).
- Policy mới `KnowledgeWrite` cho các endpoint ghi.

### Guardrails
- ≤200 entry active/dataset; content ≤4000 ký tự; rate limit ghi 30/phút/principal.
- AI không hard-delete — chỉ archive. Mọi thay đổi ghi revision.
- Entry `source='ai'` hiển thị badge trong UI; user review/sửa/pin/archive.

### API + MCP tools
```
GET/POST   /api/datasets/{id}/knowledge
PUT/DELETE /api/datasets/{id}/knowledge/{entryId}    (DELETE = archive; hard-delete JwtOnly)
GET        /api/knowledge/search?dataset_ids=…&q=…
```
MCP tools: `get_dataset_knowledge`, `save_dataset_knowledge`, `update_dataset_knowledge`, `search_knowledge`.

UI: tab "Tri thức" trong `dataset-detail.html` — list theo kind, badge nguồn, pin/sửa/archive, form tạo mới.

---

## Sub-project C — Structured Context API + Multi-dataset Query

### C1. Context API (thay manifest.md làm nguồn chính cho AI)
```
GET /api/context?dataset_ids={id1},{id2}&tables=orders,customers&detail=summary|full
```
```json
{
  "datasets": [{
    "dataset_id": "…", "name": "…", "alias": "sales",
    "source_kind": "external_db", "provider": "mysql", "dialect": "mysql",
    "tables": [{
      "table_name": "orders", "qualified_name": "sales.orders",
      "columns": [{ "name": "order_id", "type": "VARCHAR", "display_name": "Mã đơn", "aliases": ["ma don"] }],
      "sample_rows": [["ORD-001", "2026-01-03", 1200000], ["ORD-002", "2026-01-03", 450000]]
    }],
    "knowledge": [{ "kind": "metric_definition", "title": "…", "content": "…", "source": "ai", "pinned": true }]
  }],
  "memory_instructions": "Bộ nhớ dataset có 12 entries. Khi người dùng cung cấp thông tin nghiệp vụ mới hoặc sửa cách hiểu của bạn, hãy lưu bằng save_dataset_knowledge.",
  "token_estimate": 1830
}
```
- `detail=summary`: bỏ sample_values + aliases, knowledge chỉ pinned — cho dataset nhiều bảng.
- Nếu ước tính vượt `Query:SafeMaxTokens` → tự hạ summary + warning kèm gợi ý lọc `tables=`.
- `manifest.md`: vẫn generate cho dataset file (người dùng tải về đọc), nhưng MCP tool schema chuyển sang context API. Không generate manifest cho dataset external.

### C2. Multi-dataset query (chỉ dataset Parquet/file)
```
POST /api/query
{ "dataset_ids": ["id1", "id2"], "sql": "SELECT … FROM sales.raw_orders o JOIN crm.raw_customers c ON …", "options": { … } }
```
- Mỗi dataset có **alias** (slug từ tên, cột `datasets.alias`, unique per user, backfill migration).
- DuckDB: mỗi dataset → `CREATE SCHEMA <alias>` + view per table từ Parquet. Endpoint cũ single-dataset giữ nguyên.
- Giới hạn `Query:MaxDatasetsPerQuery` (mặc định 3); tất cả thuộc user + `ready` + `source_kind='file'`.
- Nếu dataset_ids chứa external → lỗi rõ ràng: "External datasets chưa hỗ trợ cross-dataset join; hãy query trực tiếp dataset đó (các bảng trong cùng connection join được với nhau)."
- Dataset-scoped key không dùng được multi-dataset. Token budget + query_logs như cũ (log thêm `dataset_ids`).

### C3. Tài liệu tri thức
- Upload .md/.txt (≤1MB) vào dataset → tách theo heading thành entries `kind='document'` (mỗi section 1 entry, title = heading) → tìm qua `search_knowledge` như mọi entry khác. AI không phải đọc nguyên file MD nữa.

### MCP tools sau update (tools.md viết lại)
`list_datasets`, `get_context` (thay get_dataset_schema), `query_dataset` (single, cả file lẫn external), `query_datasets` (multi, file-only), `get_dataset_knowledge`, `save_dataset_knowledge`, `update_dataset_knowledge`, `search_knowledge`, `create_dashboard_widget`, `list_dashboard_widgets`, `update_dashboard_widget`, `upload_dataset`, `get_dataset`, `delete_dataset`.

---

## Sub-project D — Dashboard thống kê + widget do AI tạo

### Concept
User chat với AI một lần để xây dashboard; AI gọi MCP tool tạo **widget** = saved query (SQL đã đóng băng) + cấu hình chart. Từ đó màn hình dashboard tự chạy lại query mỗi lần xem/refresh — với dataset external là query live vào DB nguồn nên dữ liệu luôn mới, **không cần chat lại với AI**.

### Data model
```sql
CREATE TABLE dashboards (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    description TEXT,
    created_by TEXT NOT NULL,               -- email hoặc "ai:<key name>"
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE dashboard_widgets (
    id UUID PRIMARY KEY,
    dashboard_id UUID NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    title VARCHAR(255) NOT NULL,
    sql TEXT NOT NULL,                      -- validated read-only, đóng băng tại thời điểm lưu
    chart_type VARCHAR(20) NOT NULL,        -- 'table' | 'line' | 'bar' | 'pie' | 'stat'
    chart_config JSONB,                     -- mapping cột → trục/series, format số…
    refresh_interval_sec INT NOT NULL DEFAULT 60,   -- min 30
    position INT NOT NULL DEFAULT 0,
    source VARCHAR(10) NOT NULL,            -- 'user' | 'ai'
    created_by TEXT NOT NULL,
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Mô hình bảo mật (điểm mấu chốt của tính năng)
1. **SQL đóng băng tại thời điểm lưu.** Browser chỉ gọi `GET /api/dashboards/{id}/widgets/{wid}/data` — không bao giờ gửi SQL. Đổi SQL phải qua PUT (JWT) hoặc MCP tool có quyền ghi.
2. **Validate 2 lần:** lúc lưu (QueryValidator theo dialect của dataset + chạy thử `LIMIT 1` để bắt lỗi sớm) và lúc mỗi lần thực thi (phòng row trong Postgres bị sửa trực tiếp). Luôn read-only, luôn row cap (`Dashboard:MaxRowsPerWidget`, mặc định 1000 — kết quả widget đi thẳng ra browser, không qua AI nên không tốn token), luôn timeout.
3. **Widget AI tạo gắn `source='ai'`** — UI hiện badge, user review/sửa/archive. Giới hạn `Dashboard:MaxWidgetsPerDashboard` (20) và 10 dashboard/user.
4. **Chống nện DB khách:** `refresh_interval_sec` tối thiểu 30s; kết quả cache **in-memory** (IMemoryCache, TTL = refresh interval) — nhiều lần xem trong TTL không đánh thêm query nào vào DB nguồn; không ghi đĩa (giữ nguyên tắc không lưu dữ liệu). Widget data endpoint có rate limit riêng.
5. **Xem dashboard yêu cầu JWT.** Share link công khai (signed URL, read-only) để phase sau — tách riêng phần rủi ro nhất.
6. Quyền tạo widget qua MCP: PAT full; dataset-scoped key cần flag `can_write` (dùng chung flag với knowledge — đổi tên `can_write_knowledge` thành `can_write`, phạm vi: knowledge + widgets của đúng dataset đó).

### API endpoints
```
POST/GET      /api/dashboards                      PUT/DELETE /api/dashboards/{id}
POST/GET      /api/dashboards/{id}/widgets         PUT/DELETE /api/dashboards/{id}/widgets/{wid}
GET           /api/dashboards/{id}/widgets/{wid}/data    — thực thi saved SQL, trả compact_table (cache theo TTL)
```
Ghi: JWT hoặc `KnowledgeWrite`-tương-đương (PAT / key có `can_write`). Đọc data: JWT.

### MCP tools
- `create_dashboard_widget(dashboard_name, dataset_id, title, sql, chart_type, chart_config)` — tự tạo dashboard nếu chưa có theo tên. Description hướng dẫn AI: "Khi người dùng muốn theo dõi một chỉ số thường xuyên, tạo widget để họ xem realtime trên dashboard mà không cần hỏi lại bạn."
- `list_dashboard_widgets`, `update_dashboard_widget` (archive qua update).

### UI
- `dashboards.html`: danh sách dashboard → grid widget; render chart bằng **Chart.js self-host** trong wwwroot (không CDN); auto-refresh theo `refresh_interval_sec` bằng polling; badge "AI" cho widget máy tạo; nút sửa/di chuyển/archive.
- Widget lỗi (bảng bị đổi schema, connection chết) hiển thị lỗi trong ô widget kèm nút "Nhờ AI sửa" (copy prompt gợi ý).

---

## Thứ tự thực hiện

| Phase | Nội dung | PR |
|---|---|---|
| 0 | P0-1…P0-6 | 1 PR |
| A | Connections CRUD + connectors + schema bootstrap + ExternalQueryService + UI | 2–3 PR (connector core → query path → UI) |
| B | Knowledge memory: schema + API + MCP tools + UI tab | 1–2 PR |
| C | Context API → multi-dataset query → documents + search | 2–3 PR |
| D | Dashboards: schema + widget API + execution/cache + MCP tools + UI | 2 PR (backend → UI) |

B và C có thể song song sau Phase 0; A độc lập sau Phase 0; D cần A (query external) + nên sau C (AI dùng context để viết SQL widget chuẩn).

## Testing
- xUnit: QueryValidator per-dialect (trọng tâm — đây là lớp an ninh), SecretProtector round-trip, row-cap wrapper per dialect, alias/slug, context shaping + token cap, knowledge guardrails + search ranking, multi-dataset DuckDB schema setup (Parquet fixture), widget save-time + execution-time validation, cache TTL.
- Integration: docker-compose thêm profile `dev` với MySQL + MSSQL container mẫu để test connector thật.

## Rủi ro còn lại
1. MSSQL không có session read-only → phụ thuộc validator + read-only account. Giảm thiểu: blocklist T-SQL đầy đủ + cảnh báo UI đậm + test injection kỹ.
2. BigQuery tính phí theo bytes scanned — mỗi query của AI là tiền của khách. Giảm thiểu: hiển thị `total_bytes_processed` trong response, hỗ trợ `maximum_bytes_billed` trong connection config (mặc định 1GB).
3. Live query nghĩa là DB khách chậm → AI chờ. Timeout 30s + thông báo lỗi rõ để AI thu hẹp query.
4. Dashboard widgets tạo tải định kỳ lên DB khách (mỗi widget 1 query/refresh). Giảm thiểu: min interval 30s, cache in-memory theo TTL, concurrent limit per-connection dùng chung với query path.
5. 2 dòng sample rows/bảng là dữ liệu người dùng được lưu (duy nhất). Kiểm soát: toggle `include_samples` per dataset, xoá sạch khi xoá dataset, ghi rõ trong UI khi tạo connection.
