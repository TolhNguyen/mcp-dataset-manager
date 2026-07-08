using System.Diagnostics;
using Dapper;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services.Connectors;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Executes AI-submitted SQL against a customer's live external database (postgresql/mysql/mssql/
/// bigquery) for datasets with source_kind = 'external_db'. Mirrors <see cref="DuckDbQueryService"/>'s
/// response shape and AI token-budget / query_logs behavior exactly, but:
///   - validates SQL with <see cref="ExternalQueryGuard"/> (a stricter, provider-aware allowlist)
///     instead of <see cref="QueryValidator"/>, because a bypass here reaches a system we don't own;
///   - caps per-connection concurrency via <see cref="ConnectionConcurrencyLimiter"/> so a burst of
///     AI queries cannot exhaust a customer's production connection pool;
///   - runs the query through the provider-specific <see cref="IExternalDbConnector"/> instead of
///     DuckDB over local parquet files.
/// </summary>
public class ExternalQueryService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    DbConnectionService dbConnectionService,
    IEnumerable<IExternalDbConnector> connectors,
    ConnectionConcurrencyLimiter concurrencyLimiter,
    AiTokenBudgetService aiTokenBudget,
    ILogger<ExternalQueryService> logger)
{
    public async Task<object> QueryAsync(Guid userId, DatasetRecord dataset, QueryRequest request, CancellationToken ct)
    {
        var datasetId = dataset.Id;
        var queryId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();

        if (!string.Equals(dataset.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return BuildErrorResponse(datasetId, queryId, request.Sql, null, "unknown",
                ErrorCodes.DatasetNotReady,
                $"Dataset is in status '{dataset.Status}' and cannot be queried yet.",
                sw.ElapsedMilliseconds);
        }

        if (dataset.ConnectionId is null)
        {
            await LogAsync(queryId, datasetId, userId, request.Sql, null, "failed",
                (int)sw.ElapsedMilliseconds, 0, null, null, ErrorCodes.ConnectionNotFound,
                "Dataset has no associated connection.");
            return BuildErrorResponse(datasetId, queryId, request.Sql, null, "unknown",
                ErrorCodes.ConnectionNotFound, "This dataset has no associated database connection.",
                sw.ElapsedMilliseconds);
        }

        // The provider isn't stored on the dataset row itself — it lives on the (encrypted) connection
        // config. We need it before we can even validate the SQL (validation rules are provider-specific),
        // so the config is decrypted up front rather than immediately before ExecuteQueryAsync.
        var config = await dbConnectionService.GetConfigAsync(userId, dataset.ConnectionId.Value, ct);
        if (config is null)
        {
            await LogAsync(queryId, datasetId, userId, request.Sql, null, "failed",
                (int)sw.ElapsedMilliseconds, 0, null, null, ErrorCodes.ConnectionNotFound,
                "Connection not found.");
            return BuildErrorResponse(datasetId, queryId, request.Sql, null, "unknown",
                ErrorCodes.ConnectionNotFound, "The database connection for this dataset was not found.",
                sw.ElapsedMilliseconds);
        }

        var provider = config.Provider;

        var validation = ExternalQueryGuard.Validate(request.Sql, provider);
        if (!validation.Success)
        {
            await LogAsync(queryId, datasetId, userId, request.Sql, null, "failed",
                (int)sw.ElapsedMilliseconds, 0, null, null, validation.Code, validation.Message);
            return BuildErrorResponse(datasetId, queryId, request.Sql, null, provider,
                validation.Code!, validation.Message!, sw.ElapsedMilliseconds);
        }

        var defaultLimit = configuration.GetValue<int?>("Query:DefaultLimit") ?? 100;
        var hardCap = configuration.GetValue<int?>("Query:HardMaxRows") ?? 1000;
        var maxRows = Math.Clamp(request.Options?.MaxRows ?? defaultLimit, 1, hardCap);

        var executedSql = ExternalQueryGuard.ApplyRowCap(validation.Sql!, provider, maxRows);

        var maxConcurrentPerConnection = configuration.GetValue<int?>("ExternalQuery:MaxConcurrentPerConnection") ?? 3;
        var concurrencyWaitMs = configuration.GetValue<int?>("ExternalQuery:ConcurrencyWaitMs") ?? 200;

        IDisposable? slot = null;
        try
        {
            slot = await concurrencyLimiter.TryEnterAsync(
                dataset.ConnectionId.Value, maxConcurrentPerConnection, TimeSpan.FromMilliseconds(concurrencyWaitMs), ct);

            if (slot is null)
            {
                await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, "failed",
                    (int)sw.ElapsedMilliseconds, 0, null, null, ErrorCodes.TooManyConcurrentQueries,
                    "Too many concurrent queries for this connection.");
                return BuildTooManyConcurrentResponse(datasetId, queryId, request.Sql, executedSql, provider, sw.ElapsedMilliseconds);
            }

            var connector = connectors.FirstOrDefault(c => c.Provider == provider);
            if (connector is null)
            {
                await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, "failed",
                    (int)sw.ElapsedMilliseconds, 0, null, null, ErrorCodes.Internal,
                    $"No connector registered for provider '{provider}'.");
                return BuildErrorResponse(datasetId, queryId, request.Sql, executedSql, provider,
                    ErrorCodes.Internal, "No connector is registered for this provider.", sw.ElapsedMilliseconds);
            }

            var timeoutSeconds = configuration.GetValue<int?>("ExternalQuery:TimeoutSeconds") ?? 30;

            try
            {
                var queryResult = await connector.ExecuteQueryAsync(config, executedSql, maxRows, timeoutSeconds, ct);

                var columns = queryResult.Columns
                    .Select(c => (object)new { name = c.Name, type = c.Type })
                    .ToList();
                var rows = queryResult.Rows;

                sw.Stop();
                // Response/token-budget/query_logs cascade below is kept in sync with DuckDbQueryService — update both together.
                var result = new
                {
                    format = "compact_table",
                    columns,
                    rows,
                    row_count = rows.Count,
                    truncated = queryResult.Truncated,
                    next_cursor = (string?)null
                };

                var confirmationScope = BuildConfirmationScope(userId, datasetId, executedSql);
                var budgetDecision = aiTokenBudget.Decide(result, request.Options, confirmationScope);
                var aiBudget = BuildAiBudget(budgetDecision);
                var summary = BuildSummary(columns, rows, budgetDecision.Status == "blocked");

                // Dashboard widgets set BypassAiBudget: their result goes straight to the browser,
                // not an AI reader, so the AI token-reading budget is irrelevant to them. The row
                // cap (maxRows, above) and the command timeout remain the real safety bounds
                // regardless. Kept in sync with DuckDbQueryService — same skip, same reasoning.
                if (!budgetDecision.AllowRaw && request.Options?.BypassAiBudget != true)
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
                                engine = provider,
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
                        return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, provider, sw.ElapsedMilliseconds,
                            summary, aiBudget, ErrorCodes.InvalidConfirmation,
                            "Confirmation is missing, expired, or does not match this query result.",
                            "requires_confirmation");
                    }

                    if (budgetDecision.Status == "blocked")
                    {
                        return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, provider, sw.ElapsedMilliseconds,
                            summary, aiBudget, ErrorCodes.TokenBudgetHardLimitExceeded,
                            $"Query result is estimated at {budgetDecision.EstimatedTokens} tokens, exceeding the hard maximum of {budgetDecision.HardMaxTokens} tokens. Raw result cannot be returned through AI chat.",
                            "blocked");
                    }

                    return BuildTokenBudgetError(datasetId, queryId, request.Sql, executedSql, provider, sw.ElapsedMilliseconds,
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
                        engine = provider,
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
                var message = config.Scrub(ex.Message);

                await LogAsync(queryId, datasetId, userId, request.Sql, executedSql, "failed",
                    (int)sw.ElapsedMilliseconds, 0, null, null, ErrorCodes.ExternalQueryFailed, message);

                // Never log the raw exception (ex) here — ex.Message/stack can echo the connection string
                // or other secret material from the DB driver. Only the sanitized message is safe to log.
                logger.LogWarning("External query failed for dataset {DatasetId} ({Provider}): {Error}", datasetId, provider, message);
                var (knownTables, knownColumns) = await LoadKnownSchemaAsync(datasetId, ct);
                var details = ExternalErrorEnricher.Enrich(provider, message, knownTables, knownColumns);

                return new
                {
                    success = false,
                    dataset_id = datasetId,
                    query_id = queryId,
                    status = "failed",
                    result = (object?)null,
                    execution = new { engine = provider, elapsed_ms = sw.ElapsedMilliseconds },
                    sql = new { submitted = request.Sql, executed = executedSql },
                    warnings = Array.Empty<string>(),
                    error = new
                    {
                        code = ErrorCodes.ExternalQueryFailed,
                        message,
                        details,
                        assistant_instruction = "Fix the SQL using error.details (available_tables / did_you_mean / hint). Retry at most twice. If it still fails, report this error to the user verbatim and never fabricate data.",
                        retryable = details is not null
                    }
                };
            }
        }
        finally
        {
            slot?.Dispose();
        }
    }

    /// <summary>Strips any raw secret material (password / service account JSON) that might have leaked
    /// into a driver's exception message, e.g. via an echoed connection string.</summary>
    private object BuildErrorResponse(Guid datasetId, Guid queryId, string submittedSql, string? executedSql,
        string engine, string code, string message, long elapsedMs)
    {
        return new
        {
            success = false,
            dataset_id = datasetId,
            query_id = queryId,
            status = "failed",
            result = (object?)null,
            execution = new { engine, elapsed_ms = elapsedMs },
            sql = new { submitted = submittedSql, executed = executedSql },
            warnings = Array.Empty<string>(),
            error = new
            {
                code,
                message,
                assistant_instruction = AssistantInstructions.NeverFabricate,
                retryable = false
            }
        };
    }

    private object BuildTooManyConcurrentResponse(Guid datasetId, Guid queryId, string submittedSql, string? executedSql,
        string engine, long elapsedMs)
    {
        return new
        {
            success = false,
            dataset_id = datasetId,
            query_id = queryId,
            status = "failed",
            result = (object?)null,
            execution = new { engine, elapsed_ms = elapsedMs },
            sql = new { submitted = submittedSql, executed = executedSql },
            warnings = Array.Empty<string>(),
            error = new
            {
                code = ErrorCodes.TooManyConcurrentQueries,
                message = "Too many concurrent queries are running against this connection. Please retry shortly.",
                assistant_instruction = AssistantInstructions.NeverFabricate,
                retryable = true
            }
        };
    }

    private object BuildTokenBudgetError(
        Guid datasetId,
        Guid queryId,
        string submittedSql,
        string? executedSql,
        string engine,
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
            execution = new { engine, elapsed_ms = elapsedMs },
            sql = new { submitted = submittedSql, executed = executedSql },
            ai_budget = aiBudget,
            error = new
            {
                code,
                message,
                assistant_instruction = AssistantInstructions.NeverFabricate,
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

    private async Task<(List<string> Tables, List<string> Columns)> LoadKnownSchemaAsync(Guid datasetId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            var tables = (await conn.QueryAsync<string>(
                "SELECT table_name FROM dataset_tables WHERE dataset_id = @Id ORDER BY table_name",
                new { Id = datasetId })).ToList();
            var columns = (await conn.QueryAsync<string>("""
                SELECT DISTINCT c.normalized_name
                FROM dataset_columns c JOIN dataset_tables t ON t.id = c.dataset_table_id
                WHERE t.dataset_id = @Id
                """, new { Id = datasetId })).ToList();
            return (tables, columns);
        }
        catch
        {
            return (new List<string>(), new List<string>());
        }
    }

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
