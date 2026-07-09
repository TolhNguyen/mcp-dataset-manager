using Dapper;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Schema-token gate for query endpoints. Endpoints decide whether the caller is subject to the
/// gate; this helper computes the current token and builds AI-facing error envelopes.
/// </summary>
public static class SchemaTokenGate
{
    public static async Task<string> ComputeCurrentAsync(NpgsqlDataSource dataSource, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<(string TableName, string Name, string? Type, int Ordinal)>("""
            SELECT t.table_name AS TableName, c.normalized_name AS Name,
                   c.inferred_type AS Type, c.ordinal_position AS Ordinal
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId
            ORDER BY t.table_name, c.ordinal_position
            """, new { DatasetId = datasetId })).ToList();

        var tables = rows
            .GroupBy(r => r.TableName)
            .Select(g => (g.Key,
                (IReadOnlyList<(string, string)>)g.Select(r => (r.Name, r.Type ?? "UNKNOWN")).ToList()));

        return SchemaTokenService.Compute(tables);
    }

    /// <summary>
    /// Widget updates only pass the gate check when the caller is a PAT AND the request actually
    /// writes new SQL — title/position/chart-only updates are exempt.
    /// </summary>
    public static bool ShouldGateWidgetUpdate(bool isApiKeyPrincipal, string? sql)
        => isApiKeyPrincipal && !string.IsNullOrWhiteSpace(sql);

    public static object? BuildGateError(string? provided, string expected, Guid datasetId)
    {
        if (SchemaTokenService.Matches(provided, expected)) return null;

        var code = string.IsNullOrWhiteSpace(provided) ? ErrorCodes.ContextRequired : ErrorCodes.SchemaChanged;
        var message = code == ErrorCodes.ContextRequired
            ? "Call get_context for this dataset first, then pass the schema_token it returns. Do NOT guess table or column names."
            : "The dataset schema changed since you last read it. Call get_context again and use the new schema_token.";

        return new
        {
            success = false,
            dataset_id = datasetId,
            status = "failed",
            error = new
            {
                code,
                message,
                assistant_instruction = "Report this to the user only if it recurs. Do not fabricate data. Call get_context, then retry with the schema_token.",
                retryable = true
            }
        };
    }
}
