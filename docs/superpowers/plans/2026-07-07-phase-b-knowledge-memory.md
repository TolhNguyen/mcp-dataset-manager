# Phase B — Knowledge Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Mỗi dataset có một "bộ nhớ nghiệp vụ" gồm các entry nhỏ (1 fact/entry) mà AI tự lưu/cập nhật qua MCP; tìm kiếm bằng Postgres pg_trgm/unaccent (chi phí 0); có audit revision + guardrails; thay thế field `business_knowledge` cũ.

**Architecture:** Bảng `dataset_knowledge_entries` + `dataset_knowledge_revisions`. `KnowledgeService` (CRUD + archive + revision + search). Quyền ghi: JWT/PAT full; dataset-scoped key cần cột mới `can_write`. Policy `KnowledgeWrite`. MCP tools + UI tab.

**Tech Stack:** .NET 8, Dapper, Postgres (pg_trgm + unaccent), xUnit, vanilla JS.

**Spec:** `docs/superpowers/specs/2026-07-06-big-update-design.md` — Sub-project B.

**Base branch:** `main` (Phase A merged). Create branch `phase-b-knowledge`.

## Global Constraints

- No Entity Framework — Dapper only. Migrations = numbered SQL in `api/Migrations/` (next = `0004`), loaded by `MigrationRunner` (pattern: `NNNN_description.sql`).
- JSON API snake_case (global config in Program.cs). Endpoint groups = static `MapXxxEndpoints(this WebApplication app)` extensions in `api/Endpoints/` (follow `ConnectionEndpoints.cs`/`OAuthEndpoints.cs`).
- Auth helpers: `ClaimsPrincipalExtensions.GetUserId()`, `GetScopedDatasetId()`. Dataset-key claim = `ClaimsPrincipalExtensions.DatasetIdClaim`; `auth_method` claim distinguishes `user_api_key`/`dataset_api_key`/JWT.
- Guardrails (spec): ≤200 active entries/dataset; content ≤4000 chars; title ≤255; write rate-limit 30/min/principal; AI never hard-deletes (archive only); every create/update/archive writes a revision.
- After EACH task: `dotnet build api/ExcelDatasetManager.Api.csproj` + `dotnet test tests/ExcelDatasetManager.Tests/ExcelDatasetManager.Tests.csproj` green before commit. Commit prefix `feat:/fix:/test:/refactor:`.
- Do NOT touch Phase C/D scope (context API, multi-dataset query, documents split, dashboards). Exception: this phase REMOVES the old `business_knowledge` field entirely (spec: no back-compat) — that is in-scope here.
- `kind` allowed values: `note | column_meaning | business_rule | metric_definition | join_hint | document`. `source`: `user | ai`. `created_by`/`actor`: user email for JWT/PAT, `"ai:<key name>"` for dataset key writes.

---

### Task 1: Migration 0004 — knowledge schema + can_write + retire business_knowledge

**Files:** Create `api/Migrations/0004_knowledge.sql`; Modify `tests/ExcelDatasetManager.Tests/MigrationScriptLoaderTests.cs`.

**Step 1.1 — `api/Migrations/0004_knowledge.sql`:**
```sql
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE TABLE IF NOT EXISTS dataset_knowledge_entries (
    id UUID PRIMARY KEY,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    kind VARCHAR(30) NOT NULL,
    title VARCHAR(255) NOT NULL,
    content TEXT NOT NULL,
    source VARCHAR(10) NOT NULL,
    created_by TEXT NOT NULL,
    pinned BOOLEAN NOT NULL DEFAULT FALSE,
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_knowledge_dataset ON dataset_knowledge_entries(dataset_id) WHERE archived_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_knowledge_search ON dataset_knowledge_entries
    USING gin (unaccent(lower(title || ' ' || content)) gin_trgm_ops);

CREATE TABLE IF NOT EXISTS dataset_knowledge_revisions (
    id UUID PRIMARY KEY,
    entry_id UUID NOT NULL REFERENCES dataset_knowledge_entries(id) ON DELETE CASCADE,
    action VARCHAR(10) NOT NULL,
    previous_content TEXT,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE dataset_api_keys ADD COLUMN IF NOT EXISTS can_write BOOLEAN NOT NULL DEFAULT FALSE;

-- Retire business_knowledge: backfill non-empty values into a pinned 'note' entry, then drop.
INSERT INTO dataset_knowledge_entries (id, dataset_id, kind, title, content, source, created_by, pinned, created_at)
SELECT gen_random_uuid(), d.id, 'note', 'Ghi chú nghiệp vụ (migrated)',
       d.business_knowledge, 'user', 'migration', TRUE, NOW()
FROM datasets d
WHERE COALESCE(TRIM(d.business_knowledge), '') <> '';

ALTER TABLE datasets DROP COLUMN IF EXISTS business_knowledge;
ALTER TABLE datasets DROP COLUMN IF EXISTS business_knowledge_updated_at;
```
NOTE on the search index: `unaccent()` is not IMMUTABLE by default so it can't be used directly in a GIN expression index on some setups. If `CREATE INDEX ... unaccent(...)` fails at migration time, fall back to `USING gin (lower(title || ' ' || content) gin_trgm_ops)` (drop the unaccent wrapper in the index only) — the query still applies `unaccent` at search time; the index just pre-filters on lowercased trigrams. Implementer: try the unaccent index first; if `MigrationRunner` throws on it during the Task 8 e2e, switch the index expression to the lower()-only form and note it.

`gen_random_uuid()` requires pgcrypto — Postgres 16 has it built-in in core (`gen_random_uuid` is in core since PG13), so no extension needed.

**Step 1.2 — test:** add to `MigrationScriptLoaderTests`:
```csharp
[Fact]
public void Loads_knowledge_migration()
{
    var m4 = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly).Single(s => s.Version == 4);
    Assert.Contains("dataset_knowledge_entries", m4.Sql);
    Assert.Contains("can_write", m4.Sql);
    Assert.Contains("DROP COLUMN IF EXISTS business_knowledge", m4.Sql);
}
```
**Step 1.3:** build + test green. Commit `feat: add knowledge memory schema and retire business_knowledge column`.

---

### Task 2: Retire business_knowledge from C# (breaking cleanup)

Because migration 0004 drops the column, ALL C# references must go in the same phase or the app crashes on the SELECT. Do this right after Task 1.

**Files:** Modify `api/Services/DatasetService.cs`, `api/Models/Contracts.cs`, `api/Services/ManifestGenerator.cs`, `api/Endpoints/DatasetEndpoints.cs`, `tests/ExcelDatasetManager.Tests/*` (ManifestGeneratorTests + TestData use BusinessKnowledge).

**Requirements:**
- `DatasetService.SelectDatasetSql`: remove the `business_knowledge` + `business_knowledge_updated_at` columns from the projection.
- `DatasetRecord` (Contracts.cs): remove `BusinessKnowledge` + `BusinessKnowledgeUpdatedAt` properties. Update ALL construction sites + `TestData.NewDatasetRecord`.
- Remove `DatasetService.UpdateBusinessKnowledgeAsync`, the const `BusinessKnowledgeMaxLength`, `UpdateBusinessKnowledgeRequest` (Contracts), and the `PUT /{datasetId}/business-knowledge` route in `DatasetEndpoints.cs`.
- `ManifestGenerator.GenerateAsync`: it currently renders a "Business Knowledge" section from `dataset.BusinessKnowledge`. Change its signature to accept `IReadOnlyList<KnowledgeEntry> pinnedKnowledge` (the type from Task 3 — so do Task 3's `KnowledgeEntry` record first OR use a minimal inline shape; simplest: change signature to take `IReadOnlyList<(string Kind, string Title, string Content)> pinnedKnowledge` to avoid ordering coupling). Render each pinned entry under "## Business Knowledge / User Notes" as `### {Title}` + content; if empty, keep the existing "No user-provided business knowledge" message. Callers that regenerate the manifest must pass pinned entries (DatasetService no longer regenerates on business-knowledge update since that endpoint is gone; the parse pipeline passes `[]` at first parse — knowledge is added later, manifest is regenerated when? Keep it simple: at parse time pass `[]`; a future context/knowledge write does NOT regenerate the file manifest — file-dataset manifest is a static download, knowledge lives in the API/context. So the manifest's knowledge section will just say "none at parse time". That's acceptable; the live knowledge is served by Phase C context API. Update the ManifestGeneratorTests accordingly: the "includes provided business knowledge" test becomes "renders pinned knowledge entries when provided" using the new tuple param.)
- Fix `ManifestGeneratorTests` + `TestData` to the new shapes.

**Commit:** `refactor: remove business_knowledge field, manifest renders pinned knowledge entries`. Build + full suite green.

---

### Task 3: KnowledgeEntry model + KnowledgeService (CRUD + revisions + guardrails)

**Files:** Create `api/Services/KnowledgeService.cs`, `api/Models/Knowledge.cs`; Test `tests/ExcelDatasetManager.Tests/KnowledgeServiceTests.cs` (pure validation only — DB paths verified by Task 8 e2e).

**Interfaces (Produces — later tasks depend verbatim):**
```csharp
// api/Models/Knowledge.cs
namespace ExcelDatasetManager.Api.Models;

public record KnowledgeEntry(
    Guid Id, Guid DatasetId, string Kind, string Title, string Content,
    string Source, string CreatedBy, bool Pinned, DateTime? ArchivedAt,
    DateTime CreatedAt, DateTime UpdatedAt);

public record CreateKnowledgeRequest(string? Kind, string? Title, string? Content, bool? Pinned);
public record UpdateKnowledgeRequest(string? Title, string? Content, bool? Pinned);
```

`KnowledgeService` methods (all take the DB user scoping into account — a caller may only touch datasets they own; dataset-key callers are pre-scoped to their datasetId by the endpoint):
- `Task<ApiResult<object>> ListAsync(Guid datasetId, bool includeArchived, string? kind, CancellationToken)`
- `Task<ApiResult<object>> CreateAsync(Guid datasetId, CreateKnowledgeRequest req, string source, string actor, CancellationToken)`
- `Task<ApiResult<object>> UpdateAsync(Guid datasetId, Guid entryId, UpdateKnowledgeRequest req, string actor, CancellationToken)`
- `Task<ApiResult<object>> ArchiveAsync(Guid datasetId, Guid entryId, string actor, CancellationToken)` (soft — sets archived_at + revision action='archive')
- `Task<ApiResult<object>> HardDeleteAsync(Guid datasetId, Guid entryId, CancellationToken)` (JWT-only path)
- `Task<ApiResult<object>> SearchAsync(Guid[] datasetIds, string query, int limit, CancellationToken)` (Task 5 for ranking SQL; stub returns [] here is NOT acceptable — implement the ranking query now)
- `Task<IReadOnlyList<KnowledgeEntry>> GetPinnedAsync(Guid datasetId, CancellationToken)` (used by context API / manifest)

**Guardrail validation (extract to a pure static `KnowledgeGuard` so it's unit-testable):**
```csharp
public static class KnowledgeGuard
{
    public static readonly string[] Kinds =
        ["note","column_meaning","business_rule","metric_definition","join_hint","document"];
    public const int MaxActivePerDataset = 200;
    public const int MaxContentChars = 4000;
    public const int MaxTitleChars = 255;

    // returns error message or null
    public static string? ValidateCreate(string? kind, string? title, string? content);
    public static string? ValidateUpdate(string? title, string? content);
}
```
Rules: kind must be in `Kinds` (default `note` if null on create? No — require it, but accept null→"note" per spec "1 fact/entry"; DECISION: null kind → default "note"); title required non-empty ≤255; content required non-empty ≤4000. Update: at least one of title/content/pinned provided; same length caps.

**Search ranking SQL (implement in SearchAsync):**
```sql
SELECT e.id, e.dataset_id, e.kind, e.title, e.content, e.source, e.pinned,
       similarity(unaccent(lower(e.title || ' ' || e.content)), unaccent(lower(@Q))) AS score
FROM dataset_knowledge_entries e
WHERE e.dataset_id = ANY(@DatasetIds) AND e.archived_at IS NULL
  AND (unaccent(lower(e.title || ' ' || e.content)) % unaccent(lower(@Q))  -- pg_trgm similarity threshold
       OR e.pinned)
ORDER BY (CASE WHEN unaccent(lower(e.title)) LIKE '%' || unaccent(lower(@Q)) || '%' THEN 1 ELSE 0 END) DESC,
         e.pinned DESC, score DESC
LIMIT @Limit;
```
(If `%` operator needs `pg_trgm.similarity_threshold`, that's session default 0.3 — fine.)

**Create guardrail — enforce 200-active cap inside a transaction** (advisory lock per dataset like UploadAsync, count active, reject `KNOWLEDGE_LIMIT_REACHED` if ≥200). **Every** create/update/archive inserts a `dataset_knowledge_revisions` row (create→previous_content null; update→previous content before change; archive→content at archive time) in the SAME transaction.

**Add ErrorCodes:** `KnowledgeNotFound`, `KnowledgeLimitReached`, `ValidationError` (exists).

**Tests (pure, Task 8 covers DB):** `KnowledgeGuardTests` — valid create; kind not in set rejected; null kind→note allowed; empty title rejected; 4001-char content rejected; 256-char title rejected; update with no fields rejected; update length caps.

**Commit:** `feat: add KnowledgeService with CRUD, revisions, guardrails and trigram search`.

---

### Task 4: can_write claim + KnowledgeWrite policy + key creation flag

**Files:** Modify `api/Auth/ApiKeyAuthenticationHandler.cs`, `api/Program.cs`, `api/Services/DatasetApiKeyService.cs`, `api/Endpoints/ApiKeyEndpoints.cs`, `api/Models/Contracts.cs`; Test `tests/ExcelDatasetManager.Tests/ApiKeyAuthenticationHandlerTests.cs` (extend).

**Requirements:**
- `ApiKeyAuthenticationHandler`: when authenticating a dataset-scoped key, SELECT `can_write` too and add a claim `new Claim("can_write", value ? "true" : "false")` to the dataset-key identity. (PAT + JWT are implicitly full-write — no claim needed.)
- `ClaimsPrincipalExtensions`: add `public static bool CanWriteKnowledge(this ClaimsPrincipal p)` → true if auth_method is JWT/user_api_key, OR (dataset_api_key AND can_write claim == "true"). Add const for the claim name.
- `Program.cs`: add authorization policy `"KnowledgeWrite"` = accepts JwtBearer + ApiKey schemes, RequireAuthenticatedUser, plus a requirement/assertion that `CanWriteKnowledge()` is true. Simplest: `.RequireAssertion(ctx => ctx.User.CanWriteKnowledge())` on top of the schemes.
- `DatasetApiKeyService.CreateAsync`: add `bool canWrite` param, INSERT into `can_write`. `CreateDatasetApiKeyRequest` (Contracts) → add `bool? CanWrite`. Update the create endpoint in `ApiKeyEndpoints.cs` to pass it. The key `ListAsync` response should include `can_write` so UI shows it.
- Extend `ApiKeyAuthenticationHandlerTests`: a dataset key still authenticates (existing behavior unchanged); add a pure test for `ClaimsPrincipalExtensions.CanWriteKnowledge` with a JWT principal (true), a dataset-key principal with can_write=false (false), can_write=true (true), user_api_key (true).

**Commit:** `feat: add can_write flag on dataset keys and KnowledgeWrite authorization policy`.

---

### Task 5: KnowledgeEndpoints

**Files:** Create `api/Endpoints/KnowledgeEndpoints.cs`; Modify `api/Program.cs` (DI `AddScoped<KnowledgeService>` + `app.MapKnowledgeEndpoints()`).

**Routes:**
```
GET    /api/datasets/{datasetId}/knowledge?include_archived=&kind=   — policy QueryAccess (read: JWT/PAT/dataset-key any)
POST   /api/datasets/{datasetId}/knowledge                            — policy KnowledgeWrite
PUT    /api/datasets/{datasetId}/knowledge/{entryId}                  — policy KnowledgeWrite
DELETE /api/datasets/{datasetId}/knowledge/{entryId}                  — policy KnowledgeWrite (archive)
DELETE /api/datasets/{datasetId}/knowledge/{entryId}/hard            — policy JwtOnly (hard delete)
GET    /api/knowledge/search?dataset_ids=a,b&q=...&limit=5           — policy QueryAccess
```
**Scoping (critical):** every handler must (a) derive userId from principal; (b) verify the datasetId belongs to the user (reuse `DatasetService.GetDatasetRecordAsync(userId, datasetId)` → null ⇒ 404); (c) if principal is a dataset-scoped key, enforce `GetScopedDatasetId() == datasetId` (else Forbid) — mirror QueryEndpoints. For `/api/knowledge/search`, every dataset_id in the list must pass the same ownership + scoped-key checks; reject the whole request if any fails.
**Actor/source derivation:** JWT/PAT → actor = user email (fetch via AuthService/me or from a claim; if email not in claims, use `"user:"+userId`), source="user". Dataset-key write (can_write) → actor = `"ai:"+<key name>` (the key name — needs the key name; add it as a claim in Task 4, `new Claim("key_name", name)`, or look up by the scoped datasetId+userId; simplest: add key_name claim in Task 4). source="ai".

**Commit:** `feat: add knowledge management endpoints`.

---

### Task 6: MCP tools for knowledge

**Files:** Modify `mcp-bridge/tools.md` AND `mcp-bridge/tools.example.md` (keep in sync — tools.md is the deployed one, gitignored; tools.example.md is the tracked template). Add 4 tool blocks following the existing `type: tool` YAML pattern in the file.

Tools (exact behavior per spec — description IS the prompt that teaches the agent):
- `get_dataset_knowledge` — GET /api/datasets/{dataset_id}/knowledge. Description: read the dataset's business-knowledge memory before analysis.
- `save_dataset_knowledge` — POST /api/datasets/{dataset_id}/knowledge. Description (verbatim intent): "Khi người dùng cung cấp thông tin nghiệp vụ mới, sửa cách hiểu của bạn, định nghĩa metric/quy tắc, hoặc bạn phát hiện mapping cột quan trọng — lưu lại bằng tool này. 1 fact/entry, ngắn gọn." Params: kind, title, content, pinned.
- `update_dataset_knowledge` — PUT /api/datasets/{dataset_id}/knowledge/{entry_id}.
- `search_knowledge` — GET /api/knowledge/search. Params: dataset_ids (comma-joined), q, limit.

Validate: `cd mcp-bridge && npm run build && node dist/index.js validate ./tools.md` → OK. (Note: the dataset-scoped key path only carries write access if `can_write`; the MCP bridge in HTTP mode uses a PAT so write works.)

**Commit:** `feat: expose knowledge MCP tools (get/save/update/search)`.

---

### Task 7: UI — knowledge tab in dataset-detail

**Files:** Modify `api/wwwroot/dataset-detail.html`, `api/wwwroot/js/dataset-detail.js`; the API-key creation UI (wherever dataset keys are created — likely `dataset-detail.js`) to add a "Cho phép AI ghi tri thức (can_write)" checkbox.

**Requirements:** a "Tri thức" tab/section listing entries grouped by kind, each showing source badge (👤 user / 🤖 AI), pinned indicator, and pin/edit/archive buttons; a "＋ Thêm" form (kind select, title, content textarea, pin checkbox). Archived entries hidden by default with a "hiện đã lưu trữ" toggle. All server strings rendered via the existing `escapeHtml` helper (XSS). Use existing css classes.

**Commit:** `feat: add knowledge memory UI tab and can_write key option`.

---

### Task 8: E2E smoke (controller-run verification)

Spin up a fresh Postgres, boot API, drive via curl:
1. Register → JWT. Upload a small CSV dataset → ready (or insert a file dataset directly). 
2. POST knowledge (kind=metric_definition, title, content) → 200; GET → lists it with source=user.
3. PUT update → 200; GET revisions via DB (`SELECT action FROM dataset_knowledge_revisions`) shows create+update.
4. Search: `GET /api/knowledge/search?dataset_ids={id}&q=<vietnamese-no-accent-of-title>` → returns the entry (proves unaccent+trgm).
5. Guardrail: POST 4001-char content → ValidationError; create 200 entries then 201st → KNOWLEDGE_LIMIT_REACHED.
6. can_write: create dataset key with can_write=false → POST knowledge with `X-API-Key` → 403; create key can_write=true → POST → 200, entry source=ai, created_by starts "ai:".
7. DELETE (archive) via key → archived; GET default hides it; hard-delete via key → 403 (JwtOnly), via JWT → 200.
8. Confirm migration retired business_knowledge: `\d datasets` has no business_knowledge column; if a pre-existing dataset had text, a pinned note entry exists.

**Definition of Done (Phase B):**
1. Build + suite green (≥ 220 tests).
2. E2E steps 1-8 all pass.
3. business_knowledge column gone; no C# references remain; app boots clean on fresh DB.
4. Search matches Vietnamese accent-insensitively.
5. Guardrails enforced (200 cap, 4000 chars, AI archive-not-delete, can_write gate).
6. Every mutation writes a revision row.
