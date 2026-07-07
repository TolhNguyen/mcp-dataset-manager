using System.Text;
using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Validates user-submitted SQL before passing it to DuckDB.
/// Strategy (defense in depth):
///   1. Strip string literals and comments before scanning so we don't reject legitimate text.
///   2. Reject if scrubbed text contains more than one statement (semicolon outside string/comment).
///   3. Reject if scrubbed text contains forbidden keywords as standalone tokens.
///   4. Require the first non-whitespace token to be SELECT or WITH.
/// Final defense: DuckDB itself runs in read-only mode for the views we register and we do not expose any path injection vector.
/// </summary>
public class QueryValidator
{
    private static readonly string[] ForbiddenTokens =
    {
        "insert", "update", "delete", "drop", "alter", "create", "truncate",
        "attach", "detach", "copy", "pragma", "call", "execute",
        "read_csv", "read_csv_auto", "read_parquet", "parquet_scan", "read_json",
        "read_text", "read_blob", "glob",
        "httpfs", "install", "load", "set"
    };

    public QueryValidationResult ValidateReadOnlySelect(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return QueryValidationResult.Fail("INVALID_SQL", "SQL is required.");
        }

        var scrubbed = StripStringsAndComments(sql);
        var trimmed = scrubbed.Trim();
        if (trimmed.Length == 0)
        {
            return QueryValidationResult.Fail("INVALID_SQL", "SQL is empty after removing comments.");
        }

        // Multiple statements: any ';' that isn't a trailing one indicates a second statement.
        var indexOfSemi = scrubbed.IndexOf(';');
        if (indexOfSemi >= 0)
        {
            // Allow a single trailing ';' but reject anything after it.
            var after = scrubbed[(indexOfSemi + 1)..].Trim();
            if (after.Length > 0)
            {
                return QueryValidationResult.Fail("INVALID_SQL", "Only a single SQL statement is allowed.");
            }
        }

        // First meaningful keyword must be SELECT or WITH.
        if (!Regex.IsMatch(trimmed, @"^\s*(select|with)\b", RegexOptions.IgnoreCase))
        {
            return QueryValidationResult.Fail("NON_READONLY_SQL", "Only SELECT or WITH queries are allowed.");
        }

        foreach (var token in ForbiddenTokens)
        {
            if (Regex.IsMatch(scrubbed, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
            {
                return QueryValidationResult.Fail("NON_READONLY_SQL", $"SQL token '{token}' is not allowed.");
            }
        }

        // Return the original SQL (not the scrubbed version) so DuckDB sees the user's exact intent.
        var cleanedOriginal = sql.Trim().TrimEnd(';').Trim();
        return QueryValidationResult.Ok(cleanedOriginal);
    }

    /// <summary>
    /// Append LIMIT only when the query doesn't already have one at the top level.
    /// We detect "limit" outside strings/comments. Since CTEs/subqueries may have their own LIMITs,
    /// we wrap the query in a SELECT * FROM (...) LIMIT N — this is safer and lets DuckDB push down LIMIT.
    /// </summary>
    public string ApplyLimit(string sql, int maxRows)
    {
        var scrubbed = StripStringsAndComments(sql);

        // If the query already ends with a LIMIT clause (top-level), don't double-wrap.
        if (Regex.IsMatch(scrubbed, @"\blimit\s+\d+\s*(offset\s+\d+\s*)?$", RegexOptions.IgnoreCase))
        {
            return sql;
        }

        return $"SELECT * FROM ({sql}) AS _user_query LIMIT {maxRows}";
    }

    /// <summary>
    /// Returns the input with single-quoted string literals and SQL comments replaced by spaces.
    /// This lets us safely run keyword scans without false positives on text inside strings.
    /// </summary>
    public static string StripStringsAndComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var inSingle = false;
        var inDouble = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n') { inLineComment = false; sb.Append(ch); }
                else { sb.Append(' '); }
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/') { inBlockComment = false; sb.Append("  "); i++; }
                else { sb.Append(ch == '\n' ? '\n' : ' '); }
                continue;
            }

            if (inSingle)
            {
                if (ch == '\'' && next == '\'') { sb.Append("  "); i++; }
                else if (ch == '\'') { inSingle = false; sb.Append(' '); }
                else { sb.Append(' '); }
                continue;
            }

            if (inDouble)
            {
                // Inside double quotes we keep the identifier visible — DuckDB treats "foo" as identifier.
                if (ch == '"') { inDouble = false; }
                sb.Append(ch);
                continue;
            }

            if (ch == '-' && next == '-') { inLineComment = true; sb.Append("  "); i++; continue; }
            if (ch == '/' && next == '*') { inBlockComment = true; sb.Append("  "); i++; continue; }
            if (ch == '\'') { inSingle = true; sb.Append(' '); continue; }
            if (ch == '"') { inDouble = true; sb.Append(ch); continue; }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}

public record QueryValidationResult(bool Success, string? Sql, string? Code, string? Message)
{
    public static QueryValidationResult Ok(string sql) => new(true, sql, null, null);
    public static QueryValidationResult Fail(string code, string message) => new(false, null, code, message);
}
