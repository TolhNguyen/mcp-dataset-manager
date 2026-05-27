namespace ExcelDatasetManager.Api.Models;

// ============================================================
// API request / response DTOs
// ============================================================

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);

public record UserDto(Guid Id, string Email);

public record QueryOptions(int? MaxRows, string? ReturnFormat, bool? IncludeSql, bool? IncludeProfile);
public record QueryRequest(string QueryType, string Sql, QueryOptions? Options);

public record CreateDatasetApiKeyRequest(string Name);

// ============================================================
// Internal domain records
// ============================================================

public record DatasetRecord(
    Guid Id,
    Guid UserId,
    string Name,
    string OriginalFileName,
    string FileType,
    string StoredFileName,
    long FileSizeBytes,
    string? ManifestFileName,
    string Status,
    int TableCount,
    long TotalRows,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt
);

public record DatasetTableRecord(
    Guid Id,
    Guid DatasetId,
    string TableName,
    string SourceName,
    string SourceType,
    string DataFileName,
    long RowCount,
    int ColumnCount
);

public record DatasetColumnRecord(
    Guid Id,
    Guid DatasetTableId,
    int OrdinalPosition,
    string? OriginalHeader,
    string NormalizedName,
    string? DisplayName,
    string[] Aliases,
    string? InferredType,
    string? SemanticType,
    long NullCount,
    long DistinctCount,
    string SampleValuesJson
);

public record DownloadFile(string Path, string ContentType, string DownloadName);
