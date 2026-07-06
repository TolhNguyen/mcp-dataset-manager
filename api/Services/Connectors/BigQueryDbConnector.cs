using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Live-query connector for Google BigQuery data sources. Unlike the relational connectors there is
/// no per-call "connection" to open — <see cref="BigQueryClient"/> wraps a REST client authenticated
/// via a service-account JSON key (config.ServiceAccountJson, a JSON string per Task 2's convention).
/// BigQuery has no session-level read-only switch either, so — same as MSSQL — enforcement here is
/// two-layered: (1) <see cref="ExternalQueryGuard"/> restricts SQL to SELECT/WITH and forbids
/// DML/DDL/EXPORT/LOAD before it ever reaches this connector, and (2) every query additionally carries
/// a hard <c>MaximumBytesBilled</c> cap (config.MaxBytesBilled) so a runaway/expensive query fails
/// instead of blowing through the customer's cost budget.
/// </summary>
public class BigQueryDbConnector(ILogger<BigQueryDbConnector> logger) : IExternalDbConnector
{
    public string Provider => ExternalDbProviders.BigQuery;

    private const int ConnectTimeoutSeconds = 10;

    public async Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(config);
            await client.ExecuteQueryAsync(
                "SELECT 1",
                parameters: null,
                BuildQueryOptions(config),
                BuildResultsOptions(ConnectTimeoutSeconds),
                cancellationToken: ct);

            return new ConnectorTestResult(true, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BigQuery TestAsync failed for project {ProjectId}, dataset {Dataset}", config.ProjectId, config.BigQueryDataset);
            return new ConnectorTestResult(false, ex.Message, null);
        }
    }

    public async Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct)
    {
        var client = CreateClient(config);
        var result = new List<ExternalTableInfo>();

        await foreach (var table in client.ListTablesAsync(config.BigQueryDataset).WithCancellation(ct))
        {
            var tableId = table.Reference.TableId;
            var full = await client.GetTableAsync(config.BigQueryDataset, tableId, cancellationToken: ct);

            var columns = (full.Schema?.Fields ?? [])
                .Select(f => new ExternalColumnInfo(
                    f.Name,
                    f.Type,
                    !string.Equals(f.Mode, "REQUIRED", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var queryableName = $"{config.BigQueryDataset}.{tableId}";
            result.Add(new ExternalTableInfo(queryableName, queryableName, columns));
        }

        return result;
    }

    public async Task<List<object?[]>> GetSampleRowsAsync(DbConnectionConfig config, string queryableName, CancellationToken ct)
    {
        var quoted = QueryableIdentifier.TryQuote(queryableName, '`');
        if (quoted is null)
        {
            return [];
        }

        try
        {
            var client = CreateClient(config);
            var results = await client.ExecuteQueryAsync(
                $"SELECT * FROM {quoted} LIMIT 2",
                parameters: null,
                BuildQueryOptions(config),
                BuildResultsOptions(ConnectTimeoutSeconds),
                cancellationToken: ct);

            var fieldNames = results.Schema.Fields.Select(f => f.Name).ToList();
            var rows = new List<object?[]>();
            foreach (var row in results)
            {
                var values = new object?[fieldNames.Count];
                for (var i = 0; i < fieldNames.Count; i++)
                {
                    values[i] = TruncateCell(row[fieldNames[i]]);
                }

                rows.Add(values);
            }

            return rows;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BigQuery GetSampleRowsAsync failed for table {QueryableName}", queryableName);
            return [];
        }
    }

    public async Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        var client = CreateClient(config);
        var results = await client.ExecuteQueryAsync(
            sql,
            parameters: null,
            BuildQueryOptions(config),
            BuildResultsOptions(timeoutSeconds),
            cancellationToken: ct);

        var columns = results.Schema.Fields.Select(f => (f.Name, f.Type)).ToList();

        var rows = new List<object?[]>();
        foreach (var row in results)
        {
            if (rows.Count >= maxRows)
            {
                break;
            }

            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = ConvertValue(row[columns[i].Name]);
            }

            rows.Add(values);
        }

        return new ExternalQueryResult(columns, rows, rows.Count >= maxRows);
    }

    private static BigQueryClient CreateClient(DbConnectionConfig config)
    {
        var credential = GoogleCredential.FromJson(config.ServiceAccountJson);
        return BigQueryClient.Create(config.ProjectId, credential);
    }

    private static QueryOptions BuildQueryOptions(DbConnectionConfig config) => new()
    {
        MaximumBytesBilled = config.MaxBytesBilled,
        UseLegacySql = false,
    };

    private static GetQueryResultsOptions BuildResultsOptions(int timeoutSeconds) => new()
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds),
    };

    private static object? ConvertValue(object? raw) => raw switch
    {
        null => null,
        DateTime dt => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        _ => raw,
    };

    private static string? TruncateCell(object? value)
    {
        var s = ConvertValue(value)?.ToString();
        return s is { Length: > 200 } ? s[..200] : s;
    }
}
