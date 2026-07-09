using System.Text.RegularExpressions;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Validates and row-caps SQL sent to external customer databases (PostgreSQL, MySQL, MSSQL, BigQuery).
/// This is the security boundary between the AI-generated SQL and a customer's production database:
/// unlike QueryValidator (DuckDB, our own sandboxed process), a bypass here means writing to, or
/// exfiltrating from, a system we do not own. Strategy mirrors QueryValidator:
///   1. Strip string literals and comments before scanning (StripStringsAndComments).
///   2. Reject if more than one statement (semicolon outside string/comment, one trailing ';' allowed).
///   3. Require first non-whitespace token to be SELECT or WITH.
///   4. Reject if scrubbed text contains a common forbidden token, or a provider-specific one.
///   5. MSSQL only: reject any xp_/sp_ prefixed identifier (extended/system stored procedures).
/// </summary>
public static class ExternalQueryGuard
{
    private static readonly string[] CommonForbidden =
    {
        "insert", "update", "delete", "drop", "alter", "create", "truncate",
        "grant", "revoke", "merge", "call", "execute", "exec", "replace"
    };

    private static readonly Dictionary<string, string[]> ProviderForbidden = new()
    {
        [ExternalDbProviders.PostgreSql] = ["copy", "do", "set", "lock", "listen", "notify", "vacuum", "reindex", "cluster", "prepare", "deallocate", "pg_read_file", "pg_sleep", "dblink", "lo_import", "lo_export"],
        [ExternalDbProviders.MySql]      = ["set", "use", "load", "outfile", "dumpfile", "handler", "lock", "unlock", "install", "uninstall", "benchmark", "sleep", "load_file"],
        [ExternalDbProviders.MsSql]      = ["set", "use", "into", "bulk", "openrowset", "openquery", "opendatasource", "waitfor", "dbcc", "shutdown", "kill", "reconfigure", "backup", "restore", "xp_cmdshell", "sp_executesql"],
        [ExternalDbProviders.BigQuery]   = ["export", "load", "assert", "begin", "commit", "rollback", "declare", "set"]
    };

    private static readonly Regex MsSqlProcRegex = new(@"\b(xp|sp)_[a-z0-9_]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostgreSqlDangerousFunctionRegex = new(
        @"\b(pg_sleep|pg_read_file|pg_read_binary_file|pg_ls_dir|dblink|lo_import|lo_export|set_config|setval|nextval)[a-z0-9_]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static QueryValidationResult Validate(string? sql, string provider)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return QueryValidationResult.Fail("INVALID_SQL", "SQL is required.");
        }

        if (!ExternalDbProviders.IsValid(provider))
        {
            return QueryValidationResult.Fail("INVALID_SQL", $"Unknown provider '{provider}'.");
        }

        var scrubbed = QueryValidator.StripStringsAndComments(sql);
        var trimmed = scrubbed.Trim();
        if (trimmed.Length == 0)
        {
            return QueryValidationResult.Fail("INVALID_SQL", "SQL is empty after removing comments.");
        }

        // Multiple statements: any ';' that isn't a trailing one indicates a second statement.
        var indexOfSemi = scrubbed.IndexOf(';');
        if (indexOfSemi >= 0)
        {
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

        foreach (var token in CommonForbidden)
        {
            if (Regex.IsMatch(scrubbed, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
            {
                return QueryValidationResult.Fail("NON_READONLY_SQL", $"SQL token '{token}' is not allowed.");
            }
        }

        foreach (var token in ProviderForbidden[provider])
        {
            if (Regex.IsMatch(scrubbed, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))
            {
                return QueryValidationResult.Fail("NON_READONLY_SQL", $"SQL token '{token}' is not allowed.");
            }
        }

        if (provider == ExternalDbProviders.MsSql && MsSqlProcRegex.IsMatch(scrubbed))
        {
            return QueryValidationResult.Fail("NON_READONLY_SQL", "Extended/system stored procedures are not allowed.");
        }

        if (provider == ExternalDbProviders.PostgreSql && PostgreSqlDangerousFunctionRegex.IsMatch(scrubbed))
        {
            return QueryValidationResult.Fail("NON_READONLY_SQL", "Dangerous PostgreSQL function is not allowed.");
        }

        // Only the [ident.ident] shape (schema+table fused into one bracket) is a mistake;
        // a display alias like [No. of Orders] is a legal identifier and must pass.
        if (provider == ExternalDbProviders.MsSql
            && Regex.IsMatch(scrubbed, @"\[[A-Za-z_][A-Za-z0-9_$]*\.[A-Za-z_][A-Za-z0-9_$]*\]"))
        {
            return QueryValidationResult.Fail("SQL_INVALID_IDENTIFIER_QUOTING",
                "Use [dbo].[table] or dbo.table - [dbo.table] is a single identifier and will not resolve.");
        }

        if (Regex.IsMatch(scrubbed, @"@[A-Za-z_][A-Za-z0-9_]*"))
        {
            return QueryValidationResult.Fail("SQL_PARAMETERS_NOT_SUPPORTED",
                "Query parameters like @name are not supported. Inline literal values, e.g. '2026-06-01'.");
        }

        if (Regex.IsMatch(trimmed, @"^\s*with\b", RegexOptions.IgnoreCase)
            && !HasTopLevelSelectAfterCtes(scrubbed))
        {
            return QueryValidationResult.Fail("SQL_INCOMPLETE",
                "Your WITH block has no final SELECT. Append the main SELECT after the last CTE.");
        }

        // Return the original SQL (not the scrubbed version) so the target database sees the user's exact intent.
        var cleanedOriginal = sql.Trim().TrimEnd(';').Trim();
        return QueryValidationResult.Ok(cleanedOriginal);
    }

    private static bool HasTopLevelSelectAfterCtes(string scrubbed)
    {
        // Every CTE body lives inside parentheses, so a SELECT keyword at paren-depth 0
        // can only be the statement's main SELECT.
        var depth = 0;
        for (var i = 0; i < scrubbed.Length; i++)
        {
            var ch = scrubbed[i];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }
            else if (depth == 0
                     && (ch is 's' or 'S')
                     && i + 6 <= scrubbed.Length
                     && string.Compare(scrubbed, i, "select", 0, 6, StringComparison.OrdinalIgnoreCase) == 0
                     && (i == 0 || !IsWordChar(scrubbed[i - 1]))
                     && (i + 6 == scrubbed.Length || !IsWordChar(scrubbed[i + 6])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static string ApplyRowCap(string sql, string provider, int maxRows)
    {
        var scrubbed = QueryValidator.StripStringsAndComments(sql);

        if (provider == ExternalDbProviders.MsSql)
        {
            if (Regex.IsMatch(scrubbed, @"^\s*select\s+top\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(scrubbed, @"fetch\s+next\s+\d+\s+rows\s+only\s*$", RegexOptions.IgnoreCase))
            {
                return sql;
            }

            if (Regex.IsMatch(scrubbed, @"\border\s+by\s+[^)]+$", RegexOptions.IgnoreCase))
            {
                return $"{sql}\nOFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY";
            }

            return $"SELECT TOP ({maxRows}) * FROM (  {sql}  ) AS _edm_q";
        }

        // postgresql / mysql / bigquery
        if (Regex.IsMatch(scrubbed, @"\blimit\s+\d+\s*(offset\s+\d+\s*)?$", RegexOptions.IgnoreCase))
        {
            return sql;
        }

        return $"SELECT * FROM (  {sql}  ) AS _edm_q LIMIT {maxRows}";
    }
}
