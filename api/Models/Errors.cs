namespace ExcelDatasetManager.Api.Models;

public static class ErrorCodes
{
    // Auth
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string EmailExists = "EMAIL_EXISTS";
    public const string ValidationError = "VALIDATION_ERROR";
    public const string PasswordTooShort = "PASSWORD_TOO_SHORT";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";

    // Dataset
    public const string DatasetNotFound = "DATASET_NOT_FOUND";
    public const string DatasetNotReady = "DATASET_NOT_READY";
    public const string DatasetLimitReached = "DATASET_LIMIT_REACHED";
    public const string InvalidFile = "INVALID_FILE";
    public const string InvalidFileType = "INVALID_FILE_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileParseFailed = "FILE_PARSE_FAILED";
    public const string NoTableFound = "NO_TABLE_FOUND";
    public const string StorageError = "STORAGE_ERROR";

    // External DB connections / live-query datasets
    public const string ConnectionNotFound = "CONNECTION_NOT_FOUND";
    public const string ConnectionInUse = "CONNECTION_IN_USE";
    public const string TooManyTablesRequested = "TOO_MANY_TABLES_REQUESTED";
    public const string ExternalSchemaFetchFailed = "EXTERNAL_SCHEMA_FETCH_FAILED";

    // Query
    public const string InvalidSql = "INVALID_SQL";
    public const string NonReadOnlySql = "NON_READONLY_SQL";
    public const string ColumnNotFound = "COLUMN_NOT_FOUND";
    public const string TableNotFound = "TABLE_NOT_FOUND";
    public const string QueryTimeout = "QUERY_TIMEOUT";
    public const string QueryFailed = "QUERY_FAILED";
    public const string TokenBudgetConfirmationRequired = "TOKEN_BUDGET_CONFIRMATION_REQUIRED";
    public const string TokenBudgetHardLimitExceeded = "TOKEN_BUDGET_HARD_LIMIT_EXCEEDED";
    public const string InvalidConfirmation = "INVALID_CONFIRMATION";

    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Internal = "INTERNAL_ERROR";
}

public sealed class ApiError
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public object? Details { get; init; }
    public bool Retryable { get; init; }
}

public sealed class ApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public ApiError? Error { get; init; }

    public static ApiResult<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResult<T> Fail(string code, string message, object? details = null, bool retryable = false)
        => new() { Success = false, Error = new ApiError { Code = code, Message = message, Details = details, Retryable = retryable } };
}
