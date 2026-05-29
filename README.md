# Excel Dataset Manager

**Upload Excel or CSV files. Query them with SQL. Let Claude analyze your data — no code required.**

Excel Dataset Manager (EDM) is a self-hosted platform that turns spreadsheets into queryable datasets accessible to AI agents via the Model Context Protocol (MCP). Upload a file, and within seconds Claude can write SQL against it, interpret Vietnamese column names, and return structured results — all without exposing your raw data or credentials.

---

## Why EDM?

Most "AI + spreadsheet" workflows require you to paste data into a chat window, hand it to a third-party service, or write custom glue code. EDM takes a different approach:

- **Your data stays on your server.** Files are stored locally and queried in-process by DuckDB. Nothing leaves your infrastructure.
- **One URL to connect Claude.** No OAuth dance, no API tokens to copy-paste. Claude.ai connects to `/mcp/{your-user-id}` and gets instant access to all your datasets.
- **Vietnamese-first column normalization.** Headers like `Doanh thu tháng 3` become `doanh_thu_thang_3` automatically. Accents removed, symbols mapped, SQL keywords escaped. Claude works with the normalized names while you see the originals.
- **SQL errors are self-healing.** When Claude typos a column name, the response includes ranked suggestions (`revenue` instead of `revanue`) so Claude can self-correct without a round trip to you.
- **Zero-downtime file parsing.** Uploads return immediately. Parsing, Parquet conversion, and manifest generation happen in a background worker — you can query the moment status flips to `ready`.

---

## Architecture

```
Claude.ai Web
      │
      │  HTTPS  /mcp/{user_id}
      ▼
Caddy (TLS termination + reverse proxy)
      ├── /mcp/*  ──► mcp-bridge  (Node.js, Streamable HTTP MCP)
      │                    │
      │                    └── X-API-Key: {user_id}
      ▼
EDM API  (ASP.NET Core 8)
      ├── Auth       JWT + API keys (user-scoped PATs + dataset-scoped keys)
      ├── Upload     multipart → advisory-locked INSERT → background queue
      ├── Parser     Excel/CSV → normalized CSV → Parquet (DuckDB COPY/zstd)
      ├── Manifest   manifest.md: schema, aliases, NL query guide
      └── Query      DuckDB in-memory, CREATE VIEW per table, LIMIT enforcement
            │
            ▼
     PostgreSQL  (metadata, users, query logs, API keys)
     Storage volume  (.parquet files + originals + manifests)
```

### Data pipeline

Every uploaded file goes through the same pipeline:

1. **Parse** — `ExcelDataReader` (xlsx/xls/xlsm) or a streaming CSV parser handles the raw file row-by-row.
2. **Normalize** — `HeaderNormalizer` strips Vietnamese diacritics, maps symbols (`%` → `pct`, `₫` → `vnd`), deduplicates column names, and escapes SQL keywords.
3. **Type inference** — `ColumnStats` accumulates per-column counts for number-like, date-like, and boolean-like values (capped at 5 000 rows). `TypeInferrer.Reduce` emits `number_candidate`, `date_candidate`, `boolean_candidate`, or `string`.
4. **Parquet** — DuckDB `COPY … TO … (FORMAT 'parquet', COMPRESSION 'zstd')` converts the intermediate CSV to Parquet with full-file type inference.
5. **Manifest** — `ManifestGenerator` writes `manifest.md`: table schemas, column mapping tables, Vietnamese aliases, and a natural-language query guide.

### Query safety

Every SQL query goes through `QueryValidator` before DuckDB sees it:

- Strips string literals and comments before scanning, avoiding false positives on user text.
- Rejects multiple statements (`;` not at end).
- Requires `SELECT` or `WITH` as the first token.
- Blocks a hard list of forbidden tokens: `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `TRUNCATE`, `ATTACH`, `DETACH`, `COPY`, `read_parquet`, `read_csv`, `httpfs`, `INSTALL`, `LOAD`, `SET`.
- Wraps the query in `SELECT * FROM (...) AS _user_query LIMIT N` unless a top-level `LIMIT` already exists.

Each query runs in a fresh in-memory DuckDB connection with `memory_limit` and `statement_timeout` applied.

---

## Features

| Feature | Detail |
|---|---|
| **File formats** | `.xlsx`, `.xls`, `.xlsm`, `.csv`, `.tsv` — up to 100 MB by default |
| **Multi-sheet Excel** | Each sheet becomes a separate queryable table |
| **Async parsing** | Upload returns instantly; background worker handles the rest |
| **Parquet storage** | zstd-compressed, read by DuckDB at query time |
| **Column suggestions** | On `COLUMN_NOT_FOUND`, server returns ranked fuzzy-matched alternatives |
| **Dataset limit** | 10 datasets per user, enforced with a PostgreSQL advisory lock |
| **Auth** | JWT (7-day) + user PATs (`edm_pat_…`) + dataset-scoped keys (`edm_…`) |
| **Rate limiting** | 10 req/min on auth endpoints; 60 req/min per IP on query |
| **MCP bridge** | Streamable HTTP, hot-reload config, per-IP rate limiting |
| **HTTPS** | Caddy auto-provisions Let's Encrypt certificates |

---

## Quick start

```bash
git clone https://github.com/yourname/excel-dataset-manager
cd excel-dataset-manager

cp .env.example .env
# Edit .env: set POSTGRES_PASSWORD and EDM_DOMAIN (your real domain, or leave as localhost)
# JWT_KEY can be left blank — the deploy script will generate one

./scripts/deploy.sh
```

That's it. Four containers start: PostgreSQL, the API, the MCP bridge, and Caddy.

| Endpoint | URL |
|---|---|
| Web dashboard | `https://localhost/` |
| API direct | `http://localhost:5847/` |
| MCP (for Claude) | `https://localhost/mcp/{user-id}` |

For a public deployment, point DNS at your server and set `EDM_DOMAIN=your.domain.com` in `.env`. Caddy handles TLS automatically.

---

## Connect Claude.ai in 30 seconds

1. Log in to the EDM dashboard and copy your **Claude.ai Web Custom Connector URL** — it looks like `https://your.domain.com/mcp/{your-user-id}`.
2. In Claude.ai: **Settings → Connectors → Add custom connector**.
3. Paste the URL. No OAuth, no Client ID, no Client Secret, no Bearer token needed.
4. Ask Claude: *"List my datasets."*

The MCP bridge passes your user ID from the URL path directly to the API as an `X-API-Key` header, so Claude gets access to all your datasets with zero credential management.

---

## API key types

| Type | Prefix | Scope | Best for |
|---|---|---|---|
| User ID shortcut | UUID | All your datasets | Claude.ai Web Connector |
| Personal Access Token | `edm_pat_…` | All your datasets | Long-lived integrations, stdio MCP |
| Dataset-scoped key | `edm_…` | One specific dataset | Sharing a single dataset externally |

PATs and dataset keys are stored as SHA-256 hashes — the raw value is shown once at creation.

---

## MCP bridge tool config

The bridge is driven by a Markdown file (`mcp-bridge/tools.md`). Each fenced ` ```yaml ` block declares a connection or a tool. The default config ships with six EDM tools:

| Tool | What it does |
|---|---|
| `list_datasets` | List all uploaded datasets with status and row counts |
| `get_dataset_schema` | Fetch `manifest.md` — column names, types, aliases, SQL guide |
| `query_dataset` | Run a `SELECT` / `WITH` query, get back a compact column+rows table |
| `get_dataset` | Poll a single dataset's status (use after `upload_dataset`) |
| `upload_dataset` | Upload a file from the bridge container's filesystem |
| `delete_dataset` | Permanently delete a dataset (requires explicit user confirmation) |

To add a partner API for data reconciliation, add a `connection` block and `tool` blocks to `tools.md`, then restart the bridge:

```bash
docker compose restart bridge
```

The bridge hot-reloads config on file changes. If the new config has a validation error, the previous valid config keeps serving traffic.

---

## Local development

```bash
# Start only PostgreSQL
docker compose up -d postgres

# Run the API locally
cd api
dotnet restore
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=excel_dataset_manager;Username=app;Password=app_password"
export Jwt__Key="dev_only_secret_key_change_for_production_minimum_32_chars"
dotnet run
```

```bash
# Run the MCP bridge locally
cd mcp-bridge
npm install
npm run build
EDM_API_URL=http://localhost:8080 node dist/index.js
```

Validate your tools.md config without starting the server:

```bash
node dist/index.js validate ./tools.md
```

---

## Debugging

```bash
# All containers
docker compose logs --tail 200

# Follow bridge logs
docker compose logs -f bridge

# Follow API logs
docker compose logs -f api

# Health checks
curl http://localhost:5847/health
curl http://localhost:5848/health
```

**Common issues:**

| Symptom | Fix |
|---|---|
| `Jwt:Key must be set…` at startup | Run `./scripts/deploy.sh` or set `JWT_KEY` (≥ 32 chars) in `.env` |
| `Bad Request: use /mcp/{userId}` | Connector URL is missing the user ID at the end |
| Caddy not issuing TLS cert | Check DNS points to your server and ports 80/443 are open |
| Dataset stuck at `processing` after restart | The in-memory job queue doesn't survive restarts; the API re-enqueues orphaned jobs on boot |
| DB won't accept new password after `.env` change | `docker compose down -v && docker compose up -d --build` |

---

## Project layout

```
excel-dataset-manager/
├── api/                          # ASP.NET Core 8 backend
│   ├── Program.cs                # Minimal API endpoints, DI, middleware
│   ├── Auth/                     # JWT + API key authentication handlers
│   ├── BackgroundJobs/           # Channel-based parsing queue + hosted service
│   ├── Services/                 # Business logic: parse, store, query, manifest
│   ├── Models/                   # DTOs, error codes, domain records
│   └── wwwroot/                  # Vanilla JS + HTML dashboard
├── mcp-bridge/                   # Node.js Streamable HTTP MCP bridge
│   ├── src/
│   │   ├── index.ts              # Express server, MCP session management
│   │   ├── config/               # Markdown config loader, YAML parser, Zod validator
│   │   ├── http/                 # Tool executor, template engine, auth strategies
│   │   └── response/             # JSONPath transform
│   ├── tools.md                  # Production EDM tool declarations
│   └── tools.example.md          # Example with partner API patterns
├── docs/
│   ├── API.md                    # Full REST API reference
│   └── ARCHITECTURE.md           # Deep-dive: pipeline, query safety, type inference
├── Caddyfile                     # HTTPS reverse proxy config
├── docker-compose.yml
└── .env.example
```

---

## License

MIT
