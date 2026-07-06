using Dapper;
using Microsoft.Data.SqlClient;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Live-query connector for Microsoft SQL Server data sources. All read paths open a fresh,
/// short-lived connection (no custom pooling) and enforce a 10s connect timeout.
/// Unlike PostgreSQL/MySQL, SQL Server has no per-session "read only" setting we can flip on a
/// plain login (SET TRANSACTION ISOLATION LEVEL controls isolation, not writability; ALTER
/// DATABASE ... SET READ_ONLY is database-wide and requires elevated permissions we should not
/// assume/request). So for MSSQL, read-only enforcement relies entirely on
/// <see cref="ExternalQueryGuard"/> validating the SQL text (SELECT/WITH only, forbidden tokens,
/// no xp_/sp_ procs) before it ever reaches this connector — there is no server-side belt-and-
/// suspenders here the way there is for Postgres/MySQL. Customers should still be told to use a
/// SELECT-only login where possible.
/// </summary>
public class MsSqlDbConnector(ILogger<MsSqlDbConnector> logger) : IExternalDbConnector
{
    public string Provider => ExternalDbProviders.MsSql;

    private const int ConnectTimeoutSeconds = 10;

    private sealed record TableRow(string TableSchema, string TableName);

    private sealed record ColumnRow(string TableSchema, string TableName, string ColumnName, string DataType, string IsNullable);

    public async Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(config));
            await conn.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));

            var hasWriteGrant = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
                """
                SELECT CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.database_permissions dp
                    JOIN sys.database_principals pr ON pr.principal_id = dp.grantee_principal_id
                    WHERE dp.permission_name IN ('INSERT', 'UPDATE', 'DELETE')
                      AND dp.state IN ('G', 'W')
                      AND pr.name = USER_NAME()
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
                cancellationToken: ct));

            var warning = hasWriteGrant == true
                ? "Tài khoản có vẻ có quyền ghi — nên dùng tài khoản chỉ SELECT."
                : null;

            return new ConnectorTestResult(true, null, warning);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SQL Server TestAsync failed for {Host}:{Port}/{Database}", config.Host, config.Port, config.Database);
            return new ConnectorTestResult(false, ex.Message, null);
        }
    }

    public async Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct)
    {
        await using var conn = new SqlConnection(BuildConnectionString(config));
        await conn.OpenAsync(ct);

        var tables = (await conn.QueryAsync<TableRow>(new CommandDefinition(
            """
            SELECT TABLE_SCHEMA AS TableSchema, TABLE_NAME AS TableName
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """,
            cancellationToken: ct))).ToList();

        var columns = (await conn.QueryAsync<ColumnRow>(new CommandDefinition(
            """
            SELECT TABLE_SCHEMA AS TableSchema, TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName,
                   DATA_TYPE AS DataType, IS_NULLABLE AS IsNullable
            FROM INFORMATION_SCHEMA.COLUMNS
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
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
            var queryableName = $"{t.TableSchema}.{t.TableName}";
            var tableColumns = columnsByTable.TryGetValue((t.TableSchema, t.TableName), out var list)
                ? list
                : [];
            result.Add(new ExternalTableInfo(queryableName, queryableName, tableColumns));
        }

        return result;
    }

    public async Task<List<object?[]>> GetSampleRowsAsync(DbConnectionConfig config, string queryableName, CancellationToken ct)
    {
        var quoted = QueryableIdentifier.TryQuote(queryableName, '[');
        if (quoted is null)
        {
            return [];
        }

        try
        {
            await using var conn = new SqlConnection(BuildConnectionString(config));
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand($"SELECT TOP 2 * FROM {quoted}", conn);
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
            logger.LogWarning(ex, "SQL Server GetSampleRowsAsync failed for table {QueryableName}", queryableName);
            return [];
        }
    }

    public async Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct)
    {
        await using var conn = new SqlConnection(BuildConnectionString(config));
        await conn.OpenAsync(ct);

        // No session-level read-only switch exists for SQL Server (see class doc comment above) —
        // the SQL itself has already been validated + row-capped by ExternalQueryGuard before it
        // reaches here.
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
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
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = config.Port is int port ? $"{config.Host},{port}" : config.Host,
            InitialCatalog = config.Database,
            UserID = config.Username,
            Password = config.Password,
            Encrypt = config.Ssl,
            // Self-hosted SQL Server instances commonly present a self-signed / non-CA certificate;
            // trusting the server cert (while still encrypting the channel when Ssl=true) mirrors
            // the pragmatic default used by other DB tools connecting to on-prem SQL Server.
            TrustServerCertificate = true,
            ConnectTimeout = ConnectTimeoutSeconds,
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
