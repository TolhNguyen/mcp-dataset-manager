namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Short SQL rules returned with get_context. The text is intentionally English because it is
/// primarily consumed by AI clients.
/// </summary>
public static class DialectNotes
{
    public static string MapDialect(string sourceKind, string? provider)
    {
        var isExternal = string.Equals(sourceKind, "external_db", StringComparison.OrdinalIgnoreCase);
        if (!isExternal) return "duckdb";
        return (provider ?? "").ToLowerInvariant() switch
        {
            "mssql" => "tsql",
            "bigquery" => "bigquery",
            "postgresql" => "postgresql",
            "mysql" => "mysql",
            _ => provider ?? "unknown"
        };
    }

    public static IReadOnlyList<string> For(string dialect) => dialect switch
    {
        "tsql" =>
        [
            "Reference tables as dbo.table_name or [dbo].[table_name] - NEVER [dbo.table_name] (that is one identifier and will not resolve).",
            "No query parameters (@name). Inline literal values, e.g. WHERE date = '2026-06-01'.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses SELECT TOP (n) or OFFSET/FETCH, not LIMIT."
        ],
        "bigquery" =>
        [
            "Use GoogleSQL (standard SQL). Qualify tables as `project.dataset.table` in backticks.",
            "No query parameters (@name). Inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT - do not stop after the CTEs.",
            "Row limiting uses LIMIT n."
        ],
        "postgresql" =>
        [
            "Use standard PostgreSQL. Quote identifiers with double quotes only when needed.",
            "No bound parameters ($1 / :name); inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        "mysql" =>
        [
            "Use MySQL syntax. Quote identifiers with backticks when needed.",
            "No bound parameters (?/:name); inline literal values.",
            "Send ONE complete statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        "duckdb" =>
        [
            "Use DuckDB SQL. Reference each table by its name (a view) from get_context.",
            "Use the normalized column names from get_context, not the original headers.",
            "Send ONE complete SELECT/WITH statement; a WITH block must end with its final SELECT.",
            "Row limiting uses LIMIT n."
        ],
        _ => Array.Empty<string>()
    };
}
