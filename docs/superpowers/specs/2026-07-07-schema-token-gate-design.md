# Schema-Token Gate Design — Ép AI đọc skill + schema trước khi query, lỗi tự sửa được, chống bịa số liệu

Ngày: 2026-07-07 (bản 2 — 2026-07-08: thêm Query Guide skill + guide token; bỏ dataset API key, thay bằng toggle ghi tri thức)
Trạng thái: chờ user duyệt

## Vấn đề

Sau khi connector claude.ai hoạt động (15 tools), quan sát thực tế cho thấy các lỗi hành vi của AI client:

1. **Bỏ qua schema**: `get_context` đã ghi "ALWAYS call this first" nhưng Claude vẫn viết SQL theo trí nhớ/manifest cũ — hướng dẫn mềm trong description không đủ.
2. **Không biết cách dùng hệ thống**: Claude không biết khi nào đọc schema, khi nào xem dữ liệu mẫu, khi nào query, dataset loại nào dùng cú pháp nào — mỗi phiên mới lại mò từ đầu.
3. **Lỗi trả về không tự sửa được**: MSSQL chỉ báo `Invalid object name 'dbo.sync_fb_campaigns_report_days'` (nguyên nhân thật: viết `[dbo.table]` — bracket bao cả schema lẫn tên bảng thành 1 identifier); BigQuery chỉ báo `Syntax error: Unexpected end of script` (nguyên nhân thật: gửi CTE `WITH ... AS (...)` thiếu câu SELECT cuối, kèm tham số `@run_date` không tồn tại). AI không có thông tin để sửa nên loay hoay hoặc bỏ cuộc.
4. **Bịa số liệu khi lỗi**: query fail → AI tự chế số liệu cho báo cáo thay vì báo lỗi cho user.
5. **Dataset API key cầu kỳ**: UI "API key cho Claude / MCP" per-dataset (kèm checkbox can_write từng key) thừa từ khi có OAuth/PAT — quyền AI ghi tri thức chỉ cần 1 công tắc.

## Các quyết định đã chốt

| Quyết định | Lựa chọn |
|---|---|
| Mức ép buộc | **Chặn cứng** ở server + error chi tiết + chỉ thị chống bịa (user chọn phương án đầy đủ nhất) |
| Cơ chế gate | **Chuỗi proof-of-read stateless, 2 tầng**: đọc Query Guide → nhận `guide_token` → `get_context` (nộp guide_token) → nhận `schema_token` → query (nộp schema_token). Không lưu state phiên |
| "Câu hỏi bí mật" | Chính là `guide_token` nằm trong nội dung guide — phiên MCP mới không có token, tool đầu tiên bị chặn kèm hướng dẫn đọc guide; đọc xong tự khắc "trả lời được" |
| Phạm vi gate | Chỉ request xác thực bằng **PAT/API-key** (đường MCP). JWT (web UI, trang "Thử truy vấn SQL") giữ nguyên, không cần token |
| Tools phải nộp schema_token | `query_dataset`, `query_datasets`, `create_dashboard_widget`, `update_dashboard_widget` (mọi chỗ AI viết SQL) |
| Tool phải nộp guide_token | `get_context` |
| Nguồn schema cho gợi ý lỗi | Bảng `dataset_tables` + `dataset_columns` đã lưu sẵn — **không** gọi vào DB nguồn khi enrich lỗi |
| Quyền AI ghi tri thức | **Bỏ hẳn dataset API key** (service + endpoints + UI). Thay bằng toggle per-dataset `ai_can_write_knowledge`, mặc định BẬT (memory-style knowledge là tính năng lõi; user tắt cho dataset nhạy cảm) |
| Ngôn ngữ chỉ thị cho AI | Tiếng Anh (model tuân thủ tốt hơn), thông báo lỗi cho user giữ nguyên như hiện tại |

## Thành phần 0 — Query Guide skill + guide token

### Nội dung guide

Tool MCP mới `get_query_guide` (GET `/api/query-guide`, auth PAT) trả về markdown + token:

```json
{ "guide_token": "gd_4b7e01c2a9f3", "content": "<markdown>" }
```

Guide là "skill" dạy AI dùng EDM, gồm các mục:

1. **Workflow bắt buộc**: list_datasets → get_context (nộp guide_token) → query (nộp schema_token). Khi nào đọc schema (luôn, trước query đầu tiên của mỗi dataset; đọc lại khi gặp `SCHEMA_CHANGED`), khi nào xem sample rows (đủ để hiểu format — không query thăm dò từng cột), khi nào query thật.
2. **Cú pháp theo loại dataset**: bảng tra `dialect` → quy tắc + ví dụ đúng/sai (duckdb cho file Excel/CSV; tsql/bigquery/postgresql/mysql cho external DB) — nhất quán với `dialect_notes` trong get_context.
3. **Quy tắc khi lỗi**: đọc `error.details` (available_tables/did_you_mean/hint) để tự sửa, tối đa 2 lần; vẫn fail thì báo user nguyên văn lỗi. **Never fabricate data.**
4. **Ghi tri thức**: khi nào save_dataset_knowledge (theo memory_instructions), tôn trọng quyền `ai_can_write_knowledge`.

Cuối guide: dòng `Your guide_token is gd_xxxx — pass it to get_context to prove you have read this guide.`

### Lưu trữ & token

- Nội dung mặc định embedded trong API. Nếu tồn tại file `storage/query-guide.md` thì dùng file đó thay (operator sửa được không cần rebuild).
- `guide_token` = `gd_` + 12 hex đầu SHA-256 của nội dung guide — **sửa guide là token đổi**, AI buộc đọc lại bản mới. Không lưu DB, tính khi serve (cache in-memory theo mtime).

### Enforcement

- `get_context` (auth PAT): thiếu/sai `guide_token` → lỗi `GUIDE_REQUIRED`: message tiếng Anh "Call get_query_guide and READ it first; pass the guide_token it contains." + `assistant_instruction` chống bịa.
- Phiên MCP mới ⇒ Claude chưa có token ⇒ get_context/query đều bị chặn cho tới khi đọc guide — đây chính là "MCP hỏi Claude biết skill chưa khi bắt đầu session".

## Thành phần 1 — Schema token (chặn cứng)

### Tính và lưu token

- Migration mới: thêm cột `schema_hash TEXT NULL` vào bảng `datasets`.
- Giá trị: `st_` + 12 hex đầu của SHA-256 trên chuỗi canonical: các bảng sort theo `table_name`, mỗi bảng nối `table_name` + danh sách cột theo thứ tự (`normalized_name`:`type`). Schema đổi ⇒ hash đổi ⇒ token cũ vô hiệu ⇒ AI buộc đọc lại.
- Cập nhật tại các điểm schema thay đổi: file dataset parse xong (status→ready), external dataset tạo xong (manifest bootstrap). Với dataset cũ (`schema_hash IS NULL`): tính lazy lần đầu cần đến rồi lưu lại — không cần backfill migration.
- Helper chung `SchemaTokenService` (tính từ rows của `dataset_tables`/`dataset_columns`, so khớp token) — một nơi duy nhất, cả ContextService lẫn query endpoints dùng.

### Trả token cho AI

`get_context` (`/api/context`) trả thêm cho từng dataset:

```json
{
  "id": "...",
  "schema_token": "st_9f2c1a8b4e01",
  "dialect": "tsql",
  "dialect_notes": [
    "Reference tables as dbo.table_name or [dbo].[table_name] — NEVER [dbo.table_name].",
    "No query parameters (@x). Inline literal values.",
    "Send ONE complete statement; a WITH block must end with its final SELECT."
  ],
  "tables": [ ... ]
}
```

`dialect` map từ provider: `mssql→tsql`, `bigquery→bigquery`, `postgresql→postgresql`, `mysql→mysql`, file dataset→`duckdb`. `dialect_notes` là mảng 3–5 dòng cứng (hardcode theo dialect), đúc từ các lỗi thực tế đã gặp, nhất quán với nội dung guide.

### Kiểm tra token ở query

- `POST /api/datasets/{id}/query`: body `options.schema_token`.
- `POST /api/query` (multi-dataset): body `schema_tokens` — object `{ "<dataset_id>": "st_..." }`, đủ mọi dataset tham gia.
- `POST/PUT` widget (dashboard): body `schema_token` cho dataset của widget.
- Enforcement chỉ khi principal xác thực qua API-key scheme (PAT). Xác định bằng authentication scheme/claim hiện có của `ApiKeyAuthenticationHandler`.
- Thiếu token → `CONTEXT_REQUIRED`; token sai/cũ → `SCHEMA_CHANGED`. Cả hai kèm message hướng dẫn gọi `get_context` và `assistant_instruction` chống bịa (xem Thành phần 3). Response giữ đúng envelope lỗi hiện tại của query.

## Thành phần 2 — Lỗi tự sửa được

### 2a. Pre-flight trong `ExternalQueryGuard` (bắt trước khi đụng DB khách)

Chạy sau các check hiện có (read-only, single statement), trên SQL đã strip string/comment:

| Rule | Điều kiện bắt | Error code | Message gợi ý sửa |
|---|---|---|---|
| CTE thiếu SELECT cuối | SQL bắt đầu bằng `WITH` nhưng không còn `SELECT` ở paren-depth 0 sau danh sách CTE | `SQL_INCOMPLETE` | "Your WITH block has no final SELECT. Append the main SELECT after the last CTE." |
| Tham số hoá | Xuất hiện `@name` (ngoài string), hoặc `:name` với postgres/mysql | `SQL_PARAMETERS_NOT_SUPPORTED` | "Query parameters are not supported. Inline literal values (e.g. '2026-06-01')." |
| Bracket sai (chỉ mssql) | Pattern `[ident.ident]` | `SQL_INVALID_IDENTIFIER_QUOTING` | "Use [dbo].[table] or dbo.table — [dbo.table] is a single identifier and will not resolve." |

Heuristic bảo thủ: chỉ fail khi chắc chắn sai; nghi ngờ thì cho qua để DB nguồn phán xử (đã có enrich ở 2b đỡ).

### 2b. Enrich lỗi từ DB nguồn — `ExternalErrorEnricher`

Static class mới: input = provider + message (đã scrub) + danh sách bảng/cột đã lưu của dataset; output = `error.details` (hoặc null nếu không nhận diện được). Gắn vào catch-block của `ExternalQueryService`.

| Provider | Pattern nhận diện | details trả thêm |
|---|---|---|
| mssql | `Invalid object name '<x>'` / `Invalid column name '<x>'` | `available_tables` (tối đa 50 tên) hoặc `suggested_columns`, `did_you_mean` (khoảng cách chuỗi gần nhất) |
| bigquery | `Not found: Table <x>` / `Unrecognized name: <x>` / `Syntax error: ...` | như trên; với syntax error thêm `dialect` + 1 dòng nhắc "single complete GoogleSQL statement, no @params" |
| postgresql | `relation "<x>" does not exist` / `column <x> does not exist` | như trên |
| mysql | `Table '<x>' doesn't exist` / `Unknown column '<x>'` | như trên |

`did_you_mean` dùng lại logic khoảng cách chuỗi sẵn có của `DuckDbErrorMapper` (tách helper chung nếu cần). Message gốc từ driver vẫn trả nguyên (đã scrub) — không nuốt.

## Thành phần 3 — Chống bịa số liệu

- Mọi response lỗi của query (cả `ExternalQueryService` lẫn `DuckDbQueryService`, cả `GUIDE_REQUIRED`/`CONTEXT_REQUIRED`/`SCHEMA_CHANGED`) thêm field cố định trong `error`:
  `"assistant_instruction": "Report this error to the user verbatim. Never estimate, interpolate, or fabricate data values. If you cannot obtain real data, tell the user what failed and stop."`
- `tools.md`: thêm tool `get_query_guide`; cập nhật description + response_hint của `get_context`, `query_dataset`, `query_datasets`, widget tools:
  - `get_query_guide`: "Call this FIRST in every session. Returns the usage guide and the guide_token required by get_context."
  - `get_context`: param `guide_token` (required), nêu rõ trả `schema_token` bắt buộc cho mọi query tool + `dialect_notes` phải tuân theo.
  - query tools: param `schema_token` (required, "obtain from get_context"), câu chốt "If a query fails, fix it using error.details or report the failure — NEVER invent data."
- `tools.example.md` cập nhật tương ứng để repo mẫu nhất quán.

## Thành phần 4 — Bỏ dataset API key, thay bằng toggle ghi tri thức

- Migration: thêm cột `ai_can_write_knowledge BOOLEAN NOT NULL DEFAULT TRUE` vào `datasets`; DROP bảng dataset API key.
- Xoá: `DatasetApiKeyService`, các endpoint tạo/thu hồi dataset key, nhánh nhận dataset key trong `ApiKeyAuthenticationHandler` (chỉ còn PAT `edm_pat_` + JWT), block UI "API key cho Claude / MCP" trên trang dataset. Dataset key cũ hết hiệu lực ngay — chấp nhận breaking (đã thống nhất không cần back-compat, client chuẩn là OAuth/PAT).
- Thay bằng: toggle "Cho phép AI ghi tri thức" trên trang dataset (PATCH field mới). `save_dataset_knowledge` / `update_dataset_knowledge` qua PAT kiểm tra toggle; tắt → lỗi `KNOWLEDGE_WRITE_DISABLED` với message rõ ("The dataset owner disabled AI knowledge writes for this dataset.").
- Đọc tri thức (`get_dataset_knowledge`, `search_knowledge`, get_context) không bị ảnh hưởng bởi toggle.

## Luồng mới (happy path + failure path)

```
Phiên MCP mới
Claude → get_context(...)                    → GUIDE_REQUIRED (chưa có guide_token)
Claude → get_query_guide()                   → guide (workflow + cú pháp + quy tắc) + guide_token
Claude → get_context(ids, guide_token)       → schema + schema_token + dialect_notes + knowledge
Claude → query_dataset(sql, schema_token)
          ├─ thiếu/sai token   → CONTEXT_REQUIRED / SCHEMA_CHANGED (+instruction) → gọi lại get_context
          ├─ pre-flight fail   → SQL_INCOMPLETE / SQL_PARAMETERS_NOT_SUPPORTED / ... (+cách sửa) → sửa SQL
          ├─ DB nguồn fail     → EXTERNAL_QUERY_FAILED + details.available_tables/did_you_mean → sửa SQL
          └─ OK                → data thật
Claude → save_dataset_knowledge(...)         → OK nếu toggle bật, KNOWLEDGE_WRITE_DISABLED nếu tắt
```

## Testing

- `SchemaTokenService`: hash ổn định, đổi cột/kiểu/bảng ⇒ hash đổi; so khớp đúng/sai.
- Guide: token đổi khi nội dung đổi; file override được ưu tiên; get_context PAT thiếu/sai guide_token → `GUIDE_REQUIRED`, JWT không cần.
- Query endpoints: PAT thiếu token → `CONTEXT_REQUIRED`; PAT token cũ → `SCHEMA_CHANGED`; PAT token đúng → chạy; JWT không token → chạy (exempt); multi-dataset thiếu 1 token → chặn.
- `ExternalQueryGuard` pre-flight: case thực tế BigQuery WITH-thiếu-SELECT, `@run_date`, `[dbo.table]`; case hợp lệ không bị chặn oan (`WITH x AS (...) SELECT ...`, string chứa `@`, mssql `[dbo].[t]`).
- `ExternalErrorEnricher`: từng pattern per provider, message không khớp pattern → details null, message gốc giữ nguyên.
- Toggle tri thức: PAT ghi khi bật → OK, khi tắt → `KNOWLEDGE_WRITE_DISABLED`; đọc không bị ảnh hưởng; dataset key cũ không còn đăng nhập được.
- Error envelope: mọi nhánh lỗi có `assistant_instruction`.

## Ngoài phạm vi

- Không lưu state phiên ở bridge; bridge không đổi (mọi thay đổi nằm ở API + tools.md).
- Không refresh schema external DB tự động (cơ chế bootstrap manifest giữ nguyên).
- Không thêm UI mới ngoài toggle ghi tri thức (thay chỗ block API key cũ).
- Cross-dialect translation / validate SQL bằng parser đầy đủ — không làm; chỉ heuristic bảo thủ.
- Guide chỉ 1 bản per deployment (chưa per-user/per-dataset).
