using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Quote-safety helper for turning an <see cref="ExternalTableInfo.QueryableName"/> (a value we generated
/// ourselves from information_schema, but which flows back in from the client on GetSampleRowsAsync calls)
/// into a safely-quoted identifier for interpolation into a "SELECT * FROM {name} LIMIT n" statement.
/// Never trust queryableName as pre-validated: reject anything containing a character outside the
/// conservative identifier charset before quoting.
/// </summary>
public static class QueryableIdentifier
{
    private static readonly Regex SafeCharset = new(@"^[A-Za-z0-9_.$]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the quoted identifier (each dot-separated segment individually quoted), or null if
    /// <paramref name="queryableName"/> contains any character outside [A-Za-z0-9_.$] or is empty.
    /// </summary>
    public static string? TryQuote(string? queryableName, char quoteChar)
    {
        if (string.IsNullOrEmpty(queryableName) || !SafeCharset.IsMatch(queryableName))
        {
            return null;
        }

        var segments = queryableName.Split('.');
        if (segments.Any(string.IsNullOrEmpty))
        {
            return null;
        }

        var closeChar = quoteChar switch
        {
            '"' => '"',
            '`' => '`',
            '[' => ']',
            _ => quoteChar,
        };

        return string.Join('.', segments.Select(s => $"{quoteChar}{s}{closeChar}"));
    }
}
