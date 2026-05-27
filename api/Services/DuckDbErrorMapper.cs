using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Translates DuckDB exception messages into structured error codes and suggested fixes.
/// DuckDB exception messages are stable enough strings to pattern-match for the common cases.
/// </summary>
public static class DuckDbErrorMapper
{
    private static readonly Regex BinderColumnMissing = new(
        @"Referenced column ""?(?<col>[^""]+?)""?\s+not found(\s+in.*table\s+""?(?<table>[^""]+?)""?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CatalogTableMissing = new(
        @"Table with name\s+""?(?<table>[^""\s]+)""?\s+does not exist",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParserError = new(
        @"\bParser Error\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record MappedError(string Code, string Message, string? MissingColumn, string? MissingTable);

    public static MappedError Map(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new MappedError("QUERY_FAILED", "Unknown query error.", null, null);
        }

        var colMatch = BinderColumnMissing.Match(raw);
        if (colMatch.Success)
        {
            return new MappedError(
                "COLUMN_NOT_FOUND",
                colMatch.Value,
                colMatch.Groups["col"].Value,
                colMatch.Groups["table"].Success ? colMatch.Groups["table"].Value : null);
        }

        // DuckDB sometimes says "Column ... not found" with slightly different phrasing.
        var altCol = Regex.Match(raw, @"Column\s+""?(?<col>[^""]+?)""?\s+(?:not found|does not exist|is not in)", RegexOptions.IgnoreCase);
        if (altCol.Success)
        {
            return new MappedError("COLUMN_NOT_FOUND", altCol.Value, altCol.Groups["col"].Value, null);
        }

        var tableMatch = CatalogTableMissing.Match(raw);
        if (tableMatch.Success)
        {
            return new MappedError("TABLE_NOT_FOUND", tableMatch.Value, null, tableMatch.Groups["table"].Value);
        }

        if (ParserError.IsMatch(raw))
        {
            return new MappedError("INVALID_SQL", raw, null, null);
        }

        return new MappedError("QUERY_FAILED", raw, null, null);
    }
}
