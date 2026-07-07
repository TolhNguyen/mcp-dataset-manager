using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services.Connectors;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Creates and refreshes "external_db" datasets: datasets whose rows are never copied locally —
/// only table/column schema + a couple of sample rows are stored, and queries run LIVE against the
/// source database (see <see cref="ExternalQueryService"/>, added in a later task).
/// </summary>
public class ExternalSchemaService(
    NpgsqlDataSource dataSource,
    DbConnectionService connections,
    IEnumerable<IExternalDbConnector> connectors,
    FileStorageService storage,
    IConfiguration configuration,
    ILogger<ExternalSchemaService> logger)
{
    // ============================================================
    // Create
    // ============================================================

    public async Task<ApiResult<object>> CreateExternalDatasetAsync(
        Guid userId, Guid connectionId, string name, string[] tables, bool includeSamples, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "Name is required.");
        }

        if (tables is null || tables.Length == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "At least one table must be selected.");
        }

        tables = DedupTables(tables);

        var maxTables = configuration.GetValue<int?>("ExternalQuery:MaxTablesPerDataset") ?? 50;
        if (tables.Length > maxTables)
        {
            return ApiResult<object>.Fail(
                ErrorCodes.TooManyTablesRequested,
                $"Cannot select more than {maxTables} tables in a single dataset.",
                new { max_tables = maxTables, requested = tables.Length });
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var connRow = await conn.QuerySingleOrDefaultAsync<ConnectionMetaRow>("""
            SELECT id AS Id, name AS Name, provider AS Provider
            FROM db_connections
            WHERE id = @Id AND user_id = @UserId
            """, new { Id = connectionId, UserId = userId });

        if (connRow is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var config = await connections.GetConfigAsync(userId, connectionId, ct);
        if (config is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var connector = ResolveConnector(connRow.Provider);
        if (connector is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.Internal, $"No connector registered for provider '{connRow.Provider}'.");
        }

        var datasetId = Guid.NewGuid();
        var trimmedName = name.Trim();

        // Advisory-lock the per-user dataset-count check (mirrors DatasetService.UploadAsync) so
        // concurrent creates cannot both observe "count < limit" and both insert.
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@UserKey::text, 0))",
            new { UserKey = userId }, tx);

        var maxDatasets = await conn.ExecuteScalarAsync<int>(
            "SELECT max_datasets FROM users WHERE id = @UserId", new { UserId = userId }, tx);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE user_id = @UserId", new { UserId = userId }, tx);

        if (count >= maxDatasets)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(
                ErrorCodes.DatasetLimitReached,
                $"Bạn đã đạt giới hạn {maxDatasets} dataset. Vui lòng xóa dataset cũ để tạo thêm.",
                new { max_datasets = maxDatasets, current_count = count });
        }

        List<ExternalTableInfo> remoteTables;
        try
        {
            remoteTables = await connector.ListTablesAsync(config, ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "ListTablesAsync failed for connection {ConnectionId}", connectionId);
            return ApiResult<object>.Fail(ErrorCodes.ExternalSchemaFetchFailed, "Could not read table schema from the source database.");
        }

        var byName = remoteTables.ToDictionary(t => t.QueryableName, StringComparer.Ordinal);
        var selected = new List<ExternalTableInfo>();
        foreach (var t in tables)
        {
            if (!byName.TryGetValue(t, out var info))
            {
                await tx.RollbackAsync(ct);
                return ApiResult<object>.Fail(ErrorCodes.TableNotFound, $"Table '{t}' was not found on the source database.");
            }

            selected.Add(info);
        }

        var externalTablesJson = JsonSerializer.Serialize(tables);
        var alias = await DatasetService.GenerateUniqueAliasAsync(conn, userId, trimmedName, tx);

        await conn.ExecuteAsync("""
            INSERT INTO datasets
                (id, user_id, name, original_file_name, file_type, stored_file_name, file_size_bytes,
                 manifest_file_name, status, source_kind, connection_id, external_tables, include_samples, table_count, alias)
            VALUES
                (@Id, @UserId, @Name, @OriginalFileName, @FileType, '', 0,
                 'manifest.md', 'ready', 'external_db', @ConnectionId, CAST(@ExternalTables AS jsonb), @IncludeSamples, @TableCount, @Alias)
            """, new
        {
            Id = datasetId,
            UserId = userId,
            Name = trimmedName,
            OriginalFileName = connRow.Name,
            FileType = connRow.Provider,
            ConnectionId = connectionId,
            ExternalTables = externalTablesJson,
            IncludeSamples = includeSamples,
            TableCount = selected.Count,
            Alias = alias
        }, tx);

        await tx.CommitAsync(ct);

        // From here on the dataset row is committed. If persisting tables/columns or writing the
        // manifest throws, the committed row would otherwise strand as a "ready" dataset that
        // consumes the user's max_datasets slot forever. Compensate by deleting it (cascades to
        // dataset_tables/columns) and its storage directory, then report failure instead of
        // leaving a half-created dataset behind.
        try
        {
            var manifestTables = await PersistTablesAsync(conn, connector, config, datasetId, selected, includeSamples, ct);

            storage.EnsureDatasetDirectories(userId, datasetId);
            var manifest = ExternalManifestBuilder.Build(trimmedName, connRow.Provider, manifestTables);
            await File.WriteAllTextAsync(storage.GetManifestPath(userId, datasetId), manifest, ct);

            await conn.ExecuteAsync(
                "UPDATE datasets SET schema_refreshed_at = NOW() WHERE id = @Id",
                new { Id = datasetId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist tables/manifest for new external dataset {DatasetId}; compensating.", datasetId);

            try
            {
                await conn.ExecuteAsync("DELETE FROM datasets WHERE id = @Id", new { Id = datasetId });
            }
            catch (Exception cleanupEx)
            {
                logger.LogError(cleanupEx, "Failed to compensate-delete stranded dataset {DatasetId} after persistence failure.", datasetId);
            }

            try
            {
                storage.DeleteDatasetDirectory(userId, datasetId);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to delete storage directory for dataset {DatasetId} during rollback.", datasetId);
            }

            return ApiResult<object>.Fail(ErrorCodes.Internal, "Failed to save dataset schema. Please try again.");
        }

        return ApiResult<object>.Ok(new
        {
            dataset_id = datasetId,
            name = trimmedName,
            status = "ready",
            table_count = selected.Count,
            actions = new
            {
                detail_url = $"/api/datasets/{datasetId}",
                query_url = $"/api/datasets/{datasetId}/query",
                refresh_schema_url = $"/api/datasets/{datasetId}/refresh-schema"
            }
        });
    }

    // ============================================================
    // Refresh
    // ============================================================

    public async Task<ApiResult<object>> RefreshSchemaAsync(Guid userId, Guid datasetId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var dataset = await conn.QuerySingleOrDefaultAsync<ExternalDatasetRow>("""
            SELECT id AS Id, name AS Name, connection_id AS ConnectionId,
                   external_tables::text AS ExternalTablesJson, include_samples AS IncludeSamples
            FROM datasets
            WHERE id = @Id AND user_id = @UserId AND source_kind = 'external_db'
            """, new { Id = datasetId, UserId = userId });

        if (dataset is null || dataset.ConnectionId is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "External dataset not found.");
        }

        var connectionId = dataset.ConnectionId.Value;

        var connRow = await conn.QuerySingleOrDefaultAsync<ConnectionMetaRow>("""
            SELECT id AS Id, name AS Name, provider AS Provider
            FROM db_connections
            WHERE id = @Id
            """, new { Id = connectionId });

        if (connRow is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var config = await connections.GetConfigAsync(userId, connectionId, ct);
        if (config is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ConnectionNotFound, "Connection not found.");
        }

        var connector = ResolveConnector(connRow.Provider);
        if (connector is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.Internal, $"No connector registered for provider '{connRow.Provider}'.");
        }

        var wantedTables = string.IsNullOrWhiteSpace(dataset.ExternalTablesJson)
            ? []
            : JsonSerializer.Deserialize<string[]>(dataset.ExternalTablesJson) ?? [];

        List<ExternalTableInfo> remoteTables;
        try
        {
            remoteTables = await connector.ListTablesAsync(config, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ListTablesAsync failed while refreshing dataset {DatasetId}", datasetId);
            return ApiResult<object>.Fail(ErrorCodes.ExternalSchemaFetchFailed, "Could not read table schema from the source database.");
        }

        var byName = remoteTables.ToDictionary(t => t.QueryableName, StringComparer.Ordinal);
        var selected = new List<ExternalTableInfo>();
        foreach (var t in wantedTables)
        {
            if (byName.TryGetValue(t, out var info))
            {
                selected.Add(info);
            }
            else
            {
                logger.LogWarning(
                    "Table {Table} no longer exists on connection {ConnectionId}; dropping from dataset {DatasetId}.",
                    t, connectionId, datasetId);
            }
        }

        if (selected.Count == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.NoTableFound, "None of the previously selected tables were found on the source database.");
        }

        // Wrapped in a transaction: if re-persisting the new tables fails partway through, the
        // delete rolls back too, so the dataset keeps its prior (still-valid) schema instead of
        // being left with no dataset_tables at all.
        await using var refreshTx = await conn.BeginTransactionAsync(ct);
        List<ExternalManifestTable> manifestTables;
        try
        {
            // Cascades to dataset_columns via ON DELETE CASCADE.
            await conn.ExecuteAsync("DELETE FROM dataset_tables WHERE dataset_id = @Id", new { Id = datasetId }, refreshTx);

            manifestTables = await PersistTablesAsync(conn, connector, config, datasetId, selected, dataset.IncludeSamples, ct, refreshTx);

            await refreshTx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await refreshTx.RollbackAsync(ct);
            logger.LogError(ex, "Failed to refresh dataset_tables for dataset {DatasetId}; rolled back to prior schema.", datasetId);
            return ApiResult<object>.Fail(ErrorCodes.Internal, "Failed to refresh dataset schema. Please try again.");
        }

        storage.EnsureDatasetDirectories(userId, datasetId);
        var manifest = ExternalManifestBuilder.Build(dataset.Name, connRow.Provider, manifestTables);
        await File.WriteAllTextAsync(storage.GetManifestPath(userId, datasetId), manifest, ct);

        await conn.ExecuteAsync("""
            UPDATE datasets SET table_count = @TableCount, schema_refreshed_at = NOW() WHERE id = @Id
            """, new { TableCount = selected.Count, Id = datasetId });

        return ApiResult<object>.Ok(new
        {
            dataset_id = datasetId,
            table_count = selected.Count,
            schema_refreshed_at = DateTime.UtcNow
        });
    }

    // ============================================================
    // Shared: persist dataset_tables + dataset_columns for a set of remote tables
    // ============================================================

    private async Task<List<ExternalManifestTable>> PersistTablesAsync(
        NpgsqlConnection conn,
        IExternalDbConnector connector,
        DbConnectionConfig config,
        Guid datasetId,
        List<ExternalTableInfo> tables,
        bool includeSamples,
        CancellationToken ct,
        NpgsqlTransaction? tx = null)
    {
        var manifestTables = new List<ExternalManifestTable>();
        var sourceType = "external_" + connector.Provider;

        foreach (var table in tables)
        {
            List<object?[]> sampleRows = [];
            if (includeSamples)
            {
                try
                {
                    sampleRows = await connector.GetSampleRowsAsync(config, table.QueryableName, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "GetSampleRowsAsync failed for table {Table}", table.QueryableName);
                    sampleRows = [];
                }
            }

            var tableId = Guid.NewGuid();
            await conn.ExecuteAsync("""
                INSERT INTO dataset_tables
                    (id, dataset_id, table_name, source_name, source_type, data_file_name, row_count, column_count, sample_rows)
                VALUES
                    (@Id, @DatasetId, @TableName, @SourceName, @SourceType, '', 0, @ColumnCount, CAST(@SampleRowsJson AS jsonb))
                """, new
            {
                Id = tableId,
                DatasetId = datasetId,
                TableName = table.QueryableName,
                SourceName = table.SourceLabel,
                SourceType = sourceType,
                ColumnCount = table.Columns.Count,
                SampleRowsJson = includeSamples ? JsonSerializer.Serialize(sampleRows) : null
            }, tx);

            var ordinal = 1;
            foreach (var col in table.Columns)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO dataset_columns
                        (id, dataset_table_id, ordinal_position, original_header, normalized_name, display_name, inferred_type)
                    VALUES
                        (@Id, @TableId, @Ordinal, @OriginalHeader, @NormalizedName, @DisplayName, @InferredType)
                    """, new
                {
                    Id = Guid.NewGuid(),
                    TableId = tableId,
                    Ordinal = ordinal,
                    OriginalHeader = col.Name,
                    NormalizedName = col.Name,
                    DisplayName = col.Name,
                    InferredType = col.DataType
                }, tx);
                ordinal++;
            }

            manifestTables.Add(new ExternalManifestTable(table.QueryableName, table.SourceLabel, table.Columns, sampleRows));
        }

        return manifestTables;
    }

    /// <summary>Removes duplicate table names (ordinal comparison) from a caller-supplied list,
    /// preserving first-seen order, so duplicates can't create double dataset_tables rows or
    /// artificially inflate the MaxTablesPerDataset count.</summary>
    public static string[] DedupTables(string[] tables) =>
        tables.Distinct(StringComparer.Ordinal).ToArray();

    private IExternalDbConnector? ResolveConnector(string provider) =>
        connectors.FirstOrDefault(c => c.Provider == provider);

    private sealed record ConnectionMetaRow(Guid Id, string Name, string Provider);

    private sealed record ExternalDatasetRow(
        Guid Id, string Name, Guid? ConnectionId, string? ExternalTablesJson, bool IncludeSamples);
}
