using Dapper;
using Npgsql;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Live-query connector for PostgreSQL data sources. All read paths open a fresh, short-lived
/// connection (no custom pooling) and enforce a 10s connect timeout. ExecuteQueryAsync additionally
/// opens the connection with `Options=-c default_transaction_read_only=on`, so even a misconfigured
/// (writable) customer account cannot mutate data through this path — the SQL itself is already
/// validated + row-capped by <see cref="ExternalQueryGuard"/> before it reaches here.
/// </summary>
public class PostgresDbConnector(ILogger<PostgresDbConnector> logger) : IExternalDbConnector
{
    public string Provider => ExternalDbProviders.PostgreSql;

    private const int ConnectTimeoutSeconds = 10;

    private sealed record TableRow(string TableSchema, string TableName);

    private sealed record ColumnRow(string TableSchema, string TableName, string ColumnName, string DataType, string IsNullable);

    public async Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString(config, readOnly: false));
            await conn.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));

            var hasWriteGrant = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                """
                SELECT bool_or(privilege_type IN ('INSERT', 'UPDATE', 'DELETE'))
                FROM information_schema.role_table_grants
                WHERE grantee = current_user
                """,
                cancellationToken: ct));

            var warning = hasWriteGrant == true
                ? "Tài khoản có vẻ có quyền ghi — nên dùng tài khoản chỉ SELECT."
                : null;

            return new ConnectorTestResult(true, null, warning);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostgreSQL TestAsync failed for {Host}:{Port}/{Database}", config.Host, config.Port, config.Database);
            return new ConnectorTestResult(false, ex.Message, null);
        }
    }

    public async Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(config, readOnly: false));
        await conn.OpenAsync(ct);

        var tables = (await conn.QueryAsync<TableRow>(new CommandDefinition(
            """
            SELECT table_schema AS "TableSchema", table_name AS "TableName"
            FROM information_schema.tables
            WHERE table_type IN ('BASE TABLE', 'VIEW')
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name
            """,
            cancellationToken: ct))).ToList();

        var columns = (await conn.QueryAsync<ColumnRow>(new CommandDefinition(
            """
            SELECT table_schema AS "TableSchema", table_name AS "TableName", column_name AS "ColumnName",
                   data_type AS "DataType", is_nullable AS "IsNullable"
            FROM information_schema.columns
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name, ordinal_position
            """,
            cancellationToken: ct))).ToList();

        var columnsByTable = columns
            .GroupBy(c => (c.TableSchema, c.TableName))
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new ExternalColumnInfo(
                    c.ColumnName,
                    c.DataType,
                    string.Equals(c.IsNullable, "YES", StringComparison.OrdinalIgnoreCase))).ToList());

        var result = new List<ExternalTableInfo>();
        foreach (var t in tables)
        {
            var queryableName = t.TableSchema == "public" ? t.TableName : $"{t.TableSchema}.{t.TableName}";
            var sourceLabel = $"{t.TableSchema}.{t.TableName}";
            var tableColumns = columnsByTable.TryGetValue((t.TableSchema, t.TableName), out var list)
                ? list
                : [];
            result.Add(new ExternalTableInfo(queryableName, sourceLabel, tableColumns));
        }

        return result;
    }

    public async Task<List<object?[]>> GetSampleRowsAsync(DbConnectionConfig config, string queryableName, CancellationToken ct)
    {
        var quoted = QueryableIdentifier.TryQuote(queryableName, '"');
        if (quoted is null)
        {
            return [];
        }

        try
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString(config, readOnly: false));
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand($"SELECT * FROM {quoted} LIMIT 2", conn);
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
            logger.LogWarning(ex, "PostgreSQL GetSampleRowsAsync failed for table {QueryableName}", queryableName);
            return [];
        }
    }

    public async Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString(config, readOnly: true));
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
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

    private static string BuildConnectionString(DbConnectionConfig config, bool readOnly)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port ?? 5432,
            Database = config.Database,
            Username = config.Username,
            Password = config.Password,
            SslMode = config.Ssl ? SslMode.Require : SslMode.Disable,
            Timeout = ConnectTimeoutSeconds,
        };

        if (readOnly)
        {
            // Belt-and-suspenders on top of ExternalQueryGuard's static SQL validation: force the
            // server-side session itself into read-only mode so a writable account still can't mutate data.
            builder.Options = "-c default_transaction_read_only=on";
        }

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
