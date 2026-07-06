using Dapper;
using MySqlConnector;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Live-query connector for MySQL data sources. All read paths open a fresh, short-lived connection
/// (no custom pooling) and enforce a 10s connect timeout. MySQL has no connection-string equivalent of
/// PostgreSQL's `default_transaction_read_only`, so ExecuteQueryAsync issues `SET SESSION TRANSACTION
/// READ ONLY;` right after opening the connection, before running the (already validated + row-capped
/// by <see cref="ExternalQueryGuard"/>) query.
/// </summary>
public class MySqlDbConnector(ILogger<MySqlDbConnector> logger) : IExternalDbConnector
{
    public string Provider => ExternalDbProviders.MySql;

    private const int ConnectTimeoutSeconds = 10;

    private sealed record TableRow(string TableName);

    private sealed record ColumnRow(string TableName, string ColumnName, string DataType, string IsNullable);

    public async Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(BuildConnectionString(config));
            await conn.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));

            var grants = (await conn.QueryAsync<string>(new CommandDefinition("SHOW GRANTS", cancellationToken: ct))).ToList();
            var hasWriteGrant = grants.Any(g =>
                g.Contains("ALL PRIVILEGES", StringComparison.OrdinalIgnoreCase)
                || g.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                || g.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || g.Contains("DELETE", StringComparison.OrdinalIgnoreCase));

            var warning = hasWriteGrant
                ? "Tài khoản có vẻ có quyền ghi — nên dùng tài khoản chỉ SELECT."
                : null;

            return new ConnectorTestResult(true, null, warning);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MySQL TestAsync failed for {Host}:{Port}/{Database}", config.Host, config.Port, config.Database);
            return new ConnectorTestResult(false, ex.Message, null);
        }
    }

    public async Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(BuildConnectionString(config));
        await conn.OpenAsync(ct);

        var tables = (await conn.QueryAsync<TableRow>(new CommandDefinition(
            """
            SELECT table_name AS TableName
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
            ORDER BY table_name
            """,
            cancellationToken: ct))).ToList();

        var columns = (await conn.QueryAsync<ColumnRow>(new CommandDefinition(
            """
            SELECT table_name AS TableName, column_name AS ColumnName, data_type AS DataType, is_nullable AS IsNullable
            FROM information_schema.columns
            WHERE table_schema = DATABASE()
            ORDER BY table_name, ordinal_position
            """,
            cancellationToken: ct))).ToList();

        var columnsByTable = columns
            .GroupBy(c => c.TableName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new ExternalColumnInfo(
                    c.ColumnName,
                    c.DataType,
                    string.Equals(c.IsNullable, "YES", StringComparison.OrdinalIgnoreCase))).ToList());

        var result = new List<ExternalTableInfo>();
        foreach (var t in tables)
        {
            var sourceLabel = $"{config.Database}.{t.TableName}";
            var tableColumns = columnsByTable.TryGetValue(t.TableName, out var list) ? list : [];
            result.Add(new ExternalTableInfo(t.TableName, sourceLabel, tableColumns));
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
            await using var conn = new MySqlConnection(BuildConnectionString(config));
            await conn.OpenAsync(ct);

            await using var cmd = new MySqlCommand($"SELECT * FROM {quoted} LIMIT 2", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = TruncateCell(reader.IsDBNull(i) ? null : reader.GetValue(i));
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MySQL GetSampleRowsAsync failed for table {QueryableName}", queryableName);
            return [];
        }
    }

    public async Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(BuildConnectionString(config));
        await conn.OpenAsync(ct);

        await using (var readOnlyCmd = new MySqlCommand("SET SESSION TRANSACTION READ ONLY;", conn) { CommandTimeout = timeoutSeconds })
        {
            await readOnlyCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<(string Name, string Type)>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add((reader.GetName(i), reader.GetDataTypeName(i)));
        }

        var rows = new List<object?[]>();
        while (rows.Count < maxRows && await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = ConvertValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }

            rows.Add(row);
        }

        return new ExternalQueryResult(columns, rows, rows.Count >= maxRows);
    }

    private static string BuildConnectionString(DbConnectionConfig config)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = config.Host,
            Port = (uint)(config.Port ?? 3306),
            Database = config.Database,
            UserID = config.Username,
            Password = config.Password,
            SslMode = config.Ssl ? MySqlSslMode.Required : MySqlSslMode.None,
            ConnectionTimeout = ConnectTimeoutSeconds,
        };

        return builder.ConnectionString;
    }

    private static object? ConvertValue(object? raw) => raw switch
    {
        null => null,
        DateTime dt => dt.ToString("O"),
        _ => raw,
    };

    private static string? TruncateCell(object? value)
    {
        var s = value?.ToString();
        return s is { Length: > 200 } ? s[..200] : s;
    }
}
