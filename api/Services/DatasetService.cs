using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.BackgroundJobs;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class DatasetService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    FileStorageService storage,
    ParsingJobQueue parsingQueue,
    ILogger<DatasetService> logger)
{
    public const string SelectDatasetSql = """
        SELECT id AS Id,
               user_id AS UserId,
               name AS Name,
               original_file_name AS OriginalFileName,
               file_type AS FileType,
               stored_file_name AS StoredFileName,
               file_size_bytes AS FileSizeBytes,
               manifest_file_name AS ManifestFileName,
               status AS Status,
               table_count AS TableCount,
               total_rows AS TotalRows,
               error_message AS ErrorMessage,
               created_at AS CreatedAt,
               processed_at AS ProcessedAt,
               source_kind AS SourceKind,
               connection_id AS ConnectionId,
               alias AS Alias,
               ai_can_write_knowledge AS AiCanWriteKnowledge
        FROM datasets
        """;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls", ".xlsm", ".csv", ".tsv"
    };

    // ============================================================
    // List
    // ============================================================

    public async Task<DatasetListPayload> ListAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var maxDatasets = await GetMaxDatasetsAsync(conn, userId);

        var used = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE user_id = @UserId",
            new { UserId = userId });

        var rows = (await conn.QueryAsync<DatasetListRow>("""
            SELECT id AS DatasetId,
                   name AS Name,
                   original_file_name AS OriginalFileName,
                   file_type AS FileType,
                   file_size_bytes AS FileSizeBytes,
                   table_count AS TableCount,
                   total_rows AS TotalRows,
                   status AS Status,
                   error_message AS ErrorMessage,
                   created_at AS CreatedAt,
                   source_kind AS SourceKind
            FROM datasets
            WHERE user_id = @UserId
            ORDER BY created_at DESC
            """, new { UserId = userId })).ToList();

        var items = rows.Select(r => new DatasetListItem(
            r.DatasetId, r.Name, r.OriginalFileName, r.FileType, r.FileSizeBytes,
            r.TableCount, r.TotalRows, r.Status, r.ErrorMessage, r.CreatedAt,
            BuildActions(r.DatasetId), r.SourceKind)).ToList();

        return new DatasetListPayload(
            new DatasetLimit(maxDatasets, used, Math.Max(0, maxDatasets - used), used < maxDatasets),
            items);
    }

    // ============================================================
    // Upload (returns immediately; parsing happens in background)
    // ============================================================

    public async Task<ApiResult<object>> UploadAsync(Guid userId, string? datasetName, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.InvalidFile, "A non-empty file is required.");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            return ApiResult<object>.Fail(ErrorCodes.InvalidFileType,
                "Supported file types: .xlsx, .xls, .xlsm, .csv, .tsv.");
        }

        var maxMb = configuration.GetValue<int?>("Upload:MaxFileSizeMb") ?? 100;
        if (file.Length > maxMb * 1024L * 1024L)
        {
            return ApiResult<object>.Fail(ErrorCodes.FileTooLarge,
                $"File exceeds the {maxMb}MB limit.",
                new { max_file_size_mb = maxMb, file_size_bytes = file.Length });
        }

        var fileType = ext.TrimStart('.');
        var storedFileName = "original_file" + ext;
        var name = string.IsNullOrWhiteSpace(datasetName)
            ? Path.GetFileNameWithoutExtension(file.FileName)
            : datasetName.Trim();

        // Insert the dataset row inside a transaction, taking an advisory lock per user so
        // concurrent uploads cannot both see "count < 10" and both insert.
        var datasetId = Guid.NewGuid();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // pg_advisory_xact_lock takes a bigint; derive a stable key from the user id.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@UserKey::text, 0))",
            new { UserKey = userId }, tx);

        var maxDatasets = await GetMaxDatasetsAsync(conn, userId, tx);

        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM datasets WHERE user_id = @UserId",
            new { UserId = userId }, tx);

        if (count >= maxDatasets)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(
                ErrorCodes.DatasetLimitReached,
                $"Bạn đã đạt giới hạn {maxDatasets} dataset. Vui lòng xóa dataset cũ để upload thêm.",
                new { max_datasets = maxDatasets, current_count = count });
        }

        var alias = await GenerateUniqueAliasAsync(conn, userId, name, tx);

        storage.EnsureDatasetDirectories(userId, datasetId);
        var originalPath = storage.GetOriginalPath(userId, datasetId, storedFileName);

        try
        {
            await using (var output = File.Create(originalPath))
            {
                await file.CopyToAsync(output, ct);
            }
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            storage.DeleteDatasetDirectory(userId, datasetId);
            logger.LogError(ex, "Failed to save uploaded file for dataset {DatasetId}", datasetId);
            return ApiResult<object>.Fail(ErrorCodes.StorageError, "Failed to save uploaded file.");
        }

        await conn.ExecuteAsync("""
            INSERT INTO datasets
                (id, user_id, name, original_file_name, file_type, stored_file_name,
                 file_size_bytes, manifest_file_name, status, alias)
            VALUES
                (@Id, @UserId, @Name, @OriginalFileName, @FileType, @StoredFileName,
                 @FileSizeBytes, 'manifest.md', 'processing', @Alias)
            """, new
        {
            Id = datasetId,
            UserId = userId,
            Name = name,
            OriginalFileName = file.FileName,
            FileType = fileType,
            StoredFileName = storedFileName,
            FileSizeBytes = file.Length,
            Alias = alias
        }, tx);

        await tx.CommitAsync(ct);

        // Hand off to the background worker.
        await parsingQueue.EnqueueAsync(new ParsingJob(userId, datasetId), ct);

        var payload = new
        {
            dataset_id = datasetId,
            name,
            original_file_name = file.FileName,
            file_type = fileType,
            file_size_bytes = file.Length,
            status = "processing",
            created_at = DateTimeOffset.UtcNow,
            actions = BuildActions(datasetId)
        };

        return ApiResult<object>.Ok(payload);
    }

    // ============================================================
    // Detail / Delete / Downloads
    // ============================================================

    public async Task<ApiResult<DatasetDetailPayload>> GetDetailAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var dataset = await conn.QuerySingleOrDefaultAsync<DatasetRecord>(
            SelectDatasetSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = datasetId, UserId = userId });

        if (dataset is null)
        {
            return ApiResult<DatasetDetailPayload>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        var tablesRows = (await conn.QueryAsync<TableRow>("""
            SELECT id AS Id, table_name AS TableName, source_name AS SourceName,
                   source_type AS SourceType, row_count AS RowCount, column_count AS ColumnCount
            FROM dataset_tables
            WHERE dataset_id = @DatasetId
            ORDER BY table_name
            """, new { DatasetId = datasetId })).ToList();

        var columnsRows = (await conn.QueryAsync<ColumnRow>("""
            SELECT c.dataset_table_id AS DatasetTableId,
                   c.ordinal_position AS OrdinalPosition,
                   c.original_header AS OriginalHeader,
                   c.normalized_name AS NormalizedName,
                   c.display_name AS DisplayName,
                   c.inferred_type AS InferredType,
                   c.semantic_type AS SemanticType,
                   c.null_count AS NullCount,
                   c.distinct_count AS DistinctCount,
                   c.sample_values::text AS SampleValuesJson
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId
            ORDER BY c.dataset_table_id, c.ordinal_position
            """, new { DatasetId = datasetId })).ToList();

        var tableDtos = tablesRows.Select(t => new
        {
            table_name = t.TableName,
            source_name = t.SourceName,
            source_type = t.SourceType,
            row_count = t.RowCount,
            column_count = t.ColumnCount,
            columns = columnsRows
                .Where(c => c.DatasetTableId == t.Id)
                .Select(c => new
                {
                    ordinal_position = c.OrdinalPosition,
                    original_header = c.OriginalHeader,
                    normalized_name = c.NormalizedName,
                    display_name = c.DisplayName,
                    inferred_type = c.InferredType,
                    semantic_type = c.SemanticType,
                    null_count = c.NullCount,
                    distinct_count = c.DistinctCount,
                    sample_values = ParseSampleValues(c.SampleValuesJson)
                })
                .ToList()
        }).Cast<object>().ToList();

        var detail = new DatasetDetailPayload(BuildDatasetDto(dataset), tableDtos);
        return ApiResult<DatasetDetailPayload>.Ok(detail);
    }

    public async Task<ApiResult<object>> DeleteAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM datasets WHERE id = @DatasetId AND user_id = @UserId",
            new { DatasetId = datasetId, UserId = userId });

        if (affected == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        try
        {
            storage.DeleteDatasetDirectory(userId, datasetId);
        }
        catch (Exception ex)
        {
            // The DB row is gone; storage cleanup is best-effort. Log so an admin can clean up later.
            logger.LogWarning(ex, "Failed to delete storage directory for dataset {DatasetId}", datasetId);
        }

        return ApiResult<object>.Ok(new { deleted = true, dataset_id = datasetId });
    }

    public async Task<DownloadFile?> GetOriginalDownloadAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        var dataset = await GetDatasetRecordAsync(userId, datasetId, ct);
        if (dataset is null) return null;
        var path = storage.GetOriginalPath(userId, datasetId, dataset.StoredFileName);
        return File.Exists(path)
            ? new DownloadFile(path, "application/octet-stream", dataset.OriginalFileName)
            : null;
    }

    public async Task<DownloadFile?> GetManifestDownloadAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        var dataset = await GetDatasetRecordAsync(userId, datasetId, ct);
        if (dataset is null || string.IsNullOrWhiteSpace(dataset.ManifestFileName)) return null;
        var path = storage.GetManifestPath(userId, datasetId, dataset.ManifestFileName);
        return File.Exists(path)
            ? new DownloadFile(path, "text/markdown; charset=utf-8", "manifest.md")
            : null;
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    public async Task<DatasetRecord?> GetDatasetRecordAsync(Guid userId, Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<DatasetRecord>(
            SelectDatasetSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = datasetId, UserId = userId });
    }

    public async Task<List<DatasetTableRecord>> GetTablesAsync(Guid datasetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var tables = await conn.QueryAsync<DatasetTableRecord>("""
            SELECT id AS Id, dataset_id AS DatasetId, table_name AS TableName,
                   source_name AS SourceName, source_type AS SourceType,
                   data_file_name AS DataFileName, row_count AS RowCount, column_count AS ColumnCount
            FROM dataset_tables
            WHERE dataset_id = @DatasetId
            ORDER BY table_name
            """, new { DatasetId = datasetId });
        return tables.ToList();
    }

    public async Task<List<string>> GetColumnNamesAsync(Guid datasetId, string tableName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<string>("""
            SELECT c.normalized_name
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId AND t.table_name = @TableName
            ORDER BY c.ordinal_position
            """, new { DatasetId = datasetId, TableName = tableName });
        return rows.ToList();
    }

    private static Task<int> GetMaxDatasetsAsync(NpgsqlConnection conn, Guid userId, NpgsqlTransaction? tx = null) =>
        conn.ExecuteScalarAsync<int>(
            "SELECT max_datasets FROM users WHERE id = @UserId",
            new { UserId = userId }, tx);

    /// <summary>
    /// Computes a unique-per-user alias slug for a new dataset. Call inside the same
    /// advisory-locked transaction as the INSERT so concurrent creates can't collide.
    /// </summary>
    public static async Task<string> GenerateUniqueAliasAsync(
        NpgsqlConnection conn, Guid userId, string name, NpgsqlTransaction? tx = null)
    {
        var existing = (await conn.QueryAsync<string>(
            "SELECT alias FROM datasets WHERE user_id = @UserId AND alias IS NOT NULL",
            new { UserId = userId }, tx)).ToHashSet(StringComparer.Ordinal);
        return AliasGenerator.MakeUnique(AliasGenerator.Slugify(name), existing);
    }

    private static object BuildActions(Guid datasetId) => new
    {
        download_original_url = $"/api/datasets/{datasetId}/download/original",
        download_manifest_url = $"/api/datasets/{datasetId}/download/manifest",
        query_url = $"/api/datasets/{datasetId}/query",
        detail_url = $"/api/datasets/{datasetId}",
        delete_url = $"/api/datasets/{datasetId}"
    };

    private static object BuildDatasetDto(DatasetRecord d) => new
    {
        dataset_id = d.Id,
        name = d.Name,
        original_file_name = d.OriginalFileName,
        file_type = d.FileType,
        file_size_bytes = d.FileSizeBytes,
        table_count = d.TableCount,
        total_rows = d.TotalRows,
        status = d.Status,
        error_message = d.ErrorMessage,
        created_at = d.CreatedAt,
        processed_at = d.ProcessedAt,
        actions = BuildActions(d.Id)
    };

    private static object? ParseSampleValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch { return Array.Empty<string>(); }
    }

    private sealed record DatasetListRow(
        Guid DatasetId, string Name, string OriginalFileName, string FileType, long FileSizeBytes,
        int TableCount, long TotalRows, string Status, string? ErrorMessage, DateTime CreatedAt,
        string SourceKind);

    private sealed record TableRow(Guid Id, string TableName, string SourceName, string SourceType, long RowCount, int ColumnCount);
    private sealed record ColumnRow(
        Guid DatasetTableId, int OrdinalPosition, string? OriginalHeader, string NormalizedName,
        string? DisplayName, string? InferredType, string? SemanticType,
        long NullCount, long DistinctCount, string? SampleValuesJson);
}

// ============================================================
// Return payloads
// ============================================================

public record DatasetListItem(
    Guid DatasetId, string Name, string OriginalFileName, string FileType, long FileSizeBytes,
    int TableCount, long TotalRows, string Status, string? ErrorMessage, DateTime CreatedAt,
    object Actions, string SourceKind);

public record DatasetLimit(int MaxDatasets, int Used, int Remaining, bool CanUpload);

public record DatasetListPayload(DatasetLimit Limit, List<DatasetListItem> Datasets);

public record DatasetDetailPayload(object Dataset, List<object> Tables);
