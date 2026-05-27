using System.Globalization;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Strict, per-value type checks used to accumulate column-level inference.
/// Tries to avoid common false positives:
///  - "1", "2", "12" being parsed as DateTime by InvariantCulture
///  - "0" / "1" being read as boolean while also being numeric
/// </summary>
public static class TypeInferrer
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy/MM/dd",
        "yyyy/MM/dd HH:mm:ss",
        "dd/MM/yyyy",
        "dd/MM/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm",
        "MM/dd/yyyy",
        "dd-MM-yyyy",
        "dd-MM-yyyy HH:mm:ss",
        "dd.MM.yyyy",
        "dd.MM.yyyy HH:mm:ss"
    };

    private static readonly HashSet<string> BooleanTrue = new(StringComparer.OrdinalIgnoreCase)
        { "true", "yes", "y", "có", "co" };
    private static readonly HashSet<string> BooleanFalse = new(StringComparer.OrdinalIgnoreCase)
        { "false", "no", "n", "không", "khong" };

    public static bool LooksLikeNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        // Reject if too many digits (likely an ID/code we shouldn't treat as numeric for analysis).
        // Allow up to 18 digits to cover BIGINT range comfortably.
        // Allow common thousand separators (",") and decimal "." with InvariantCulture.
        return decimal.TryParse(
            s.Trim(),
            NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out _);
    }

    public static bool LooksLikeDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();

        // Require at least one separator and length >= 8 to reject "1", "12", "2024".
        if (trimmed.Length < 8) return false;
        if (!trimmed.Any(c => c == '-' || c == '/' || c == '.' || c == ' ' || c == ':' || c == 'T'))
            return false;

        return DateTime.TryParseExact(
            trimmed,
            DateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    public static bool LooksLikeBoolean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();
        return BooleanTrue.Contains(trimmed) || BooleanFalse.Contains(trimmed);
    }

    /// <summary>
    /// Reduce running counters into a final inferred-type label.
    /// Booleans take precedence over numbers only when the column is *exclusively* booleans
    /// (avoids treating a 0/1 numeric column as boolean).
    /// </summary>
    public static string Reduce(long nonNullCount, long numberLike, long dateLike, long booleanLike, long stringLike)
    {
        if (nonNullCount == 0) return "unknown";

        // Pure boolean only if every value is a recognized boolean keyword.
        if (booleanLike == nonNullCount && stringLike == 0) return "boolean_candidate";

        if (numberLike == nonNullCount) return "number_candidate";
        if (dateLike == nonNullCount) return "date_candidate";

        // Mixed: take the dominant type if it's >=95% to avoid stringifying mostly-clean columns.
        const double threshold = 0.95;
        if (numberLike / (double)nonNullCount >= threshold) return "number_candidate";
        if (dateLike / (double)nonNullCount >= threshold) return "date_candidate";

        return "string";
    }
}
