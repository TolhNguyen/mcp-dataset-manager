# Excel Dataset Manager

**Token-controlled data analytics for AI agents — spreadsheets *and* live databases.**

Excel Dataset Manager (EDM) is a self-hosted platform that turns Excel/CSV files **and external SQL databases** into a safe, queryable layer for AI agents. Instead of pushing an entire spreadsheet — or a raw database dump — into a chat context, EDM exposes a controlled SQL query layer, a structured schema/context API, a per-dataset business-knowledge memory, and AI-built dashboards, all reachable over MCP.

> **Core positioning**
>
> EDM is not a zero-token data reader. EDM is a **token-controlled query layer** for AI.
>
> Token usage does not disappear — it shifts from "read the entire raw file" to "read only the relevant schema, SQL, and query result." For live databases it goes one step further: **EDM never stores your data at all** — it queries the source on demand.

---

## What EDM does

| Capability | Summary |
|---|---|
| 📄 **File datasets** | Upload Excel/CSV → parsed once into Parquet → queried many times via DuckDB. |
| 🗄 **External databases (live)** | Connect MySQL / SQL Server / PostgreSQL / BigQuery. EDM stores **only** the schema + 2 sample rows per table — never your data. AI queries the source live, read-only. |
| 🧠 **Knowledge memory** | Each dataset has a business-knowledge memory. The AI saves/updates facts (metric definitions, column meanings, rules) via MCP; other agents and granted users reuse them. Accent-insensitive search, zero external cost. |
| 📑 **Knowledge documents** | Upload `.md`/`.txt` docs; EDM splits them by heading into searchable knowledge entries. |
| 🔗 **Structured context + multi-dataset join** | `GET /api/context` returns schema + sample rows + knowledge as structured JSON (replaces the old `manifest.md` for AI). One SQL query can JOIN across several file datasets by alias. |
| 📊 **AI dashboards** | The AI builds a dashboard widget once (frozen SQL + chart). The dashboard re-runs it on each view — live data, no re-chat. |
| 🔐 **One-click MCP auth** | Connect from Claude via OAuth 2.1 — the user just logs in and clicks *Allow*; no access token to copy-paste. |

---

## Architecture

```mermaid
flowchart TD
    U[User / Claude / Agent] --> MCP[MCP Bridge]
    MCP --> A[ASP.NET Core 8 API]

    A --> AUTH[JWT / PAT / dataset key / OAuth]
    A --> PG[(PostgreSQL: metadata, users, keys,\nconnections·encrypted, knowledge, dashboards, logs)]

    subgraph File datasets
      A --> UPLOAD[Upload] --> PARSE[Parse → Parquet]
      A --> DUCK[DuckDB over Parquet]
    end

    subgraph External databases (live)
      A --> CONN[Connectors: MySQL/MSSQL/PostgreSQL/BigQuery]
      CONN -. read-only, on demand .-> SRC[(Your source DB)]
    end

    A --> CTX[Context API: schema + samples + knowledge]
    A --> KNOW[Knowledge memory + search]
    A --> DASH[Dashboards: frozen-SQL widgets + TTL cache]
```

### Source areas

| Area | Path |
|---|---|
| API host + DI + auth policies | `api/Program.cs` |
| Endpoints (grouped) | `api/Endpoints/*.cs` |
| Migrations (numbered SQL) | `api/Migrations/000N_*.sql` |
| File query (DuckDB, single + multi-dataset) | `api/Services/DuckDbQueryService.cs` |
| External connectors + guard | `api/Services/Connectors/` |
| Live external query | `api/Services/ExternalQueryService.cs` |
| Knowledge memory | `api/Services/KnowledgeService.cs` |
| Structured context | `api/Services/ContextService.cs` + `ContextShaper.cs` |
| Dashboards | `api/Services/DashboardService.cs` |
| Secret encryption (AES-256-GCM) | `api/Services/SecretProtector.cs` |
| OAuth 2.1 for MCP | `api/Services/OAuthService.cs`, `api/Endpoints/OAuthEndpoints.cs` |
| MCP bridge | `mcp-bridge/` |
| Web UI | `api/wwwroot/` |

Full detail: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Endpoint + MCP reference: [`docs/API.md`](docs/API.md).

---

## Security model

EDM handles other people's spreadsheets, database credentials, and live production databases. The guarantees:

- **External databases are never copied.** Only the schema (table/column/type) plus **2 sample rows per table** are stored — and sample rows are togglable off per dataset. Every AI query runs live against the source, read-only.
- **Read-only enforced in depth.** A dialect-aware SQL guard (per MySQL/T-SQL/PostgreSQL/BigQuery) accepts only `SELECT`/`WITH`, blocks writes/DDL/file functions (`read_parquet`, `parquet_scan`, `xp_*`, `set_config`, …), rejects multiple statements, and applies a row cap + timeout. PostgreSQL/MySQL sessions are additionally opened read-only.
- **Connection credentials are encrypted at rest** (AES-256-GCM) and never returned to any client — responses are masked.
- **BigQuery cost is capped** via `maximum_bytes_billed`.
- **Dashboard widget SQL is frozen at save.** The browser only calls a data endpoint (never sends SQL); the SQL is validated at save *and* re-validated on every execution; results are cached in-memory by TTL to avoid hammering the source DB.
- **Layered authorization:** JWT (browser), Personal Access Token (MCP, full user scope), and dataset-scoped API keys (one dataset; read-only by default, `can_write` opt-in for knowledge + widgets). MCP connects via OAuth 2.1 (PKCE S256, single-use codes) that mints a PAT — the user never handles a raw token.

---

## MCP tools

Exposed to Claude through the bridge (`mcp-bridge/tools.md`):

| Tool | Purpose |
|---|---|
| `list_datasets` | List the user's datasets. |
| `get_context` | Structured schema + knowledge memory for 1–3 datasets. **Call first.** Replaces the old manifest download. |
| `query_dataset` | Read-only SQL against one dataset (file or live external DB). |
| `query_datasets` | One SQL query joining several **file** datasets by alias. |
| `get_dataset_knowledge` / `save_dataset_knowledge` / `update_dataset_knowledge` / `search_knowledge` | Read and grow the dataset's business-knowledge memory. |
| `create_dashboard_widget` / `list_dashboards` / `get_dashboard` / `update_dashboard_widget` | Build and manage live dashboards. |
| `upload_dataset` / `get_dataset` / `delete_dataset` | Dataset lifecycle. |

`save_dataset_knowledge`'s description doubles as a prompt that teaches the agent *when* to record a fact; every `get_context` response carries `memory_instructions` reinforcing it. This is how the dataset's memory grows across sessions and agents.

---

## Quick start

```bash
git clone https://github.com/TolhNguyen/mcp-dataset-manager
cd mcp-dataset-manager

cp .env.example .env
# Edit .env — required secrets:
#   POSTGRES_PASSWORD
#   JWT_KEY               (≥ 32 random chars)
#   EDM_ENCRYPTION_KEY    (≥ 32 random chars — encrypts stored DB credentials)
#   EDM_DOMAIN            (your domain; Caddy provisions HTTPS)
#   EDM_PUBLIC_URL        (https://<your-domain> — required for MCP OAuth discovery)

docker compose up -d --build
```

| Service | Purpose |
|---|---|
| PostgreSQL | Metadata, users, keys, encrypted connections, knowledge, dashboards, query logs |
| EDM API | Upload, parse, query, context, knowledge, dashboards, auth, OAuth |
| MCP bridge | Connects Claude/agents to EDM (Streamable HTTP + stdio) |
| Caddy | HTTPS reverse proxy |

### Connect Claude (MCP)

Add a connector in Claude pointing at `https://<your-domain>/mcp`. Claude discovers the OAuth endpoints automatically; the user logs into EDM and clicks **Allow** on the consent screen — no token to paste. (For scripts/stdio, create a Personal Access Token in the web UI and set `MCP_AUTH_TOKEN=edm_pat_...`.)

---

## Using external databases

1. **Connections** page → add a connection (MySQL / SQL Server / PostgreSQL / BigQuery). Use a **read-only** database account — EDM never writes, but a read-only account is the strongest guarantee.
2. EDM tests the connection and reads its schema + 2 sample rows/table.
3. Create a dataset from the connection by selecting tables. It's queryable immediately — no import, no wait.
4. The AI queries it live via `query_dataset` (source dialect SQL). Nothing is copied to EDM.

---

## Example: the AI workflow

```text
User: "Doanh thu theo thành phố tháng này, và lưu định nghĩa doanh thu thuần."

Agent:
1. get_context(dataset_ids=[sales])          → schema + sample rows + existing knowledge
2. query_dataset(sales, "SELECT city, SUM(revenue) ... GROUP BY city")
3. save_dataset_knowledge(sales, metric_definition,
     "Doanh thu thuần", "Doanh thu sau chiết khấu, trước thuế")   ← memory grows
4. create_dashboard_widget("Doanh thu", sales, "Theo thành phố",
     "<the SQL>", "bar")                       ← user sees it live afterwards, no re-chat
```

Next session, any agent calling `get_context(sales)` sees the saved metric definition and the dashboard is already live with fresh numbers.

---

## Query safety (recap)

- Accept only `SELECT` / `WITH`; reject write/DDL/file-access tokens per dialect.
- Single statement only; enforce row limits and per-query timeout.
- File datasets run in an in-memory DuckDB over Parquet; external datasets run live against the source with a read-only session where the driver supports it.
- Log submitted SQL, executed SQL, elapsed time, row count, and error code (never the result data, never credentials).
- AI-facing results pass an output token budget (summary/confirmation for oversized results); dashboard widget results bypass that budget (they go to the browser, not the model) but keep the row cap + timeout.

---

## Roadmap (deferred by design)

| Item | Note |
|---|---|
| Cross-source JOIN (Excel × external DB, or DB × DB) | Multi-dataset JOIN currently covers file datasets; cross-source federation is a future phase. |
| Public dashboard share links | Signed read-only URLs; the highest-risk surface, intentionally separated. |
| Live end-to-end tests for MySQL/MSSQL/BigQuery | Connectors are implemented + unit-reviewed and share the PostgreSQL-proven guard/query path; a full multi-provider integration harness is a follow-up. |
| Semantic knowledge search (pgvector) | Trigram search is sufficient at current entry counts; the schema leaves room to add embeddings. |

---

## Final product message

> Excel Dataset Manager lets AI agents analyze spreadsheets and live databases without loading raw data into the model context. Data stays where it belongs — Parquet on your server, or your own database untouched — behind a safe, read-only SQL layer, a structured context API, a growing business-knowledge memory, and AI-built live dashboards. Token usage stays controlled, explainable, and scalable.
