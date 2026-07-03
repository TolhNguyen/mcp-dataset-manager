using System.Data;
using System.Diagnostics;
using Dapper;
using DuckDB.NET.Data;
using ExcelDatasetManager.Api.Models;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public class DuckDbQueryService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    DatasetService datasetService,
    FileStorageService storage,
    QueryValidator validator,
    AiTokenBudgetService aiTokenBudget,
    ILogger<DuckDbQueryService> logger)
{
    public async Task<object> QueryAsync(Guid userId, Guid datasetId, QueryRequest request, CancellationToken ct)
    {
        var queryId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();

        var validation = validator.ValidateReadOnlySelect(request.Sql);
        if (!validation.Success)
        {
            await LogAsync(queryId, datasetId, userId, request.Sql, null, "failed",
                (int)sw.ElapsedMilliseconds, 0, null, null, validation.Code, validation.Message);
            return BuildErrorResponse(datasetId, queryId, request.Sql, null,
                validation.Code!, validation.Message!, sw.ElapsedMilliseconds);
        }

        var dataset = await datasetService.GetDatasetRecordAsync(userId, datasetId, ct);
        if (dataset is null)
        {
            return BuildErrorResponse(datasetId, queryId, request.Sql, null,
                ErrorCodes.DatasetNotFound, "Dataset not found.", sw.ElapsedMilliseconds);
        }

        if (!string.Equals(dataset.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return BuildErrorResponse(datasetId, queryId, request.Sql, null,
                ErrorCodes.DatasetNotReady,
                $"Dataset is in status '{dataset.Status}' and cannot be queried yet.",
                sw.ElapsedMilliseconds);
        }

        var defaultLimit = configuration.GetValue<int?>("Query:DefaultLimit") ?? 100;
        var hardCap = configuration.GetValue<int?>("Query:HardMaxRows") ?? 1000;
        var maxRows = Math.Clamp(request.Options?.MaxRows ?? defaultLimit, 1, hardCap);

        var executedSql = validator.ApplyLimit(validation.Sql!, maxRows);

        try
        {
            var tables = await datasetService.GetTablesAsync(datasetId, ct);
            var parquetDir = storage.GetParquetDirectory(userId, datasetId);

            using var conn = new DuckDBConnection("DataSource=:memory:");
            conn.Open();

            // Apply per-query safety limits.
            ExecNonQuery(conn, $"SET memory_limit='{configuration["Query:MemoryLimit"] ?? "1GB"}'");
            var timeoutSec = configuration.GetValue<int?>("Query:TimeoutSeconds") ?? 30;

            foreach (var t in tables)
            {
                var parquetPath = Path.Combine(parquetDir, t.DataFileName)
                    .Replace("\\", "/")
                    .Replace("'", "''");
                ExecNonQuery(conn, $"CREATE VIEW \"{t.TableName}\" AS SELECT * FROM read_parquet('{parquetPath}');");
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = executedSql;
            cmd.CommandTimeout = timeoutSec;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = new List<object>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new
                {
                    name = reader.GetName(i),
                    type = reader.GetDataTypeName(i)
                });
            }

            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : NormalizeValue(reader.GetValue(i));
                }
                rows.Add(row);
            }

            sw.Stop();
            var result = new
            {
                format = "compact_table",
                columns,
                rows,
                row_count = rows.Count,
                truncated = rows.Count >= maxRows,
                next_cursor = (string?)null
            };

            var confirmationScope = BuildConfirmationScope(userId, datasetId, executedSql);
            var budgetDecision = aiTokenBudget.Decide(result, request.Options, confirmationScope);
            var aiBudget = BuildAiBudget(budgetDecision);
            var summary = BuildSummary(columns, rows, budgetDecision.Status == "blocked");

            if (!budgetDecision.AllowRaw)
            {
                await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, budgetDecision.Status,
                    (int)sw.ElapsedMilliseconds, rows.Count, budgetDecision.EstimatedTokens, budgetDecision.Status, null, null);

                if (budgetDecision.Status == "summary")
                {
                    return new
                    {
                        success = true,
                        dataset_id = datasetId,
                        query_id = queryId,
                        status = "summary",
                        result = (object?)null,
                        summary,
                        execution = new
                        {
                            engine = "duckdb",
                            elapsed_ms = sw.ElapsedMilliseconds,
                            max_rows = maxRows
                        },
                        sql = new { submitted = request.Sql, executed = executedSql },
                        ai_budget = aiBudget,
                        suggestions = BuildSuggestions(),
                        error = (object?)null
                    };
                }

                if (request.Options?.AllowLargeResult == true && !string.IsNullOrWhiteSpace(request.Options.ConfirmationId))
                {
                    return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, sw.ElapsedMilliseconds,
                        summary, aiBudget, ErrorCodes.InvalidConfirmation,
                        "Confirmation is missing, expired, or does not match this query result.",
                        "requires_confirmation");
                }

                if (budgetDecision.Status == "blocked")
                {
                    return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, sw.ElapsedMilliseconds,
                        summary, aiBudget, ErrorCodes.TokenBudgetHardLimitExceeded,
                        $"Query result is estimated at {budgetDecision.EstimatedTokens} tokens, exceeding the hard maximum of {budgetDecision.HardMaxTokens} tokens. Raw result cannot be returned through AI chat.",
                        "blocked");
                }

                return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, sw.ElapsedMilliseconds,
                    summary, aiBudget, ErrorCodes.TokenBudgetConfirmationRequired,
                    $"Query result is estimated at {budgetDecision.EstimatedTokens} tokens, exceeding the safe AI reading budget of {budgetDecision.SafeMaxTokens} tokens. Confirm to return raw result or refine the query.",
                    "requires_confirmation");
            }

            await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, "completed",
                (int)sw.ElapsedMilliseconds, rows.Count, budgetDecision.EstimatedTokens, budgetDecision.Status, null, null);

            return new
            {
                success = true,
                dataset_id = datasetId,
                query_id = queryId,
                status = "completed",
                result,
                execution = new
                {
                    engine = "duckdb",
                    elapsed_ms = sw.ElapsedMilliseconds,
                    max_rows = maxRows
                },
                ai_budget = aiBudget,
                sql = new
                {
                    submitted = request.Sql,
                    executed = executedSql
                },
                warnings = Array.Empty<string>(),
                error = (object?)null
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var mapped = DuckDbErrorMapper.Map(ex.Message);
            await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, "failed",
                (int)sw.ElapsedMilliseconds, 0, null, null, mapped.Code, ex.Message);

            object? details = null;
            object? retryHint = null;

            if (mapped.Code == "COLUMN_NOT_FOUND" && mapped.MissingColumn is not null)
            {
                var (tableForSuggestion, suggestions) = await SuggestColumnsAsync(
                    datasetId, mapped.MissingTable, mapped.MissingColumn, ct);

                details = new
                {
                    missing_column = mapped.MissingColumn,
                    table = mapped.MissingTable ?? tableForSuggestion,
                    suggested_columns = suggestions
                };

                if (suggestions.Count > 0)
                {
                    retryHint = new
                    {
                        message = $"Try `{suggestions[0]}` instead of `{mapped.MissingColumn}`.",
                        suggested_column = suggestions[0]
                    };
                }
            }
            else if (mapped.Code == "TABLE_NOT_FOUND" && mapped.MissingTable is not null)
            {
                var tables = await datasetService.GetTablesAsync(datasetId, ct);
                details = new
                {
                    missing_table = mapped.MissingTable,
                    available_tables = tables.Select(t => t.TableName).ToArray()
                };
            }

            logger.LogWarning(ex, "Query failed for dataset {DatasetId}: {Message}", datasetId, ex.Message);

            return new
            {
                success = false,
                dataset_id = datasetId,
                query_id = queryId,
                status = "failed",
                result = (object?)null,
                execution = new { engine = "duckdb", elapsed_ms = sw.ElapsedMilliseconds },
                sql = new { submitted = request.Sql, executed = executedSql },
                warnings = Array.Empty<string>(),
                error = new
                {
                    code = mapped.Code,
                    message = mapped.Message,
                    details,
                    retryable = mapped.Code is "COLUMN_NOT_FOUND" or "TABLE_NOT_FOUND" or "INVALID_SQL"
                },
                retry_hint = retryHint
            };
        }
    }

    private async Task<(string? Table, List<string> Suggestions)> SuggestColumnsAsync(
        Guid datasetId, string? hintedTable, string missingColumn, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Get all columns in the dataset.
        var rows = (await conn.QueryAsync<(string TableName, string NormalizedName, string? SemanticType, string[] Aliases)>("""
            SELECT t.table_name AS TableName,
                   c.normalized_name AS NormalizedName,
                   c.semantic_type AS SemanticType,
                   c.aliases AS Aliases
            FROM dataset_columns c
            JOIN dataset_tables t ON t.id = c.dataset_table_id
            WHERE t.dataset_id = @DatasetId
            """, new { DatasetId = datasetId })).ToList();

        if (rows.Count == 0) return (null, new List<string>());

        var lower = missingColumn.ToLowerInvariant();

        // Score: substring match > alias match > prefix match > Levenshtein-ish length similarity.
        var scored = rows
            .Select(r => new
            {
                r.TableName,
                r.NormalizedName,
                Score = ScoreSuggestion(r.NormalizedName, r.Aliases ?? Array.Empty<string>(), lower)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        var primaryTable = hintedTable ?? scored.FirstOrDefault()?.TableName;
        return (primaryTable, scored.Select(s => s.NormalizedName).ToList());
    }

    private static int ScoreSuggestion(string normalized, IReadOnlyList<string> aliases, string lowerTarget)
    {
        if (string.Equals(normalized, lowerTarget, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (normalized.Contains(lowerTarget, StringComparison.OrdinalIgnoreCase)) return 500;
        if (lowerTarget.Contains(normalized, StringComparison.OrdinalIgnoreCase)) return 400;

        foreach (var alias in aliases)
        {
            if (alias.Contains(lowerTarget, StringComparison.OrdinalIgnoreCase) ||
                lowerTarget.Contains(alias, StringComparison.OrdinalIgnoreCase))
            {
                return 300;
            }
        }

        // Loose prefix match.
        if (normalized.StartsWith(lowerTarget[..Math.Min(3, lowerTarget.Length)], StringComparison.OrdinalIgnoreCase))
            return 100;

        return 0;
    }

    private static void ExecNonQuery(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        // Make decimals JSON-friendly without losing precision for typical cases.
        decimal d => d,
        DateTime dt => dt.ToString("O"),
        _ => value
    };

    private object BuildErrorResponse(Guid datasetId, Guid queryId, string submittedSql, string? executedSql,
        string code, string message, long elapsedMs)
    {
        return new
        {
            success = false,
            dataset_id = datasetId,
            query_id = queryId,
            status = "failed",
            result = (object?)null,
            execution = new { engine = "duckdb", elapsed_ms = elapsedMs },
            sql = new { submitted = submittedSql, executed = executedSql },
            warnings = Array.Empty<string>(),
            error = new
            {
                code,
                message,
                retryable = code is "COLUMN_NOT_FOUND" or "TABLE_NOT_FOUND" or "INVALID_SQL"
            }
        };
    }

    private object BuildTokenBudgetError(
        Guid datasetId,
        Guid queryId,
        string submittedSql,
        string? executedSql,
        long elapsedMs,
        object summary,
        object aiBudget,
        string code,
        string message,
        string status)
    {
        return new
        {
            success = false,
            dataset_id = datasetId,
            query_id = queryId,
            status,
            result = (object?)null,
            summary,
            execution = new { engine = "duckdb", elapsed_ms = elapsedMs },
            sql = new { submitted = submittedSql, executed = executedSql },
            ai_budget = aiBudget,
            error = new
            {
                code,
                message,
                retryable = true
            },
            suggestions = BuildSuggestions()
        };
    }

    private object BuildSummary(List<object> columns, List<object?[]> rows, bool omitPreviewRows)
    {
        var previewRows = omitPreviewRows
            ? Array.Empty<object?[]>()
            : rows.Take(aiTokenBudget.PreviewRows).ToArray();

        return new
        {
            format = "query_result_summary",
            total_rows_returned = rows.Count,
            total_columns = columns.Count,
            preview_rows = previewRows,
            preview_row_count = previewRows.Length,
            columns
        };
    }

    private static string[] BuildSuggestions() =>
    [
        "Use SELECT with only required columns instead of SELECT *.",
        "Add WHERE filters to reduce rows.",
        "Use GROUP BY to aggregate before returning data.",
        "Use LIMIT 100 for inspection.",
        "Use summary mode if you only need data shape and examples."
    ];

    private static object BuildAiBudget(AiBudgetDecision decision) => new
    {
        estimated_tokens = decision.EstimatedTokens,
        safe_max_tokens = decision.SafeMaxTokens,
        hard_max_tokens = decision.HardMaxTokens,
        requires_confirmation = decision.RequiresConfirmation,
        blocked = decision.Blocked,
        confirmation_id = decision.ConfirmationId
    };

    private static string BuildConfirmationScope(Guid userId, Guid datasetId, string sql) =>
        $"{userId:N}:{datasetId:N}:{sql}";

    private async Task LogAsync(
        Guid queryId, Guid datasetId, Guid userId,
        string submittedSql, string? executedSql, string status,
        int elapsedMs, int rowCount, int? estimatedTokens, string? aiBudgetStatus,
        string? errorCode, string? errorMessage)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync();
            await conn.ExecuteAsync("""
                INSERT INTO query_logs
                    (id, dataset_id, user_id, sql_submitted, sql_executed, status,
                     elapsed_ms, row_count, estimated_tokens, ai_budget_status, error_code, error_message)
                VALUES
                    (@Id, @DatasetId, @UserId, @SubmittedSql, @ExecutedSql, @Status,
                     @ElapsedMs, @RowCount, @EstimatedTokens, @AiBudgetStatus, @ErrorCode, @ErrorMessage)
                """, new
            {
                Id = queryId,
                DatasetId = datasetId,
                UserId = userId,
                SubmittedSql = submittedSql,
                ExecutedSql = executedSql,
                Status = status,
                ElapsedMs = elapsedMs,
                RowCount = rowCount,
                EstimatedTokens = estimatedTokens,
                AiBudgetStatus = aiBudgetStatus,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage is null ? null : (errorMessage.Length > 4000 ? errorMessage[..4000] : errorMessage)
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write query log for query {QueryId}", queryId);
        }
    }
}
