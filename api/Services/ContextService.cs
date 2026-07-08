using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public record ContextRequest(Guid[] DatasetIds, string[]? Tables, string Detail);

/// <summary>
/// Assembles the AI-facing context payload (schema + sample rows + knowledge memory)
/// for one or more of the caller's datasets, applying a token cap via ContextShaper.
/// Replaces manifest.md as the primary schema source for AI.
/// </summary>
public class ContextService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    DatasetService datasetService,
    AiTokenBudgetService tokenBudget)
{
    public async Task<ApiResult<object>> BuildAsync(Guid userId, ContextRequest req, CancellationToken ct)
    {
        var maxDatasets = configuration.GetValue<int?>("Query:MaxDatasetsPerQuery") ?? 3;
        if (req.DatasetIds.Length is 0 || req.DatasetIds.Length > maxDatasets)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError,
                $"Yêu cầu 1–{maxDatasets} dataset_ids.", new { max_datasets_per_query = maxDatasets });
        }

        var inputs = new List<ContextDatasetInput>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        foreach (var datasetId in req.DatasetIds)
        {
            var dataset = await datasetService.GetDatasetRecordAsync(userId, datasetId, ct);
            if (dataset is null)
            {
                return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, $"Dataset {datasetId} không tồn tại.");
            }
            if (!string.Equals(dataset.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResult<object>.Fail(ErrorCodes.DatasetNotReady,
                    $"Dataset {datasetId} đang ở trạng thái '{dataset.Status}'.");
            }

            inputs.Add(await LoadDatasetAsync(conn, dataset, ct));
        }

        var filter = req.Tables is { Length: > 0 }
            ? new HashSet<string>(req.Tables, StringComparer.OrdinalIgnoreCase)
            : null;
        var safeMaxTokens = configuration.GetValue<int?>("Query:SafeMaxTokens") ?? 32000;

        var result = ContextShaper.Shape(inputs, filter, req.Detail, safeMaxTokens, o => tokenBudget.EstimateTokens(o));
        return ApiResult<object>.Ok(result.Payload);
    }

    private static async Task<ContextDatasetInput> LoadDatasetAsync(
        NpgsqlConnection conn, DatasetRecord dataset, CancellationToken ct)
    {
        var alias = dataset.Alias ?? "ds";
        var isExternal = string.Equals(dataset.SourceKind, "external_db", StringComparison.OrdinalIgnoreCase);
        var dialect = DialectNotes.MapDialect(dataset.SourceKind, dataset.FileType);

        var tableRows = (await conn.QueryAsync<TableRow>("""
            SELECT id AS Id, table_name AS TableName, sample_rows::text AS SampleRowsJson
            FROM dataset_tables WHERE dataset_id = @DatasetId ORDER BY table_name
            """, new { DatasetId = dataset.Id })).ToList();

        var columnRows = (await conn.QueryAsync<ColumnRow>("""
            SELECT c.dataset_table_id AS DatasetTableId, c.normalized_name AS Name,
                   c.inferred_type AS Type, c.display_name AS DisplayName, c.aliases AS Aliases
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId
            ORDER BY c.dataset_table_id, c.ordinal_position
            """, new { DatasetId = dataset.Id })).ToList();

        var tables = tableRows.Select(t => new ContextTableInput(
            TableName: t.TableName,
            QualifiedName: $"{alias}.{t.TableName}",
            Columns: columnRows.Where(c => c.DatasetTableId == t.Id)
                .Select(c => new ContextColumnInput(c.Name, c.Type ?? "UNKNOWN", c.DisplayName, c.Aliases ?? []))
                .ToList(),
            SampleRows: isExternal ? ParseSampleRows(t.SampleRowsJson) : null)).ToList();

        var knowledgeRows = (await conn.QueryAsync<KnowledgeRow>("""
            SELECT kind AS Kind, title AS Title, content AS Content, source AS Source, pinned AS Pinned
            FROM dataset_knowledge_entries
            WHERE dataset_id = @DatasetId AND archived_at IS NULL
            ORDER BY pinned DESC, updated_at DESC
            LIMIT 40
            """, new { DatasetId = dataset.Id })).ToList();

        var activeCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dataset_knowledge_entries WHERE dataset_id = @DatasetId AND archived_at IS NULL",
            new { DatasetId = dataset.Id });

        var knowledge = knowledgeRows
            .Select(k => new ContextKnowledgeInput(k.Kind, k.Title, k.Content, k.Source, k.Pinned))
            .ToList();

        var schemaToken = SchemaTokenService.Compute(
            tables.Select(t => (
                t.TableName,
                (IReadOnlyList<(string, string)>)t.Columns.Select(c => (c.Name, c.Type)).ToList())));
        var notes = DialectNotes.For(dialect);

        return new ContextDatasetInput(
            DatasetId: dataset.Id,
            Name: dataset.Name,
            Alias: dataset.Alias,
            SourceKind: dataset.SourceKind,
            Provider: isExternal ? dataset.FileType : null,
            Dialect: dialect,
            Tables: tables,
            Knowledge: knowledge,
            ActiveKnowledgeCount: activeCount,
            SchemaToken: schemaToken,
            DialectNotes: notes);
    }

    private static List<object?[]>? ParseSampleRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<object?[]>>(json); }
        catch { return null; }
    }

    private sealed record TableRow(Guid Id, string TableName, string? SampleRowsJson);
    private sealed record KnowledgeRow(string Kind, string Title, string Content, string Source, bool Pinned);

    // Class (not positional record) so Dapper maps the text[] `aliases` column via a property
    // setter — Dapper's positional-constructor matching does not bind a string[] array param.
    private sealed class ColumnRow
    {
        public Guid DatasetTableId { get; init; }
        public string Name { get; init; } = "";
        public string? Type { get; init; }
        public string? DisplayName { get; init; }
        public string[]? Aliases { get; init; }
    }
}
