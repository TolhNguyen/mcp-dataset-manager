using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Npgsql;

namespace ExcelDatasetManager.Api.BackgroundJobs;

/// <summary>
/// Consumes <see cref="ParsingJob"/>s and runs the full parse → parquet → manifest pipeline.
/// Each job runs in its own scope and updates the dataset row to either "ready" or "failed".
/// </summary>
public class ParsingHostedService(
    ParsingJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ParsingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Parsing background service started.");

        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Parsing service shutting down.");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Parsing job for dataset {DatasetId} crashed unexpectedly.", job.DatasetId);
            }
        }
    }

    private async Task ProcessJobAsync(ParsingJob job, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
        var storage = sp.GetRequiredService<FileStorageService>();
        var parser = sp.GetRequiredService<FileParserService>();
        var parquetWriter = sp.GetRequiredService<ParquetWriter>();
        var manifest = sp.GetRequiredService<ManifestGenerator>();

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Re-read the dataset row so we have authoritative metadata.
        var dataset = await conn.QuerySingleOrDefaultAsync<DatasetRecord>(
            DatasetService.SelectDatasetSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = job.DatasetId, UserId = job.UserId });

        if (dataset is null)
        {
            logger.LogWarning("Parsing job skipped — dataset {DatasetId} not found.", job.DatasetId);
            return;
        }

        if (!string.Equals(dataset.Status, "processing", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Parsing job skipped — dataset {DatasetId} is in status {Status}.", dataset.Id, dataset.Status);
            return;
        }

        var originalPath = storage.GetOriginalPath(job.UserId, job.DatasetId, dataset.StoredFileName);

        try
        {
            logger.LogInformation("Parsing dataset {DatasetId} ({File})", dataset.Id, dataset.OriginalFileName);

            var parsed = await parser.ParseAsync(job.UserId, job.DatasetId, originalPath, dataset.FileType, ct);

            // Convert each temp CSV into a parquet file.
            foreach (var table in parsed.Tables)
            {
                var parquetPath = storage.GetParquetPath(job.UserId, job.DatasetId, table.ParquetFileName);
                parquetWriter.WriteParquet(table.TempCsvPath, parquetPath);
            }

            // Persist tables + columns.
            await using (var tx = await conn.BeginTransactionAsync(ct))
            {
                foreach (var table in parsed.Tables)
                {
                    var tableId = Guid.NewGuid();
                    await conn.ExecuteAsync("""
                        INSERT INTO dataset_tables
                            (id, dataset_id, table_name, source_name, source_type, data_file_name, row_count, column_count)
                        VALUES
                            (@Id, @DatasetId, @TableName, @SourceName, @SourceType, @DataFileName, @RowCount, @ColumnCount)
                        """, new
                    {
                        Id = tableId,
                        DatasetId = job.DatasetId,
                        table.TableName,
                        table.SourceName,
                        table.SourceType,
                        DataFileName = table.ParquetFileName,
                        table.RowCount,
                        ColumnCount = table.Columns.Count
                    }, tx);

                    foreach (var col in table.Columns)
                    {
                        await conn.ExecuteAsync("""
                            INSERT INTO dataset_columns
                                (id, dataset_table_id, ordinal_position, original_header, normalized_name, display_name,
                                 aliases, inferred_type, semantic_type, null_count, distinct_count, sample_values)
                            VALUES
                                (@Id, @TableId, @Ordinal, @OriginalHeader, @NormalizedName, @DisplayName,
                                 @Aliases, @InferredType, @SemanticType, @NullCount, @DistinctCount, CAST(@SampleValuesJson AS jsonb))
                            """, new
                        {
                            Id = Guid.NewGuid(),
                            TableId = tableId,
                            col.Ordinal,
                            col.OriginalHeader,
                            col.NormalizedName,
                            col.DisplayName,
                            col.Aliases,
                            col.InferredType,
                            col.SemanticType,
                            col.NullCount,
                            col.DistinctCount,
                            SampleValuesJson = JsonSerializer.Serialize(col.SampleValues)
                        }, tx);
                    }
                }

                var totalRows = parsed.Tables.Sum(t => t.RowCount);
                await conn.ExecuteAsync("""
                    UPDATE datasets
                    SET status = 'ready',
                        table_count = @TableCount,
                        total_rows = @TotalRows,
                        processed_at = NOW(),
                        error_message = NULL
                    WHERE id = @DatasetId
                    """, new
                {
                    DatasetId = job.DatasetId,
                    TableCount = parsed.Tables.Count,
                    TotalRows = totalRows
                }, tx);

                await tx.CommitAsync(ct);
            }

            // Generate manifest using the post-update dataset record.
            var updatedDataset = dataset with
            {
                Status = "ready",
                TableCount = parsed.Tables.Count,
                TotalRows = parsed.Tables.Sum(t => t.RowCount),
                ProcessedAt = DateTime.UtcNow
            };

            var manifestPath = storage.GetManifestPath(job.UserId, job.DatasetId);
            await manifest.GenerateAsync(manifestPath, updatedDataset, parsed.Tables, parsed.Warnings, ct);

            // Clean up temp CSVs.
            storage.DeleteTempDirectory(job.UserId, job.DatasetId);

            logger.LogInformation("Dataset {DatasetId} parsed: {Tables} tables, {Rows} rows.",
                dataset.Id, parsed.Tables.Count, updatedDataset.TotalRows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Parsing failed for dataset {DatasetId}", dataset.Id);

            await conn.ExecuteAsync("""
                UPDATE datasets
                SET status = 'failed',
                    error_message = @Error,
                    processed_at = NOW()
                WHERE id = @DatasetId
                """, new { DatasetId = job.DatasetId, Error = Truncate(ex.Message, 4000) });

            storage.DeleteTempDirectory(job.UserId, job.DatasetId);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
