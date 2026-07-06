using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services.Connectors;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Manages external (live-query) database connections: encrypted-at-rest storage of connection
/// configs, masked responses (never leak host/password/service_account_json to the client), and
/// connection testing. Table schema for a specific dataset is handled by
/// <see cref="ExternalSchemaService"/>, which depends on <see cref="GetConfigAsync"/> to decrypt
/// configs server-side.
/// </summary>
public class DbConnectionService(
    NpgsqlDataSource dataSource,
    SecretProtector protector,
    IEnumerable<IExternalDbConnector> connectors,
    ILogger<DbConnectionService> logger)
{
    private const string SelectSql = """
        SELECT id AS Id,
               name AS Name,
               provider AS Provider,
               encrypted_config AS EncryptedConfig,
               last_test_status AS LastTestStatus,
               last_test_at AS LastTestAt,
               last_test_error AS LastTestError,
               created_at AS CreatedAt
        FROM db_connections
        """;

    // ============================================================
    // Create / List / Update / Delete
    // ============================================================

    public async Task<ApiResult<object>> CreateAsync(
        Guid userId, string name, string provider, JsonElement config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "Name is required.");
        }

        var (parsed, error) = DbConnectionConfig.Parse(provider, config);
        if (parsed is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, error ?? "Invalid configuration.");
        }

        var id = Guid.NewGuid();
        var trimmedName = name.Trim();
        var encrypted = protector.Protect(parsed.ToJson());
        var createdAt = DateTime.UtcNow;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO db_connections (id, user_id, name, provider, encrypted_config)
            VALUES (@Id, @UserId, @Name, @Provider, @EncryptedConfig)
            """, new { Id = id, UserId = userId, Name = trimmedName, Provider = provider, EncryptedConfig = encrypted });

        var dto = DbConnectionMasking.Build(id, trimmedName, provider, parsed, null, null, null, createdAt);
        return ApiResult<object>.Ok(dto);
    }

    public async Task<List<object>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ConnectionRow>(
            SelectSql + " WHERE user_id = @UserId ORDER BY created_at DESC",
            new { UserId = userId })).ToList();

        var result = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var config = DbConnectionConfig.FromJson(protector.Unprotect(row.EncryptedConfig));
            result.Add(DbConnectionMasking.Build(
                row.Id, row.Name, row.Provider, config,
                row.LastTestStatus, row.LastTestAt, row.LastTestError, row.CreatedAt));
        }

        return result;
    }

    public async Task<ApiResult<object>> UpdateAsync(
        Guid userId, Guid id, string? name, JsonElement? config, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ConnectionRow>(
            SelectSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        if (row is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var newName = string.IsNullOrWhiteSpace(name) ? row.Name : name.Trim();

        DbConnectionConfig parsedConfig;
        var newEncrypted = row.EncryptedConfig;

        if (config is { } raw && raw.ValueKind != JsonValueKind.Undefined && raw.ValueKind != JsonValueKind.Null)
        {
            var (parsed, error) = DbConnectionConfig.Parse(row.Provider, raw);
            if (parsed is null)
            {
                return ApiResult<object>.Fail(ErrorCodes.ValidationError, error ?? "Invalid configuration.");
            }

            parsedConfig = parsed;
            newEncrypted = protector.Protect(parsed.ToJson());
        }
        else
        {
            parsedConfig = DbConnectionConfig.FromJson(protector.Unprotect(row.EncryptedConfig));
        }

        await conn.ExecuteAsync("""
            UPDATE db_connections
            SET name = @Name, encrypted_config = @EncryptedConfig, updated_at = NOW()
            WHERE id = @Id AND user_id = @UserId
            """, new { Name = newName, EncryptedConfig = newEncrypted, Id = id, UserId = userId });

        var dto = DbConnectionMasking.Build(
            id, newName, row.Provider, parsedConfig,
            row.LastTestStatus, row.LastTestAt, row.LastTestError, row.CreatedAt);
        return ApiResult<object>.Ok(dto);
    }

    public async Task<ApiResult<object>> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM db_connections WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        if (exists == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var datasetsUsingIt = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE connection_id = @Id",
            new { Id = id });

        if (datasetsUsingIt > 0)
        {
            return ApiResult<object>.Fail(
                ErrorCodes.ConnectionInUse,
                "This connection is still used by one or more datasets. Delete those datasets first.",
                new { dataset_count = datasetsUsingIt });
        }

        await conn.ExecuteAsync(
            "DELETE FROM db_connections WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        return ApiResult<object>.Ok(new { deleted = true, connection_id = id });
    }

    // ============================================================
    // Test
    // ============================================================

    public async Task<ApiResult<object>> TestAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ConnectionRow>(
            SelectSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        if (row is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var connector = ResolveConnector(row.Provider);
        if (connector is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.Internal, $"No connector registered for provider '{row.Provider}'.");
        }

        var config = DbConnectionConfig.FromJson(protector.Unprotect(row.EncryptedConfig));

        ConnectorTestResult result;
        try
        {
            result = await connector.TestAsync(config, ct);
        }
        catch (Exception ex)
        {
            // Never log the raw exception: a driver may echo the connection string / credentials.
            var scrubbed = config.Scrub(ex.Message);
            logger.LogWarning("TestAsync failed for connection {ConnectionId}: {Error}", id, scrubbed);
            result = new ConnectorTestResult(false, scrubbed, null);
        }

        // Scrub any secret a connector's own caught exception may have surfaced in its Error/Warning.
        result = result with
        {
            Error = result.Error is null ? null : config.Scrub(result.Error),
            Warning = result.Warning is null ? null : config.Scrub(result.Warning)
        };

        var testedAt = DateTime.UtcNow;
        var status = result.Success ? "success" : "failed";

        await conn.ExecuteAsync("""
            UPDATE db_connections
            SET last_test_status = @Status, last_test_at = @TestedAt, last_test_error = @Error, updated_at = NOW()
            WHERE id = @Id AND user_id = @UserId
            """, new
        {
            Status = status,
            TestedAt = testedAt,
            Error = result.Success ? null : result.Error,
            Id = id,
            UserId = userId
        });

        return ApiResult<object>.Ok(new
        {
            connection_id = id,
            success = result.Success,
            error = result.Error,
            warning = result.Warning,
            last_test_at = testedAt
        });
    }

    // ============================================================
    // Internal — used by ExternalSchemaService
    // ============================================================

    /// <summary>Decrypts and returns the config for a connection owned by <paramref name="userId"/>.
    /// Never exposed directly to API responses — internal use only.</summary>
    internal async Task<DbConnectionConfig?> GetConfigAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var encrypted = await conn.ExecuteScalarAsync<string?>(
            "SELECT encrypted_config FROM db_connections WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        return encrypted is null ? null : DbConnectionConfig.FromJson(protector.Unprotect(encrypted));
    }

    public async Task<ApiResult<object>> ListRemoteTablesAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ConnectionRow>(
            SelectSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = id, UserId = userId });

        if (row is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var connector = ResolveConnector(row.Provider);
        if (connector is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.Internal, $"No connector registered for provider '{row.Provider}'.");
        }

        var config = DbConnectionConfig.FromJson(protector.Unprotect(row.EncryptedConfig));

        try
        {
            var tables = await connector.ListTablesAsync(config, ct);
            var dto = tables.Select(t => new
            {
                queryable_name = t.QueryableName,
                source_label = t.SourceLabel,
                column_count = t.Columns.Count
            }).ToList();

            return ApiResult<object>.Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ListTablesAsync failed for connection {ConnectionId}", id);
            return ApiResult<object>.Fail(ErrorCodes.ExternalSchemaFetchFailed, "Could not list tables from the source database.");
        }
    }

    private IExternalDbConnector? ResolveConnector(string provider) =>
        connectors.FirstOrDefault(c => c.Provider == provider);

    private sealed record ConnectionRow(
        Guid Id, string Name, string Provider, string EncryptedConfig,
        string? LastTestStatus, DateTime? LastTestAt, string? LastTestError, DateTime CreatedAt);
}

/// <summary>
/// Pure helper that builds the client-facing representation of a connection. Deliberately excludes
/// password / service_account_json / raw host — only a 3-char-prefix + "***" mask is exposed.
/// Extracted as a standalone static class so it is unit-testable without a database.
/// </summary>
public static class DbConnectionMasking
{
    public static object Build(
        Guid id, string name, string provider, DbConnectionConfig config,
        string? lastTestStatus, DateTime? lastTestAt, string? lastTestError, DateTime createdAt)
    {
        var isBigQuery = provider == ExternalDbProviders.BigQuery;

        return new
        {
            id,
            name,
            provider,
            host_masked = MaskHost(isBigQuery ? config.ProjectId : config.Host),
            database = isBigQuery ? config.BigQueryDataset : config.Database,
            username = isBigQuery ? null : config.Username,
            last_test_status = lastTestStatus,
            last_test_at = lastTestAt,
            last_test_error = lastTestError,
            created_at = createdAt
        };
    }

    public static string MaskHost(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "***";
        }

        var prefix = value.Length <= 3 ? value : value[..3];
        return prefix + "***";
    }
}
