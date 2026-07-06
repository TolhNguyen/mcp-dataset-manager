namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure validation rules for dataset knowledge entries. No I/O, no DB access —
/// safe to unit test directly (see KnowledgeGuardTests).
/// </summary>
public static class KnowledgeGuard
{
    public static readonly string[] Kinds =
        ["note", "column_meaning", "business_rule", "metric_definition", "join_hint", "document"];

    public const int MaxActivePerDataset = 200;
    public const int MaxContentChars = 4000;
    public const int MaxTitleChars = 255;

    /// <summary>
    /// Validates a create request. A null/blank kind defaults to "note" per spec.
    /// Returns an error message, or null when valid.
    /// </summary>
    public static string? ValidateCreate(string? kind, string? title, string? content)
    {
        var effectiveKind = string.IsNullOrWhiteSpace(kind) ? "note" : kind;
        if (!Kinds.Contains(effectiveKind))
        {
            return $"kind must be one of: {string.Join(", ", Kinds)}.";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return "title is required.";
        }
        if (title.Length > MaxTitleChars)
        {
            return $"title must be at most {MaxTitleChars} characters.";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return "content is required.";
        }
        if (content.Length > MaxContentChars)
        {
            return $"content must be at most {MaxContentChars} characters.";
        }

        return null;
    }

    /// <summary>
    /// Validates an update request. At least one of title/content/pinned must be provided.
    /// Note: this takes an extra `pinned` argument beyond the brief's abbreviated
    /// ValidateUpdate(title, content) sketch, because "at least one of title/content/pinned"
    /// cannot be expressed without knowing whether pinned was supplied. KnowledgeService is
    /// the only caller, so this does not break any cross-task contract.
    /// Returns an error message, or null when valid.
    /// </summary>
    public static string? ValidateUpdate(string? title, string? content, bool? pinned)
    {
        if (title is null && content is null && pinned is null)
        {
            return "At least one of title, content, or pinned must be provided.";
        }

        if (title is not null)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return "title cannot be empty.";
            }
            if (title.Length > MaxTitleChars)
            {
                return $"title must be at most {MaxTitleChars} characters.";
            }
        }

        if (content is not null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "content cannot be empty.";
            }
            if (content.Length > MaxContentChars)
            {
                return $"content must be at most {MaxContentChars} characters.";
            }
        }

        return null;
    }
}
