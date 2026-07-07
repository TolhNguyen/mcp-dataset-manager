namespace ExcelDatasetManager.Api.Services;

// ============================================================
// Inputs (DB-free so the shaping is unit-testable)
// ============================================================

public record ContextColumnInput(string Name, string Type, string? DisplayName, string[] Aliases);

public record ContextTableInput(
    string TableName,
    string QualifiedName,
    IReadOnlyList<ContextColumnInput> Columns,
    IReadOnlyList<object?[]>? SampleRows);

public record ContextKnowledgeInput(string Kind, string Title, string Content, string Source, bool Pinned);

public record ContextDatasetInput(
    Guid DatasetId,
    string Name,
    string? Alias,
    string SourceKind,
    string? Provider,
    string Dialect,
    IReadOnlyList<ContextTableInput> Tables,
    IReadOnlyList<ContextKnowledgeInput> Knowledge,
    int ActiveKnowledgeCount);

public record ContextShapeResult(object Payload, bool Downgraded, int TokenEstimate);

/// <summary>
/// Builds the AI-facing context payload from loaded dataset inputs. Pure: no DB, no I/O.
/// full detail = columns with aliases + sample rows + pinned/recent knowledge.
/// summary detail = columns without aliases, no sample rows, pinned knowledge only.
/// If a full payload exceeds safeMaxTokens it is rebuilt as summary with a warning.
/// </summary>
public static class ContextShaper
{
    public const int MaxKnowledgePerDatasetFull = 20;

    public static ContextShapeResult Shape(
        IReadOnlyList<ContextDatasetInput> datasets,
        IReadOnlySet<string>? tableFilter,
        string detail,
        int safeMaxTokens,
        Func<object, int> estimateTokens)
    {
        var wantFull = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);

        var payload = Build(datasets, tableFilter, wantFull, warning: null, estimateTokens, out var estimate);

        if (wantFull && estimate > safeMaxTokens)
        {
            var warning = $"Context vượt {safeMaxTokens} tokens (ước tính {estimate}), đã hạ xuống summary. " +
                          "Lọc bằng tables= để lấy full cho các bảng cần thiết.";
            payload = Build(datasets, tableFilter, useFull: false, warning, estimateTokens, out estimate);
            return new ContextShapeResult(payload, Downgraded: true, estimate);
        }

        return new ContextShapeResult(payload, Downgraded: false, estimate);
    }

    private static object Build(
        IReadOnlyList<ContextDatasetInput> datasets,
        IReadOnlySet<string>? tableFilter,
        bool useFull,
        string? warning,
        Func<object, int> estimateTokens,
        out int estimate)
    {
        var totalActiveKnowledge = datasets.Sum(d => d.ActiveKnowledgeCount);

        var datasetPayloads = datasets.Select(d => (object)new
        {
            dataset_id = d.DatasetId,
            name = d.Name,
            alias = d.Alias,
            source_kind = d.SourceKind,
            provider = d.Provider,
            dialect = d.Dialect,
            tables = d.Tables
                .Where(t => tableFilter is null || tableFilter.Contains(t.TableName))
                .Select(t => (object)new
                {
                    table_name = t.TableName,
                    qualified_name = t.QualifiedName,
                    columns = t.Columns.Select(c => useFull
                        ? (object)new { name = c.Name, type = c.Type, display_name = c.DisplayName, aliases = c.Aliases }
                        : new { name = c.Name, type = c.Type, display_name = c.DisplayName }).ToList(),
                    sample_rows = useFull ? t.SampleRows : null
                }).ToList(),
            knowledge = (useFull
                    ? d.Knowledge.OrderByDescending(k => k.Pinned).Take(MaxKnowledgePerDatasetFull)
                    : d.Knowledge.Where(k => k.Pinned))
                .Select(k => (object)new { kind = k.Kind, title = k.Title, content = k.Content, source = k.Source, pinned = k.Pinned })
                .ToList()
        }).ToList();

        var body = new
        {
            success = true,
            datasets = datasetPayloads,
            memory_instructions =
                $"Bộ nhớ dataset có {totalActiveKnowledge} entries. Khi người dùng cung cấp thông tin nghiệp vụ mới " +
                "hoặc sửa cách hiểu của bạn, hãy lưu bằng save_dataset_knowledge.",
            warning
        };

        estimate = estimateTokens(body);
        // token_estimate is part of the returned payload but must not affect the estimate it reports,
        // so it is layered on after measuring.
        return new
        {
            body.success,
            body.datasets,
            body.memory_instructions,
            body.warning,
            token_estimate = estimate
        };
    }
}
