using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Serves the AI-facing query guide and its proof-of-read token.
/// </summary>
public class QueryGuideService
{
    public const string Prefix = "gd_";
    private readonly string? _storageDir;
    private readonly object _lock = new();
    private string? _cachedPath;
    private DateTime _cachedMtimeUtc;
    private string _content = "";
    private string _token = "";

    public QueryGuideService(string? storageDir)
    {
        _storageDir = storageDir;
        Reload();
    }

    public (string Token, string Content) GetGuide()
    {
        MaybeReload();
        return (_token, _content);
    }

    public string CurrentToken()
    {
        MaybeReload();
        return _token;
    }

    private void MaybeReload()
    {
        var path = _storageDir is null ? null : Path.Combine(_storageDir, "query-guide.md");
        if (path is not null && File.Exists(path))
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (_cachedPath == path && mtime == _cachedMtimeUtc) return;
        }
        else if (_cachedPath is null)
        {
            return;
        }

        Reload();
    }

    private void Reload()
    {
        lock (_lock)
        {
            var path = _storageDir is null ? null : Path.Combine(_storageDir, "query-guide.md");
            if (path is not null && File.Exists(path))
            {
                _content = File.ReadAllText(path);
                _cachedPath = path;
                _cachedMtimeUtc = File.GetLastWriteTimeUtc(path);
            }
            else
            {
                _content = DefaultGuide;
                _cachedPath = null;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_content));
            _token = Prefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();

            if (!_content.Contains(_token, StringComparison.Ordinal))
            {
                _content = _content.TrimEnd() +
                    $"\n\n---\nYour guide_token is {_token} - pass it to get_context to prove you have read this guide.\n";
            }
        }
    }

    private const string DefaultGuide = """
        # EDM Query Guide (read this before querying)

        You are querying datasets in Excel Dataset Manager (EDM) through MCP tools. Follow this
        workflow exactly. Never invent data.

        ## Required workflow every session
        1. Call `get_query_guide` (this) once - it returns a `guide_token`.
        2. Call `get_context` with the dataset_ids AND the `guide_token`. It returns each dataset's
           tables, normalized column names, sample rows, `dialect`, `dialect_notes`, and a
           `schema_token`. Read the schema - never guess table or column names.
        3. Call `query_dataset` / `query_datasets` with your SQL AND the `schema_token` from get_context.
           The same schema_token is also required by `create_dashboard_widget`, and by
           `update_dashboard_widget` whenever you send new SQL.

        ## When to do what
        - Read schema (get_context): before your first query on a dataset, and again whenever a query
          returns `SCHEMA_CHANGED` (the schema was updated - read it and use the new schema_token).
        - Read sample rows: they are in get_context. Use them to understand value formats. Do NOT run
          exploratory probe queries column-by-column.
        - Query: only after you have the schema_token. Write ONE complete SELECT/WITH statement.

        ## SQL syntax by dataset type
        Each dataset reports a `dialect` in get_context with `dialect_notes`. Obey them. Highlights:
        - tsql (MSSQL): `dbo.table` or `[dbo].[table]`, never `[dbo.table]`. No `@params`. TOP/OFFSET, not LIMIT.
        - bigquery: backticked `project.dataset.table`. No `@params`. A `WITH` must end with its final SELECT.
        - postgresql / mysql: standard SQL, LIMIT n, no bound params.
        - duckdb (uploaded Excel/CSV): each table is a view by name; use normalized column names.

        ## When a query fails
        - Read `error.details`: it may contain `available_tables`, `suggested_columns`, or `did_you_mean`.
          Fix your SQL and retry - at most twice.
        - If it still fails, report the exact error to the user and STOP.
        - NEVER estimate, interpolate, or fabricate data values. If you cannot get real data, say so.

        ## Writing business knowledge
        - Save durable business facts with `save_dataset_knowledge` when the user teaches you something,
          following `memory_instructions` from get_context. If a dataset has AI knowledge-writing
          disabled you will get `KNOWLEDGE_WRITE_DISABLED` - report it, do not retry.

        ## Dashboards & reports (visual-first)
        When the user asks for a "dashboard" or "report", FIRST decide (ask ONE question if unclear):
        - SNAPSHOT (one-off, frozen data): query the data, then build an HTML artifact in chat with
          the data embedded. Do NOT call dashboard tools.
        - REALTIME (data must be fresh every time it is opened): create one endpoint per query with
          create_dashboard_widget (same dashboard_name), then build the page and call
          set_dashboard_html (its description contains the REQUIRED postMessage contract), and give
          the user the returned view_url.
        Always visual-first for both kinds: prefer charts and KPI tiles over raw tables, with
        artifact-quality layout. Only the dashboard owner (or Claude via their PAT) can edit
        endpoint SQL; share viewers can never see SQL.
        """;
}
