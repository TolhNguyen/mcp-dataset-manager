using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Normalizes table and column names per spec section 11.
/// Removes Vietnamese accents, lowercases, replaces symbols, dedupes.
/// </summary>
public class HeaderNormalizer
{
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "group", "order", "by", "limit", "offset",
        "join", "inner", "outer", "left", "right", "cross", "on", "as",
        "table", "view", "user", "date", "time", "timestamp", "index",
        "insert", "update", "delete", "drop", "alter", "create", "truncate",
        "union", "all", "distinct", "having", "case", "when", "then", "else", "end",
        "and", "or", "not", "null", "true", "false", "in", "between", "like",
        "asc", "desc"
    };

    // Symbol replacements applied BEFORE non-alphanumeric stripping.
    // Note: we add surrounding spaces so they survive the next step as word boundaries.
    private static readonly (string Symbol, string Replacement)[] SymbolMap =
    {
        ("%",  " pct "),
        ("₫",  " vnd "),
        ("đ",  " d "),    // handled separately below for accent context, but kept as a defensive replacement
        ("$",  " usd "),
        ("#",  " number "),
        ("&",  " and "),
        ("+",  " plus ")
    };

    public List<NormalizedName> NormalizeColumns(IReadOnlyList<string?> headers)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<NormalizedName>(headers.Count);

        for (var i = 0; i < headers.Count; i++)
        {
            var raw = headers[i];
            var fallback = $"col_{i + 1:000}";
            var baseName = NormalizeSingle(raw, fallback);
            var unique = MakeUnique(baseName, used);
            result.Add(new NormalizedName(raw, unique));
        }

        return result;
    }

    public string NormalizeTableName(string? name, string fallback = "table")
    {
        var normalized = NormalizeSingle(name, fallback);
        if (!normalized.StartsWith("raw_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "raw_" + normalized;
        }
        return normalized;
    }

    /// <summary>
    /// Core normalization. Always returns a non-empty, snake_case, ASCII-only identifier.
    /// </summary>
    public string NormalizeSingle(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            return EnsureValidIdentifier(fallback);
        }

        text = text.ToLowerInvariant();

        // Apply symbol mappings.
        foreach (var (sym, repl) in SymbolMap)
        {
            if (text.Contains(sym, StringComparison.Ordinal))
            {
                text = text.Replace(sym, repl);
            }
        }

        // Remove Vietnamese accents (đ/Đ first because they don't decompose).
        text = RemoveVietnameseAccents(text);

        // Replace any non-alphanumeric run with a single underscore.
        text = Regex.Replace(text, "[^a-z0-9]+", "_");
        text = text.Trim('_');

        if (string.IsNullOrEmpty(text))
        {
            text = fallback;
        }

        return EnsureValidIdentifier(text);
    }

    private static string EnsureValidIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text)) text = "col";

        // Identifiers may not start with a digit.
        if (char.IsDigit(text[0]))
        {
            text = "col_" + text;
        }

        // Avoid SQL keyword collisions.
        if (SqlKeywords.Contains(text))
        {
            text += "_col";
        }

        return text;
    }

    private static string MakeUnique(string name, Dictionary<string, int> used)
    {
        if (!used.TryGetValue(name, out var count))
        {
            used[name] = 1;
            return name;
        }

        count++;
        used[name] = count;
        var candidate = $"{name}_{count}";

        // Re-check for collision after suffix (rare but possible).
        while (used.ContainsKey(candidate))
        {
            count++;
            candidate = $"{name}_{count}";
        }
        used[name] = count;
        used[candidate] = 1;
        return candidate;
    }

    private static string RemoveVietnameseAccents(string text)
    {
        // đ / Đ don't decompose under FormD, replace explicitly first.
        text = text.Replace('đ', 'd').Replace('Đ', 'd');

        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

public record NormalizedName(string? Original, string Normalized);
