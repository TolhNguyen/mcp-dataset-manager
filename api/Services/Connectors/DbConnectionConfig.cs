using System.Text.Json;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>
/// Parsed + validated connection config for an external live-query data source.
/// Relational providers (postgresql/mysql/mssql) use Host/Port/Database/Username/Password/Ssl.
/// BigQuery uses ProjectId/BigQueryDataset/ServiceAccountJson/MaxBytesBilled.
/// </summary>
public record DbConnectionConfig(
    string Provider,
    string? Host, int? Port, string? Database, string? Username, string? Password, bool Ssl,
    // BigQuery:
    string? ProjectId, string? BigQueryDataset, string? ServiceAccountJson, long? MaxBytesBilled)
{
    private const long DefaultMaxBytesBilled = 1_073_741_824L; // 1GB

    /// <summary>
    /// Redacts this config's secrets (password, service-account JSON) from an arbitrary text —
    /// used to scrub driver exception messages before they reach a response or log.
    /// </summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var scrubbed = text;
        if (!string.IsNullOrEmpty(Password)) scrubbed = scrubbed.Replace(Password, "***");
        if (!string.IsNullOrEmpty(ServiceAccountJson)) scrubbed = scrubbed.Replace(ServiceAccountJson, "***");
        return scrubbed;
    }

    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Parses + validates a raw JSON config for the given provider. Raw JSON uses snake_case
    /// property names (host, port, database, username, password, ssl, project_id, dataset,
    /// service_account_json, max_bytes_billed). Exactly one of the returned tuple members is non-null.
    /// </summary>
    public static (DbConnectionConfig? Config, string? Error) Parse(string provider, JsonElement raw)
    {
        if (!ExternalDbProviders.IsValid(provider))
        {
            return (null, $"Unknown provider '{provider}'.");
        }

        return provider == ExternalDbProviders.BigQuery
            ? ParseBigQuery(raw)
            : ParseRelational(provider, raw);
    }

    private static (DbConnectionConfig? Config, string? Error) ParseRelational(string provider, JsonElement raw)
    {
        var host = GetString(raw, "host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return (null, "Missing required field 'host'.");
        }

        var database = GetString(raw, "database");
        if (string.IsNullOrWhiteSpace(database))
        {
            return (null, "Missing required field 'database'.");
        }

        var username = GetString(raw, "username");
        if (string.IsNullOrWhiteSpace(username))
        {
            return (null, "Missing required field 'username'.");
        }

        var password = GetString(raw, "password");
        if (string.IsNullOrWhiteSpace(password))
        {
            return (null, "Missing required field 'password'.");
        }

        var port = GetInt(raw, "port") ?? DefaultPort(provider);
        var ssl = GetBool(raw, "ssl") ?? true;

        var config = new DbConnectionConfig(
            provider, host, port, database, username, password, ssl,
            ProjectId: null, BigQueryDataset: null, ServiceAccountJson: null, MaxBytesBilled: null);
        return (config, null);
    }

    private static (DbConnectionConfig? Config, string? Error) ParseBigQuery(JsonElement raw)
    {
        var projectId = GetString(raw, "project_id");
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return (null, "Missing required field 'project_id'.");
        }

        var dataset = GetString(raw, "dataset");
        if (string.IsNullOrWhiteSpace(dataset))
        {
            return (null, "Missing required field 'dataset'.");
        }

        var serviceAccountJson = GetString(raw, "service_account_json");
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            return (null, "Missing required field 'service_account_json'.");
        }

        var serviceAccountError = ValidateServiceAccountJson(serviceAccountJson);
        if (serviceAccountError is not null)
        {
            return (null, serviceAccountError);
        }

        var maxBytesBilled = GetLong(raw, "max_bytes_billed") ?? DefaultMaxBytesBilled;

        var config = new DbConnectionConfig(
            ExternalDbProviders.BigQuery,
            Host: null, Port: null, Database: null, Username: null, Password: null, Ssl: true,
            projectId, dataset, serviceAccountJson, maxBytesBilled);
        return (config, null);
    }

    private static string? ValidateServiceAccountJson(string serviceAccountJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(serviceAccountJson);
        }
        catch (JsonException)
        {
            return "Field 'service_account_json' is not valid JSON.";
        }

        using (document)
        {
            var root = document.RootElement;
            var hasClientEmail = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("client_email", out var clientEmail)
                && clientEmail.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(clientEmail.GetString());

            if (!hasClientEmail)
            {
                return "Field 'service_account_json' must be a JSON object containing a 'client_email' property.";
            }
        }

        return null;
    }

    private static int DefaultPort(string provider) => provider switch
    {
        ExternalDbProviders.PostgreSql => 5432,
        ExternalDbProviders.MySql => 3306,
        ExternalDbProviders.MsSql => 1433,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No default port for provider."),
    };

    private static string? GetString(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? GetLong(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool? GetBool(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    /// <summary>Serializes this config to JSON for encrypted storage (snake_case property names).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, StorageJsonOptions);

    /// <summary>Deserializes a config previously produced by <see cref="ToJson"/>.</summary>
    public static DbConnectionConfig FromJson(string json) =>
        JsonSerializer.Deserialize<DbConnectionConfig>(json, StorageJsonOptions)
        ?? throw new InvalidOperationException("Invalid DbConnectionConfig JSON.");
}
