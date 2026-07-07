using System.Globalization;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Produces a stable, URL/SQL-safe alias slug for a dataset name, unique per user.
/// The alias is used as a DuckDB schema name in multi-dataset queries (e.g. sales.orders).
/// </summary>
public static class AliasGenerator
{
    public const int MaxBaseLength = 55; // leaves room for a "_NN" collision suffix within VARCHAR(64)

    /// <summary>
    /// Lowercase, accent-folded, non-[a-z0-9] collapsed to '_', trimmed, capped; empty → "ds".
    /// </summary>
    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "ds";

        var folded = FoldAccents(name).ToLowerInvariant();
        var sb = new StringBuilder(folded.Length);
        var lastUnderscore = false;

        foreach (var ch in folded)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
            else if (!lastUnderscore)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }

        var slug = sb.ToString().Trim('_');
        if (slug.Length > MaxBaseLength) slug = slug[..MaxBaseLength].Trim('_');
        return slug.Length == 0 ? "ds" : slug;
    }

    /// <summary>Returns baseSlug, or baseSlug_2, _3, … until one is not in existingForUser.</summary>
    public static string MakeUnique(string baseSlug, ISet<string> existingForUser)
    {
        if (!existingForUser.Contains(baseSlug)) return baseSlug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseSlug}_{n}";
            if (!existingForUser.Contains(candidate)) return candidate;
        }
    }

    private static string FoldAccents(string input)
    {
        // Đ/đ have no combining-mark decomposition; map explicitly, then strip diacritics.
        var pre = input.Replace('Đ', 'D').Replace('đ', 'd');
        var normalized = pre.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
