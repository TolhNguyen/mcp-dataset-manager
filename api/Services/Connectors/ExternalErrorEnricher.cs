using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Recognizes common sanitized database-driver errors and adds details that help AI repair SQL
/// using only schema already stored by EDM.
/// </summary>
public static class ExternalErrorEnricher
{
    private static readonly Regex MsSqlObject = new(@"Invalid object name '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MsSqlColumn = new(@"Invalid column name '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqTable = new(@"Not found: Table\s+(?<name>[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqName = new(@"Unrecognized name:\s*(?<name>[^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BqSyntax = new(@"Syntax error", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PgRelation = new(@"relation ""(?<name>[^""]+)"" does not exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PgColumn = new(@"column ""?(?<name>[^""\s]+)""? does not exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyTable = new(@"Table '(?<name>[^']+)' doesn't exist", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MyColumn = new(@"Unknown column '(?<name>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static object? Enrich(string provider, string message, IReadOnlyList<string> knownTables, IReadOnlyList<string> knownColumns)
    {
        if (string.IsNullOrEmpty(message)) return null;

        foreach (var rx in new[] { MsSqlObject, BqTable, PgRelation, MyTable })
        {
            var m = rx.Match(message);
            if (m.Success)
            {
                var name = LastSegment(m.Groups["name"].Value);
                return new
                {
                    missing_table = name,
                    available_tables = knownTables.Take(50).ToArray(),
                    did_you_mean = Closest(name, knownTables)
                };
            }
        }

        foreach (var rx in new[] { MsSqlColumn, BqName, PgColumn, MyColumn })
        {
            var m = rx.Match(message);
            if (m.Success)
            {
                var name = LastSegment(m.Groups["name"].Value);
                return new
                {
                    missing_column = name,
                    suggested_columns = knownColumns.Take(50).ToArray(),
                    did_you_mean = Closest(name, knownColumns)
                };
            }
        }

        if (provider == ExternalDbProviders.BigQuery && BqSyntax.IsMatch(message))
        {
            return new
            {
                dialect = "bigquery",
                hint = "Send ONE complete GoogleSQL statement. A WITH block must end with its final SELECT. No @parameters."
            };
        }

        return null;
    }

    private static string LastSegment(string identifier)
    {
        var trimmed = identifier.Trim().Trim('`', '"', '[', ']');
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
    }

    private static string? Closest(string target, IReadOnlyList<string> candidates)
    {
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Levenshtein(target.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        return bestDist <= Math.Max(3, target.Length / 2) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }

        return dp[a.Length, b.Length];
    }
}
