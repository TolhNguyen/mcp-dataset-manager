namespace ExcelDatasetManager.Api.Services.Connectors;

public interface IExternalDbConnector
{
    string Provider { get; }

    Task<ConnectorTestResult> TestAsync(DbConnectionConfig config, CancellationToken ct);

    Task<List<ExternalTableInfo>> ListTablesAsync(DbConnectionConfig config, CancellationToken ct);

    /// <summary>2 dòng mẫu của 1 bảng; mỗi cell ToString() cắt 200 ký tự; lỗi → trả list rỗng, không throw.</summary>
    Task<List<object?[]>> GetSampleRowsAsync(DbConnectionConfig config, string queryableName, CancellationToken ct);

    /// <summary>Chạy SQL ĐÃ validate + ĐÃ wrap row cap. Session read-only nếu provider hỗ trợ.</summary>
    Task<ExternalQueryResult> ExecuteQueryAsync(DbConnectionConfig config, string sql, int maxRows, int timeoutSeconds, CancellationToken ct);
}
