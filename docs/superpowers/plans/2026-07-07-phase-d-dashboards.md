# Phase D — AI Dashboards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`).

**Goal:** AI tạo widget (saved query + chart config đóng băng) qua MCP; màn hình dashboard tự chạy lại query mỗi lần xem/refresh — dữ liệu luôn mới (external DB là live) mà không cần chat lại. Bảo mật: SQL đóng băng khi lưu, browser chỉ gọi endpoint data, validate 2 lần, cache in-memory chống nện DB.

**Architecture:** `dashboards` + `dashboard_widgets` tables. `DashboardService` (CRUD + widget save-time validation via QueryValidator/ExternalQueryGuard + execution via existing DuckDb/External query services). Widget data endpoint reruns the frozen SQL through the SAME read-only+row-cap+timeout path as normal queries, cached in `IMemoryCache` by TTL. MCP tools + `dashboards.html` (Chart.js self-host).

**Tech Stack:** .NET 8, Dapper, DuckDB/external connectors (reuse), `Microsoft.Extensions.Caching.Memory` (built-in), vanilla JS + self-hosted Chart.js.

**Spec:** `docs/superpowers/specs/2026-07-06-big-update-design.md` — Sub-project D.

**Base branch:** `main` (Phase A/B/C merged). Create branch `phase-d-dashboards`. Migration next = `0006`.

## Global Constraints

- Dapper only, no EF. Endpoint groups = static `MapXxxEndpoints`. JSON snake_case.
- **Widget SQL is frozen at save time.** The browser NEVER sends SQL — it calls `GET .../widgets/{wid}/data` only. Changing SQL requires PUT (JWT) or an MCP tool with write access.
- **Validate the widget SQL twice:** at save (QueryValidator for file datasets / ExternalQueryGuard for external, by the widget's dataset dialect; plus a trial `LIMIT 1` execution to fail early) AND at every execution (re-validate before running). Always read-only, always row cap `Dashboard:MaxRowsPerWidget` (default 1000), always timeout.
- **Anti-hammer:** `refresh_interval_sec` ≥ 30; widget data cached in `IMemoryCache` with TTL = refresh interval (repeated views within TTL do NOT re-hit the source DB); cache key includes widget id + a version stamp so an edit busts it. External widgets also go through `ConnectionConcurrencyLimiter` (reuse).
- Limits: `Dashboard:MaxWidgetsPerDashboard` (20), 10 dashboards/user. Widget/dashboard AI-created → `source='ai'`, badge in UI, user can review/edit/archive.
- Write authorization: reuse the `KnowledgeWrite` policy (JWT/PAT full; dataset-scoped key with `can_write` — same flag as knowledge, scoped to that dataset's widgets). Reading dashboard data: JWT (web UI).
- Widget row cap 1000 (widget results go straight to the browser, NOT through AI, so no token budget).
- After EACH task: build + full suite green before commit. Prefix `feat:/fix:/test:`.
- Public share links are OUT OF SCOPE (deferred). Every read requires JWT.

---

### Task 1: Migration 0006 — dashboards + dashboard_widgets

**Files:** Create `api/Migrations/0006_dashboards.sql`; Modify `MigrationScriptLoaderTests`.

**Step 1.1 — `0006_dashboards.sql`:**
```sql
CREATE TABLE IF NOT EXISTS dashboards (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES users(id),
    name VARCHAR(255) NOT NULL,
    description TEXT,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_dashboards_user ON dashboards(user_id);

CREATE TABLE IF NOT EXISTS dashboard_widgets (
    id UUID PRIMARY KEY,
    dashboard_id UUID NOT NULL REFERENCES dashboards(id) ON DELETE CASCADE,
    dataset_id UUID NOT NULL REFERENCES datasets(id) ON DELETE CASCADE,
    title VARCHAR(255) NOT NULL,
    sql TEXT NOT NULL,
    chart_type VARCHAR(20) NOT NULL,
    chart_config JSONB,
    refresh_interval_sec INT NOT NULL DEFAULT 60,
    position INT NOT NULL DEFAULT 0,
    source VARCHAR(10) NOT NULL,
    created_by TEXT NOT NULL,
    archived_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_widgets_dashboard ON dashboard_widgets(dashboard_id) WHERE archived_at IS NULL;
```
**Step 1.2 — test** `Loads_dashboards_migration` (version 6, contains `dashboard_widgets`, `chart_type`).
**Commit:** `feat: add dashboards and dashboard_widgets schema`.

---

### Task 2: DashboardGuard (chart types + widget validation) + models

**Files:** Create `api/Models/Dashboard.cs`, `api/Services/DashboardGuard.cs`; Test `DashboardGuardTests`.

**Models:**
```csharp
public record DashboardWidget(
    Guid Id, Guid DashboardId, Guid DatasetId, string Title, string Sql, string ChartType,
    string? ChartConfigJson, int RefreshIntervalSec, int Position, string Source, string CreatedBy,
    DateTime? ArchivedAt, DateTime CreatedAt, DateTime UpdatedAt);

public record CreateWidgetRequest(Guid? DatasetId, string? Title, string? Sql, string? ChartType,
    System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec);
public record UpdateWidgetRequest(string? Title, string? Sql, string? ChartType,
    System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec, int? Position);
public record CreateDashboardRequest(string? Name, string? Description);
// MCP convenience: create a widget, auto-creating the dashboard by name if needed.
public record CreateWidgetByDashboardNameRequest(string? DashboardName, Guid? DatasetId, string? Title,
    string? Sql, string? ChartType, System.Text.Json.JsonElement? ChartConfig, int? RefreshIntervalSec);
```

**DashboardGuard (pure static):**
```csharp
public static class DashboardGuard
{
    public static readonly string[] ChartTypes = ["table","line","bar","pie","stat"];
    public const int MinRefreshSec = 30;
    public const int MaxWidgetsPerDashboard = 20;
    public const int MaxDashboardsPerUser = 10;
    public const int MaxTitleChars = 255;

    public static string? ValidateCreate(string? title, string? sql, string? chartType);
    public static int ClampRefresh(int? requested); // null→60, else Max(30, requested)
}
```
Rules: title required ≤255; sql required non-empty; chartType in ChartTypes. (SQL read-only validation is done in the service against the dataset's dialect — NOT here, since it needs the dataset.)

**Tests:** valid; bad chart type; empty title; empty sql; ClampRefresh(null)=60, ClampRefresh(5)=30, ClampRefresh(120)=120.

**Commit:** `feat: add dashboard models and DashboardGuard validation`.

---

### Task 3: DashboardService (CRUD + widget save-time validation + data execution + cache)

**Files:** Create `api/Services/DashboardService.cs`; Modify `api/Models/Errors.cs` (add `DashboardNotFound`, `WidgetNotFound`, `DashboardLimitReached`, `WidgetLimitReached`).

**Consumes:** `DatasetService.GetDatasetRecordAsync` (ownership + dialect), `QueryValidator` (file dialect) + `ExternalQueryGuard` (external), `DuckDbQueryService.QueryAsync` + `ExternalQueryService.QueryAsync` for execution, `IMemoryCache`, `AliasGenerator` not needed.

**Methods:**
- `CreateDashboardAsync(userId, name, description, source, createdBy)` — advisory-lock + 10-cap.
- `ListDashboardsAsync(userId)`, `GetDashboardAsync(userId, id)` (with its active widgets), `DeleteDashboardAsync(userId, id)`.
- `EnsureDashboardByNameAsync(userId, name, source, createdBy)` — return existing (by name, user) or create; for the MCP convenience tool.
- `CreateWidgetAsync(userId, dashboardId, CreateWidgetRequest, source, createdBy)`:
  1. Verify dashboard owned; enforce 20-widget cap (advisory lock on dashboard id).
  2. Verify dataset owned (GetDatasetRecordAsync) — else DatasetNotFound.
  3. **Validate SQL at save** by dataset dialect: file → `QueryValidator.ValidateReadOnlySelect`; external → `ExternalQueryGuard.Validate(sql, provider)`. Fail → ValidationError.
  4. **Trial execution** (LIMIT 1) via the appropriate query service to catch bad columns/tables early; if it errors, return the mapped error (do not save).
  5. Store frozen sql + chart_type + chart_config + ClampRefresh + source + createdBy.
- `UpdateWidgetAsync`, `ArchiveWidgetAsync` (soft), `HardDeleteWidgetAsync` (JWT-only path). Any SQL/chart change re-runs save-time validation and busts the cache (bump an in-memory version or use updated_at in the cache key).
- `GetWidgetDataAsync(userId, dashboardId, widgetId, ct)`:
  1. Verify ownership (dashboard + widget belong to user).
  2. Cache key = `widget:{widgetId}:{updated_at ticks}`; TTL = refresh_interval_sec. On hit → return cached compact_table.
  3. On miss: **re-validate** the frozen SQL (defense against a tampered DB row), row-cap to `Dashboard:MaxRowsPerWidget`, execute via DuckDb (file) or External (external, through ConnectionConcurrencyLimiter) query service, extract just the `result` compact_table (NOT the AI token-budget envelope — widget data goes to the browser, not AI), cache it, return.

**Config:** `Dashboard` section in appsettings: `{ "MaxRowsPerWidget": 1000 }`.

**Tests (pure where possible):** the guard is Task 2; the service DB/exec paths are verified by Task 7 e2e. Add a unit test for the cache-key/version logic if you extract it to a pure helper (optional).

**Commit:** `feat: add DashboardService with frozen-SQL widgets, double validation and TTL cache`.

---

### Task 4: DashboardEndpoints

**Files:** Create `api/Endpoints/DashboardEndpoints.cs`; Modify `Program.cs` (DI `AddScoped<DashboardService>`, `AddMemoryCache()`, `app.MapDashboardEndpoints()`).

**Routes:**
```
POST/GET   /api/dashboards                             — JwtOnly (create/list)
GET/DELETE /api/dashboards/{id}                         — JwtOnly
POST       /api/dashboards/{id}/widgets                 — KnowledgeWrite (create widget)
PUT/DELETE /api/dashboards/{id}/widgets/{wid}           — KnowledgeWrite (update / archive)
DELETE     /api/dashboards/{id}/widgets/{wid}/hard      — JwtOnly
GET        /api/dashboards/{id}/widgets/{wid}/data      — JwtOnly (browser reads; rate-limited "query")
```
Every handler: userId from principal; ownership check; scoped-key (if present) limited to widgets of its own dataset for the write routes (a dataset key may only create/update widgets whose `dataset_id` == its scoped id — enforce in the handler). Data route: JWT only (no scoped key), `.RequireRateLimiting("query")`. Source/actor derivation reuse the KnowledgeEndpoints pattern (dataset-key → source=ai actor="ai:"+keyname; else user).

**Commit:** `feat: add dashboard and widget management endpoints`.

---

### Task 5: MCP tools

**Files:** Modify `mcp-bridge/tools.md` + `mcp-bridge/tools.example.md`.
- `create_dashboard_widget` — POST a widget, auto-creating the dashboard by name. Uses a dedicated endpoint `POST /api/dashboards/widgets` (add to DashboardEndpoints in Task 4: body {dashboard_name, dataset_id, title, sql, chart_type, chart_config, refresh_interval_sec}) so the agent doesn't need a dashboard id first. Description: "Khi người dùng muốn theo dõi một chỉ số thường xuyên, tạo widget để họ xem realtime trên dashboard mà không cần hỏi lại bạn. SQL phải là SELECT/WITH read-only."
- `list_dashboard_widgets` — GET a dashboard's widgets (needs dashboard id or list dashboards first; simplest: GET /api/dashboards returns dashboards, GET /api/dashboards/{id} returns widgets — expose list_dashboards + get_dashboard, or a combined list_dashboard_widgets?dashboard_id=). Keep it minimal: `list_dashboards` (GET /api/dashboards) + `get_dashboard` (GET /api/dashboards/{id}).
- `update_dashboard_widget` — PUT (archive via update or a param).

Note: add the `POST /api/dashboards/widgets` convenience route in Task 4. Validate `node dist/index.js validate` OK. Keep both files in sync.

**Commit:** `feat: add dashboard widget MCP tools`.

---

### Task 6: UI — dashboards.html + self-hosted Chart.js

**Files:** Create `api/wwwroot/dashboards.html`, `api/wwwroot/js/dashboards.js`, `api/wwwroot/js/chart.umd.min.js` (self-hosted Chart.js v4, NO CDN); Modify `api/wwwroot/dashboard.html` nav (link "Bảng điều khiển").

- Obtain Chart.js: the implementer must vendor `chart.umd.min.js` locally (download into wwwroot/js). If network fetch isn't possible in the environment, implement a minimal fallback: render `table` and `stat` chart types with plain HTML/CSS and guard the line/bar/pie types behind "Chart.js not loaded" — but PREFER vendoring the real file. Reference it with a normal `<script src="js/chart.umd.min.js">` (self-hosted, CSP-safe).
- `dashboards.html`: list dashboards → select one → grid of widget cards. Each card: title, AI/user badge, chart rendered from `GET .../data`, auto-refresh via `setInterval(refresh_interval_sec*1000)`. Edit/archive/reorder buttons. A widget whose data endpoint errors shows the error text in-card + a "Nhờ AI sửa" button that copies a prompt.
- All server strings via escapeHtml. Reuse existing css + Api helper. **Runtime-check the data-shape wiring** (the browser actually renders — verify list/data responses map to the JS as expected; this is where a curl-only e2e misses bugs, per the Phase B lesson).

**Commit:** `feat: add dashboards UI with self-hosted Chart.js`.

---

### Task 7: E2E smoke (controller-run) + final review

Fresh Postgres + API + one file dataset (+ optionally reuse Phase C 2-dataset setup).
1. Create dashboard → 200; 11th dashboard → DASHBOARD_LIMIT_REACHED.
2. Create widget (valid SELECT on the dataset's real table) → 200, source per auth; invalid SQL (INSERT) → ValidationError, not saved; SQL referencing a missing column → save-time trial execution error, not saved.
3. GET widget data → compact_table; second GET within TTL → served from cache (assert the source DB wasn't hit again — for a file dataset, check via timing or a log; at minimum assert identical payload + fast).
4. `refresh_interval_sec` = 5 requested → stored as 30 (clamp).
5. can_write: dataset key without can_write → create widget 403; with can_write on the matching dataset → 200 source=ai; a can_write key for dataset X → create widget with dataset_id=Y → Forbid.
6. Archive widget (soft) → hidden from list; hard-delete via JWT → gone; via key → 403/401.
7. Widget row cap: a SELECT returning many rows caps at 1000.
8. Browser: load dashboards.html against the running API, confirm a widget renders (or table/stat fallback) — manual/screenshot check.

Then dispatch a final whole-branch review (Opus or Fable) focused on: the double-validation (save + execute), that the browser truly can't inject SQL, cache correctness (no stale cross-widget data, edit busts cache), scoped-key widget authorization (can't create a widget for another dataset), and no secret leak for external-dataset widgets.

**Definition of Done (Phase D):**
1. Build + suite green (≥ 290 tests).
2. Widget SQL frozen; browser only calls the data endpoint; save-time + execution-time validation both enforced.
3. Read-only + row-cap (1000) + TTL cache + min-refresh(30) all enforced; external widgets go through the concurrency limiter.
4. can_write scoping correct (key can only create widgets for its own dataset); JWT/PAT full.
5. Migrations 0001-0006 apply clean; a widget renders in the browser.
6. External-dataset widget executes live with no secret leak.
