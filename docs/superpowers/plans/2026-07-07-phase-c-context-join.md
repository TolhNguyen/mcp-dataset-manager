# Phase C — Structured Context API + Multi-dataset Query + Documents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`).

**Goal:** Thay `manifest.md` bằng Context API JSON có cấu trúc, lọc được, cap token (kèm knowledge của Phase B + `memory_instructions`); cho phép 1 query SQL join nhiều dataset file; upload tài liệu .md/.txt tách thành knowledge entries tìm kiếm được.

**Architecture:** `datasets.alias` (slug unique/user). `ContextService` build JSON từ datasets+tables+columns+samples(external)+pinned/knowledge, detail summary|full, token cap → summary + warning. Multi-dataset: DuckDB schema-per-alias. Documents reuse KnowledgeService (kind='document'). MCP `get_context` thay `get_dataset_schema`, `query_datasets` mới.

**Tech Stack:** .NET 8, Dapper, DuckDB, xUnit, vanilla JS.

**Spec:** `docs/superpowers/specs/2026-07-06-big-update-design.md` — Sub-project C.

**Base branch:** `main` AFTER Phase B merged (Context API embeds Phase B knowledge; `KnowledgeService.GetPinnedAsync`/`SearchAsync` + `KnowledgeEntry` model must exist). Create branch `phase-c-context`.

## Global Constraints

- Dapper only, no EF. Migration next = `0005`. Endpoint groups = static `MapXxxEndpoints` extensions.
- JSON snake_case. Reuse `AiTokenBudgetService.EstimateTokens(object)` for token cap. Reuse `QueryValidator` (DuckDB) for multi-dataset SQL.
- Multi-dataset is **file datasets only** (`source_kind='file'`); external datasets rejected with a clear message. `Query:MaxDatasetsPerQuery` default 3.
- Token cap: if estimated context > `Query:SafeMaxTokens`, auto-downgrade to summary detail + add a warning telling the caller to filter with `tables=`.
- After EACH task: build + full suite green before commit. Prefix `feat:/fix:/test:/refactor:`.
- Do NOT touch Phase D (dashboards). Knowledge CRUD/search already exists (Phase B) — reuse, don't reimplement.
- Alias: lowercase slug from dataset name (`[a-z0-9_]`, collapse others to `_`, trim, non-empty fallback `ds_<8hex>`), UNIQUE per user (suffix `_2`,`_3`… on collision).

---

### Task 1: Migration 0005 — dataset alias + query_logs.dataset_ids

**Files:** Create `api/Migrations/0005_context.sql`; Modify `MigrationScriptLoaderTests`.

**Step 1.1 — `0005_context.sql`:**
```sql
ALTER TABLE datasets ADD COLUMN IF NOT EXISTS alias VARCHAR(64);
ALTER TABLE query_logs ADD COLUMN IF NOT EXISTS dataset_ids UUID[];

-- Backfill alias from name, unique per user. Deterministic slug + row_number suffix for collisions.
WITH slugged AS (
    SELECT id, user_id,
           NULLIF(regexp_replace(lower(name), '[^a-z0-9]+', '_', 'g'), '') AS base
    FROM datasets WHERE alias IS NULL
),
numbered AS (
    SELECT id, user_id,
           COALESCE(base, 'ds') AS base,
           ROW_NUMBER() OVER (PARTITION BY user_id, COALESCE(base,'ds') ORDER BY id) AS rn
    FROM slugged
)
UPDATE datasets d
SET alias = CASE WHEN n.rn = 1 THEN n.base ELSE n.base || '_' || n.rn END
FROM numbered n WHERE d.id = n.id;

CREATE UNIQUE INDEX IF NOT EXISTS idx_datasets_user_alias ON datasets(user_id, alias) WHERE alias IS NOT NULL;
```
(`regexp_replace` may leave leading/trailing `_`; acceptable — the C# generator in Task 2 trims for NEW datasets; this backfill is best-effort for existing rows.)

**Step 1.2 — test:** `Loads_context_migration` asserting version 5, contains `ADD COLUMN IF NOT EXISTS alias` + `dataset_ids UUID[]`.

**Commit:** `feat: add dataset alias and query_logs dataset_ids columns`.

---

### Task 2: AliasGenerator + assign alias on dataset creation

**Files:** Create `api/Services/AliasGenerator.cs`; Modify `api/Services/DatasetService.cs` (assign alias in `UploadAsync`), `api/Services/ExternalSchemaService.cs` (assign alias in `CreateExternalDatasetAsync`), `DatasetRecord`+`SelectDatasetSql` (add `Alias`); Test `AliasGeneratorTests`.

**Interfaces:**
```csharp
public static class AliasGenerator
{
    // pure slug: lower, non-[a-z0-9_]→_, collapse repeats, trim _, empty→"ds"
    public static string Slugify(string name);
    // given desired base + a set of existing aliases for the user, return a unique alias (base, base_2, ...)
    public static string MakeUnique(string baseSlug, ISet<string> existingForUser);
}
```
On create (both file + external): compute slug from name, load the user's existing aliases (`SELECT alias FROM datasets WHERE user_id=@U AND alias IS NOT NULL`), `MakeUnique`, store in the new row's `alias`. Add `Alias` to `DatasetRecord` + `SelectDatasetSql` projection + all construction sites + `TestData`.

**Tests:** Slugify("Doanh Thu 2026")→"doanh_thu_2026"; strips accents? NO — keep ASCII-only, Vietnamese accented chars → `_` (acceptable; or document). Actually apply a simple accent-fold: map common Vietnamese to ASCII before slug (đ→d, remove combining marks). Keep it pragmatic: `Slugify("Đơn Hàng")` should be non-empty and `[a-z0-9_]` only. MakeUnique collision → `_2`,`_3`; empty base → "ds".

**Commit:** `feat: generate unique per-user dataset alias on creation`.

---

### Task 3: ContextService + Context endpoint

**Files:** Create `api/Services/ContextService.cs`; Modify `api/Endpoints/` (add to a new `ContextEndpoints.cs`), `Program.cs` (DI + map); Test `ContextServiceTests` (pure shaping + token cap with in-memory data).

**Interfaces:**
```csharp
public record ContextRequest(Guid[] DatasetIds, string[]? Tables, string Detail /* "summary"|"full" */);
public class ContextService {
    public Task<object> BuildAsync(Guid userId, ContextRequest req, CancellationToken ct);
}
```
**BuildAsync logic:**
- Validate: 1..MaxDatasetsPerQuery datasets, all owned by user + status ready (reuse DatasetService). Unknown/unowned → error.
- For each dataset assemble: dataset_id, name, alias, source_kind, provider (if external), dialect (external→provider, file→"duckdb"), tables[] (filtered by `tables=` if provided; each: table_name, qualified_name=`{alias}.{table_name}`, columns[name,type,display_name,aliases], and sample_rows for external OR pinned-only for file? Per spec: file datasets don't have sample_rows column set (that was external only); include column list; external includes stored 2 sample rows), knowledge[] = `KnowledgeService.GetPinnedAsync` for full detail, or pinned only for summary (both use pinned; full ALSO could include recent non-pinned — spec: full = all fields, summary = pinned only + no sample/aliases). Implement: full → columns with aliases + sample_rows + knowledge (pinned + up to N recent non-pinned, say 20 active); summary → columns without aliases, no sample_rows, knowledge pinned only.
- `memory_instructions`: `"Bộ nhớ dataset có {N} entries. Khi người dùng cung cấp thông tin nghiệp vụ mới hoặc sửa cách hiểu của bạn, hãy lưu bằng save_dataset_knowledge."` (N = total active across the requested datasets).
- `token_estimate` = `AiTokenBudgetService.EstimateTokens(payloadSoFar)`.
- **Token cap:** if detail=="full" and estimate > SafeMaxTokens → rebuild as summary, set a `warning` field: `"Context vượt {SafeMaxTokens} tokens, đã hạ xuống summary. Lọc bằng tables= để lấy full cho các bảng cần thiết."` Recompute token_estimate on the summary payload.

**Endpoint:** `GET /api/context?dataset_ids=a,b&tables=orders,customers&detail=summary|full` (default full) — policy `QueryAccess`. Parse comma lists. Dataset-scoped key: allowed only if the single scoped datasetId is the sole requested dataset (else Forbid).

**Tests (pure):** build ContextService with a fake data provider (inject an interface or pass pre-loaded rows — refactor so the shaping is testable without DB: extract a pure `ContextShaper.Shape(datasets, tables, detail, knowledge, safeMaxTokens, estimator)` returning the payload + whether it downgraded). Test: full detail includes sample_rows+aliases; summary omits them; oversized full downgrades to summary with warning; memory_instructions count correct; tables filter drops other tables; qualified_name = alias.table.

**Commit:** `feat: add structured Context API replacing manifest as AI schema source`.

---

### Task 4: Multi-dataset query (DuckDB schema-per-alias)

**Files:** Modify `api/Services/DuckDbQueryService.cs` (add a multi-dataset entry point), Create `api/Endpoints/MultiQueryEndpoints.cs` (or add to QueryEndpoints), `Program.cs` map; Test `MultiDatasetViewSetupTests` (DuckDB in-memory with Parquet fixtures).

**Requirements:**
- New `POST /api/query` body `{ dataset_ids: [], sql, options }` — policy `QueryAccess` + rate limit `query`.
- `DuckDbQueryService`: add `QueryMultiAsync(Guid userId, Guid[] datasetIds, QueryRequest req, CancellationToken)`:
  - Load each DatasetRecord (owned + ready). Reject if count>MaxDatasetsPerQuery, any not found, any `source_kind!='file'` (error `EXTERNAL_NOT_JOINABLE`: "External datasets chưa hỗ trợ cross-dataset join; query trực tiếp dataset đó."), or dataset-scoped key present (multi-dataset not allowed for scoped keys → Forbid at endpoint).
  - Validate SQL via existing `QueryValidator.ValidateReadOnlySelect` + `ApplyLimit`.
  - DuckDB in-memory: for each dataset `CREATE SCHEMA "{alias}";` then per table `CREATE VIEW "{alias}"."{table}" AS SELECT * FROM read_parquet('{parquetPath}');`. Then run the user SQL (which references `alias.table`). Reuse the SAME result-building + token-budget + query_logs flow as `QueryAsync` (factor a shared private helper if clean; else mirror). query_logs: set `dataset_ids` array; `dataset_id` column is NOT NULL — use the first dataset_id for the scalar column and the array for the new column.
  - execution.engine = "duckdb".
- Single-dataset `POST /api/datasets/{id}/query` endpoint stays unchanged.

**Tests:** using a temp dir with two small Parquet files (write via DuckDB `COPY (SELECT ...) TO ...parquet` in test setup, or reuse ParquetWriter), verify a JOIN across two aliases returns rows; verify referencing a non-existent alias errors; verify the alias/table quoting handles a normal alias. (These are real DuckDB integration tests, no external service.)

**Commit:** `feat: add multi-dataset DuckDB join query endpoint`.

---

### Task 5: Knowledge documents (upload + split by heading)

**Files:** Create `api/Services/DocumentImporter.cs` (pure split), add endpoint to `KnowledgeEndpoints.cs` (Phase B file); Test `DocumentImporterTests`.

**Requirements:**
- `POST /api/datasets/{datasetId}/knowledge/documents` (multipart, field `file`, .md/.txt ≤1MB) — policy `KnowledgeWrite`.
- `DocumentImporter.Split(string markdown) -> List<(string Title, string Content)>`: split on top-level headings (`^#{1,6}\s+`), each section = heading text as Title + following body as Content until next heading; content before the first heading (if any) → title "Tài liệu" or the filename. Truncate each content to 4000 chars (KnowledgeGuard.MaxContentChars); skip empty sections.
- For each section, create a knowledge entry via `KnowledgeService.CreateAsync(datasetId, kind="document", title, content, source=<caller>, actor)`. Respect the 200-active cap (if exceeded mid-import, stop and report how many imported).
- Response: `{ imported: n, skipped: m }`.

**Tests (pure DocumentImporter):** a 3-heading doc → 3 sections with correct titles/bodies; preamble before first heading → its own section; a 5000-char body → truncated to 4000; empty doc → [].

**Commit:** `feat: import knowledge documents by splitting markdown headings`.

---

### Task 6: MCP tools — get_context, query_datasets; retire get_dataset_schema

**Files:** Modify `mcp-bridge/tools.md` + `mcp-bridge/tools.example.md`.

- Replace `get_dataset_schema` with `get_context` → GET /api/context. Params: dataset_ids (comma), tables (comma, optional), detail (summary|full, optional). Description: "Fetch structured schema + business-knowledge memory for one or more datasets BEFORE writing SQL or planning. Returns tables, columns, sample rows, dialect, and the dataset's knowledge memory + memory_instructions. Call this first."
- Add `query_datasets` → POST /api/query with `dataset_ids` (array) + sql + max_rows. Description: multi-dataset JOIN across file datasets; each dataset addressed by its alias (from get_context). Keep `query_dataset` (single) unchanged.
- Validate config builds. Update the tools list comment if present.

**Commit:** `feat: switch MCP schema tool to Context API and add multi-dataset query tool`.

---

### Task 7: UI — document upload in knowledge tab

**Files:** Modify `api/wwwroot/js/dataset-detail.js` + the knowledge tab markup (Phase B).

- Add an "Tải tài liệu (.md/.txt)" upload control to the knowledge tab that POSTs to the documents endpoint and refreshes the list, showing `{imported} mục đã thêm`. Reuse existing upload/fetch patterns; escapeHtml on rendered results.

**Commit:** `feat: add knowledge document upload to the UI`.

---

### Task 8: E2E smoke (controller-run)

Fresh Postgres + API; needs two small FILE datasets (upload two tiny CSVs, wait ready).
1. `GET /api/context?dataset_ids={id1}&detail=full` → JSON with alias, tables, columns; `memory_instructions` present; token_estimate > 0.
2. Add a pinned knowledge entry (Phase B) → context knowledge[] includes it; summary detail omits sample_rows/aliases.
3. Token cap: request full over many tables/entries (or set Query:SafeMaxTokens low via env) → response downgraded to summary + warning.
4. Multi-dataset: `POST /api/query {dataset_ids:[id1,id2], sql:"SELECT a.* FROM alias1.tbl a JOIN alias2.tbl b ON ..."}` → rows returned; engine=duckdb; query_logs.dataset_ids has both.
5. Multi-dataset with an external dataset id → EXTERNAL_NOT_JOINABLE.
6. Documents: POST a 3-heading .md → imported:3; `GET /api/knowledge/search?dataset_ids={id1}&q=<heading text>` returns a document entry.
7. get_context via MCP tools.md validate OK.

**Definition of Done (Phase C):**
1. Build + suite green (≥ 235 tests).
2. Context API returns correct full/summary shapes; token cap downgrades with warning.
3. Multi-dataset JOIN across two file datasets works; external rejected; scoped-key rejected.
4. Documents split by heading into searchable entries.
5. MCP tools.md validates; get_context replaces get_dataset_schema.
6. No regression: single-dataset query + Phase A external query still pass.
