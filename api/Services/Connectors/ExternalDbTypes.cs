namespace ExcelDatasetManager.Api.Services.Connectors;

public static class ExternalDbProviders
{
    public const string PostgreSql = "postgresql";
    public const string MySql = "mysql";
    public const string MsSql = "mssql";
    public const string BigQuery = "bigquery";
    public static readonly string[] All = [PostgreSql, MySql, MsSql, BigQuery];
    public static bool IsValid(string? provider) => provider is not null && All.Contains(provider);
}

public record ExternalColumnInfo(string Name, string DataType, bool IsNullable);

// QueryableName = tên AI dùng trực tiếp trong SQL (vd: "public.orders", "dbo.Orders", "orders", "my_dataset.orders").
// SourceLabel   = nhãn hiển thị cho user (schema.table gốc).
public record ExternalTableInfo(string QueryableName, string SourceLabel, List<ExternalColumnInfo> Columns);

public record ExternalQueryResult(
    List<(string Name, string Type)> Columns,
    List<object?[]> Rows,
    bool Truncated);

// Warning: vd "Tài khoản có vẻ có quyền ghi — nên dùng tài khoản chỉ SELECT."
public record ConnectorTestResult(bool Success, string? Error, string? Warning);
