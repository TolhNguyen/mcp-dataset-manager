using System.Text.Json;
using Dapper;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services.Connectors;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// CRUD for dashboards / widgets, plus widget data execution. This is the security core of
/// Phase D: a widget's SQL is FROZEN at save time (the browser never sends SQL to run — it only
/// ever sends dashboard_id/widget_id) and is validated TWICE:
///   1. At save (Create/UpdateWidgetAsync): validated by the owning dataset's dialect
///      (QueryValidator for file datasets, ExternalQueryGuard for external_db datasets) and then
///      trial-executed with LIMIT 1 to catch bad columns/tables before it is ever persisted.
///   2. At every execution (GetWidgetDataAsync): RE-validated from the frozen DB row before
///      running, as defense against a tampered/corrupted database row — the validator that ran at
///      save time is not "trusted" forever, it is re-run on every read.
/// Row-count is always capped (Dashboard:MaxRowsPerWidget) and the result is cached for
/// refresh_interval_sec, keyed so an edit (which bumps updated_at) invalidates the cache — no
/// explicit cache-clear call is needed.
/// </summary>
public class DashboardService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    DatasetService datasetService,
    QueryValidator queryValidator,
    DuckDbQueryService duckDbQueryService,
    ExternalQueryService externalQueryService,
    IMemoryCache cache,
    ILogger<DashboardService> logger)
{
    private const string SelectDashboardSql = """
        SELECT id AS Id, user_id AS UserId, name AS Name, description AS Description,
               kind AS Kind, created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM dashboards
        """;

    // Widget rows scoped by dashboard only (used once the dashboard's ownership is already known).
    private const string SelectWidgetSql = """
        SELECT id AS Id, dashboard_id AS DashboardId, dataset_id AS DatasetId, title AS Title,
               sql AS Sql, chart_type AS ChartType, chart_config::text AS ChartConfigJson,
               refresh_interval_sec AS RefreshIntervalSec, position AS Position,
               source AS Source, created_by AS CreatedBy, archived_at AS ArchivedAt,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM dashboard_widgets
        """;

    // Widget rows joined to their dashboard, for direct widget-id lookups that must prove
    // ownership in the same query (Update/Archive/HardDelete/GetWidgetData).
    private const string SelectOwnedWidgetSql = """
        SELECT w.id AS Id, w.dashboard_id AS DashboardId, w.dataset_id AS DatasetId, w.title AS Title,
               w.sql AS Sql, w.chart_type AS ChartType, w.chart_config::text AS ChartConfigJson,
               w.refresh_interval_sec AS RefreshIntervalSec, w.position AS Position,
               w.source AS Source, w.created_by AS CreatedBy, w.archived_at AS ArchivedAt,
               w.created_at AS CreatedAt, w.updated_at AS UpdatedAt
        FROM dashboard_widgets w
        JOIN dashboards d ON d.id = w.dashboard_id
        """;

    // ============================================================
    // Dashboards
    // ============================================================

    public Task<ApiResult<object>> CreateDashboardAsync(
        Guid userId, string? name, string? description, string source, string createdBy, CancellationToken ct)
        => WrapEnsure(EnsureDashboardCoreAsync(userId, name, description, source, createdBy,
            allowExisting: false, DashboardGuard.KindGrid, ct));

    /// <summary>Returns the existing dashboard by (user, name) if one exists, else creates it
    /// (respecting the per-user cap). Used by the MCP convenience tool so an agent can address a
    /// dashboard by name without first checking whether it exists.</summary>
    public Task<ApiResult<object>> EnsureDashboardByNameAsync(
        Guid userId, string? name, string source, string createdBy, CancellationToken ct)
        => WrapEnsure(EnsureDashboardCoreAsync(userId, name, null, source, createdBy,
            allowExisting: true, DashboardGuard.KindGrid, ct));

    private static async Task<ApiResult<object>> WrapEnsure(Task<(Dashboard? Dashboard, ApiResult<object>? Error)> task)
    {
        var (dashboard, error) = await task;
        return error ?? ApiResult<object>.Ok(BuildDashboardDto(dashboard!));
    }

    /// <summary>Core ensure/create trả record thật (không phải DTO) để các caller nội bộ —
    /// SetPageByNameAsync (Task 3) — dùng tiếp dashboard.Id/Kind mà không phải đọc dynamic.</summary>
    private async Task<(Dashboard? Dashboard, ApiResult<object>? Error)> EnsureDashboardCoreAsync(
        Guid userId, string? name, string? description, string source, string createdBy,
        bool allowExisting, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, ApiResult<object>.Fail(ErrorCodes.ValidationError, "name is required."));
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > DashboardGuard.MaxTitleChars)
        {
            return (null, ApiResult<object>.Fail(
                ErrorCodes.ValidationError, $"name must be at most {DashboardGuard.MaxTitleChars} characters."));
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // pg_advisory_xact_lock takes a bigint; derive a stable key from the user id. Domain
        // seed 1 (distinct from DatasetService's per-user upload lock, which uses seed 0) so
        // dashboard creation and dataset upload don't needlessly serialize against each other.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@UserKey::text, 1))",
            new { UserKey = userId }, tx);

        if (allowExisting)
        {
            var existing = await conn.QuerySingleOrDefaultAsync<Dashboard>(
                SelectDashboardSql + " WHERE user_id = @UserId AND name = @Name",
                new { UserId = userId, Name = trimmedName }, tx);

            if (existing is not null)
            {
                if (!string.Equals(existing.Kind, kind, StringComparison.Ordinal))
                {
                    await tx.RollbackAsync(ct);
                    return (null, ApiResult<object>.Fail(
                        ErrorCodes.DashboardKindMismatch,
                        $"Dashboard '{existing.Name}' đã tồn tại với kind '{existing.Kind}' — không thể dùng làm dashboard '{kind}'. Chọn tên khác.",
                        new { dashboard_id = existing.Id, kind = existing.Kind }));
                }
                await tx.CommitAsync(ct);
                return (existing, null);
            }
        }

        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboards WHERE user_id = @UserId", new { UserId = userId }, tx);

        if (count >= DashboardGuard.MaxDashboardsPerUser)
        {
            await tx.RollbackAsync(ct);
            return (null, ApiResult<object>.Fail(
                ErrorCodes.DashboardLimitReached,
                $"You have reached the {DashboardGuard.MaxDashboardsPerUser}-dashboard limit.",
                new { max_dashboards = DashboardGuard.MaxDashboardsPerUser, current_count = count }));
        }

        var dashboardId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO dashboards (id, user_id, name, description, kind, created_by)
            VALUES (@Id, @UserId, @Name, @Description, @Kind, @CreatedBy)
            """, new
        {
            Id = dashboardId, UserId = userId, Name = trimmedName, Description = description,
            Kind = kind, CreatedBy = createdBy
        }, tx);

        var created = await conn.QuerySingleAsync<Dashboard>(
            SelectDashboardSql + " WHERE id = @Id", new { Id = dashboardId }, tx);

        await tx.CommitAsync(ct);

        return (created, null);
    }

    public async Task<ApiResult<object>> ListDashboardsAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<Dashboard>(
            SelectDashboardSql + " WHERE user_id = @UserId ORDER BY created_at DESC",
            new { UserId = userId })).ToList();

        return ApiResult<object>.Ok(new { dashboards = rows.Select(BuildDashboardDto).ToList() });
    }

    public async Task<ApiResult<object>> GetDashboardAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var dashboard = await conn.QuerySingleOrDefaultAsync<Dashboard>(
            SelectDashboardSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });

        if (dashboard is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var widgets = (await conn.QueryAsync<DashboardWidget>(
            SelectWidgetSql + " WHERE dashboard_id = @DashboardId AND archived_at IS NULL ORDER BY position, created_at",
            new { DashboardId = dashboardId })).ToList();

        return ApiResult<object>.Ok(new
        {
            dashboard = BuildDashboardDto(dashboard),
            widgets = widgets.Select(BuildWidgetDto).ToList()
        });
    }

    public async Task<ApiResult<object>> DeleteDashboardAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM dashboards WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });

        if (affected == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        return ApiResult<object>.Ok(new { deleted = true, dashboard_id = dashboardId });
    }

    // ============================================================
    // Widgets
    // ============================================================

    public async Task<ApiResult<object>> CreateWidgetAsync(
        Guid userId, Guid dashboardId, CreateWidgetRequest req, string source, string createdBy, CancellationToken ct)
    {
        await using (var checkConn = await dataSource.OpenConnectionAsync(ct))
        {
            var dashboardExists = await checkConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dashboards WHERE id = @Id AND user_id = @UserId",
                new { Id = dashboardId, UserId = userId });

            if (dashboardExists == 0)
            {
                return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
            }
        }

        if (req.DatasetId is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, "dataset_id is required.");
        }

        var dataset = await datasetService.GetDatasetRecordAsync(userId, req.DatasetId.Value, ct);
        if (dataset is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        var fieldError = DashboardGuard.ValidateCreate(req.Title, req.Sql, req.ChartType);
        if (fieldError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, fieldError);
        }

        // --- Save-time validation #1: dialect-aware read-only SQL check. ---
        var sqlValidation = ValidateByDialect(dataset, req.Sql!);
        if (!sqlValidation.Success)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, sqlValidation.Message!);
        }

        var frozenSql = sqlValidation.Sql!;

        // --- Save-time validation #2: trial execution (LIMIT 1) to catch bad columns/tables. ---
        var trialRequest = new QueryRequest("sql", frozenSql, new QueryOptions(
            MaxRows: 1, ReturnFormat: null, IncludeSql: null, IncludeProfile: null,
            MaxTokens: null, AllowLargeResult: null, ConfirmationId: null, ResponseMode: null));

        var trialOutcome = QueryOutcomeReader.Extract(await ExecuteQueryAsync(userId, dataset, trialRequest, ct));
        if (!trialOutcome.Completed)
        {
            return ApiResult<object>.Fail(trialOutcome.ErrorCode!, trialOutcome.ErrorMessage!);
        }

        var refresh = DashboardGuard.ClampRefresh(req.RefreshIntervalSec);
        var chartConfigJson = req.ChartConfig?.GetRawText();
        var widgetId = Guid.NewGuid();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Domain seed 2 (distinct from KnowledgeService's per-dataset lock, which also uses
        // seed 0 but a different key value) — keyed by dashboard id so concurrent widget
        // creates on the SAME dashboard serialize, but different dashboards never contend.
        await conn.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@DashboardKey::text, 2))",
            new { DashboardKey = dashboardId }, tx);

        var widgetCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboard_widgets WHERE dashboard_id = @DashboardId AND archived_at IS NULL",
            new { DashboardId = dashboardId }, tx);

        if (widgetCount >= DashboardGuard.MaxWidgetsPerDashboard)
        {
            await tx.RollbackAsync(ct);
            return ApiResult<object>.Fail(
                ErrorCodes.WidgetLimitReached,
                $"This dashboard has reached the {DashboardGuard.MaxWidgetsPerDashboard}-widget limit.",
                new { max_widgets = DashboardGuard.MaxWidgetsPerDashboard, current_count = widgetCount });
        }

        var maxPosition = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(position) FROM dashboard_widgets WHERE dashboard_id = @DashboardId",
            new { DashboardId = dashboardId }, tx);
        var position = (maxPosition ?? -1) + 1;

        await conn.ExecuteAsync("""
            INSERT INTO dashboard_widgets
                (id, dashboard_id, dataset_id, title, sql, chart_type, chart_config,
                 refresh_interval_sec, position, source, created_by)
            VALUES
                (@Id, @DashboardId, @DatasetId, @Title, @Sql, @ChartType, CAST(@ChartConfig AS jsonb),
                 @RefreshIntervalSec, @Position, @Source, @CreatedBy)
            """, new
        {
            Id = widgetId,
            DashboardId = dashboardId,
            DatasetId = dataset.Id,
            Title = req.Title!.Trim(),
            Sql = frozenSql,
            ChartType = req.ChartType,
            ChartConfig = chartConfigJson,
            RefreshIntervalSec = refresh,
            Position = position,
            Source = source,
            CreatedBy = createdBy
        }, tx);

        var created = await conn.QuerySingleAsync<DashboardWidget>(
            SelectWidgetSql + " WHERE id = @Id", new { Id = widgetId }, tx);

        await tx.CommitAsync(ct);

        return ApiResult<object>.Ok(BuildWidgetDto(created));
    }

    public async Task<ApiResult<object>> UpdateWidgetAsync(
        Guid userId, Guid dashboardId, Guid widgetId, UpdateWidgetRequest req, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var existing = await GetOwnedWidgetAsync(conn, userId, dashboardId, widgetId);
        if (existing is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.WidgetNotFound, "Widget not found.");
        }

        var newTitle = string.IsNullOrWhiteSpace(req.Title) ? existing.Title : req.Title.Trim();
        var newSql = string.IsNullOrWhiteSpace(req.Sql) ? existing.Sql : req.Sql;
        var newChartType = req.ChartType ?? existing.ChartType;
        var newChartConfigJson = req.ChartConfig.HasValue ? req.ChartConfig.Value.GetRawText() : existing.ChartConfigJson;
        var newRefresh = req.RefreshIntervalSec.HasValue ? DashboardGuard.ClampRefresh(req.RefreshIntervalSec) : existing.RefreshIntervalSec;
        var newPosition = req.Position ?? existing.Position;

        // Field-level validation always re-runs on the merged values (cheap, catches e.g. an
        // invalid chart_type even when the SQL itself did not change).
        var fieldError = DashboardGuard.ValidateCreate(newTitle, newSql, newChartType);
        if (fieldError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, fieldError);
        }

        var sqlChanged = !string.Equals(newSql, existing.Sql, StringComparison.Ordinal);
        var frozenSql = existing.Sql;

        if (sqlChanged)
        {
            // The dataset itself never changes on update (UpdateWidgetRequest carries no
            // dataset_id) but we re-fetch it for freshness: it may have been deleted since the
            // widget was created, and its dialect drives which validator/executor to use.
            var dataset = await datasetService.GetDatasetRecordAsync(userId, existing.DatasetId, ct);
            if (dataset is null)
            {
                return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
            }

            // --- Save-time validation #1: dialect-aware read-only SQL check. ---
            var sqlValidation = ValidateByDialect(dataset, newSql);
            if (!sqlValidation.Success)
            {
                return ApiResult<object>.Fail(ErrorCodes.ValidationError, sqlValidation.Message!);
            }

            frozenSql = sqlValidation.Sql!;

            // --- Save-time validation #2: trial execution (LIMIT 1). ---
            var trialRequest = new QueryRequest("sql", frozenSql, new QueryOptions(
                MaxRows: 1, ReturnFormat: null, IncludeSql: null, IncludeProfile: null,
                MaxTokens: null, AllowLargeResult: null, ConfirmationId: null, ResponseMode: null));

            var trialOutcome = QueryOutcomeReader.Extract(await ExecuteQueryAsync(userId, dataset, trialRequest, ct));
            if (!trialOutcome.Completed)
            {
                return ApiResult<object>.Fail(trialOutcome.ErrorCode!, trialOutcome.ErrorMessage!);
            }
        }

        // updated_at advances via NOW() below; GetWidgetDataAsync's cache key embeds updated_at
        // ticks, so this update naturally busts any cached widget data — no explicit cache clear.
        await conn.ExecuteAsync("""
            UPDATE dashboard_widgets
            SET title = @Title, sql = @Sql, chart_type = @ChartType, chart_config = CAST(@ChartConfig AS jsonb),
                refresh_interval_sec = @RefreshIntervalSec, position = @Position, updated_at = NOW()
            WHERE id = @Id
            """, new
        {
            Title = newTitle,
            Sql = frozenSql,
            ChartType = newChartType,
            ChartConfig = newChartConfigJson,
            RefreshIntervalSec = newRefresh,
            Position = newPosition,
            Id = widgetId
        });

        var updated = await GetOwnedWidgetAsync(conn, userId, dashboardId, widgetId);
        return ApiResult<object>.Ok(BuildWidgetDto(updated!));
    }

    public async Task<ApiResult<object>> ArchiveWidgetAsync(Guid userId, Guid dashboardId, Guid widgetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var existing = await GetOwnedWidgetAsync(conn, userId, dashboardId, widgetId);
        if (existing is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.WidgetNotFound, "Widget not found.");
        }

        if (existing.ArchivedAt is null)
        {
            await conn.ExecuteAsync(
                "UPDATE dashboard_widgets SET archived_at = NOW(), updated_at = NOW() WHERE id = @Id",
                new { Id = widgetId });
        }

        var archived = await GetOwnedWidgetAsync(conn, userId, dashboardId, widgetId);
        return ApiResult<object>.Ok(BuildWidgetDto(archived!));
    }

    /// <summary>JWT-only path (no dataset-scoped API key should be able to reach this).</summary>
    public async Task<ApiResult<object>> HardDeleteWidgetAsync(Guid userId, Guid dashboardId, Guid widgetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var affected = await conn.ExecuteAsync("""
            DELETE FROM dashboard_widgets w
            USING dashboards d
            WHERE w.dashboard_id = d.id
              AND d.user_id = @UserId
              AND w.dashboard_id = @DashboardId
              AND w.id = @WidgetId
            """, new { UserId = userId, DashboardId = dashboardId, WidgetId = widgetId });

        if (affected == 0)
        {
            return ApiResult<object>.Fail(ErrorCodes.WidgetNotFound, "Widget not found.");
        }

        return ApiResult<object>.Ok(new { deleted = true, widget_id = widgetId });
    }

    // ============================================================
    // Custom page (kind='custom'): trang HTML do AI dựng, serve qua route CSP-sandbox
    // ============================================================

    /// <summary>
    /// Upsert trang HTML cho dashboard kind='custom', addressing theo tên (convention giống
    /// EnsureDashboardByNameAsync để agent không cần lookup trước). Dashboard cùng tên nhưng
    /// kind='grid' bị từ chối (DASHBOARD_KIND_MISMATCH) — không bao giờ tự đổi kind.
    /// KHÔNG sanitize html: ranh giới an ninh là CSP sandbox lúc serve, không phải lúc lưu.
    /// </summary>
    public async Task<ApiResult<object>> SetPageByNameAsync(
        Guid userId, string? dashboardName, string? html, string source, string createdBy, CancellationToken ct)
    {
        var htmlError = DashboardPageGuard.ValidateHtml(html);
        if (htmlError is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, htmlError);
        }

        var (dashboard, error) = await EnsureDashboardCoreAsync(
            userId, dashboardName, null, source, createdBy, allowExisting: true, DashboardGuard.KindCustom, ct);
        if (error is not null) return error;

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO dashboard_pages (dashboard_id, html, created_by)
            VALUES (@DashboardId, @Html, @CreatedBy)
            ON CONFLICT (dashboard_id) DO UPDATE
            SET html = EXCLUDED.html, created_by = EXCLUDED.created_by, updated_at = NOW()
            """, new { DashboardId = dashboard!.Id, Html = html, CreatedBy = createdBy });

        var endpoints = (await conn.QueryAsync<(Guid WidgetId, string Title)>("""
            SELECT id AS WidgetId, title AS Title
            FROM dashboard_widgets
            WHERE dashboard_id = @DashboardId AND archived_at IS NULL
            ORDER BY position, created_at
            """, new { DashboardId = dashboard.Id })).ToList();

        var updatedAt = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT updated_at FROM dashboard_pages WHERE dashboard_id = @Id", new { Id = dashboard.Id });

        // Oauth:PublicUrl là issuer công khai (production: https) — dùng làm base cho view link
        // trả về agent, vì agent/user mở link ngoài origin của API nội bộ.
        var baseUrl = (configuration["Oauth:PublicUrl"] ?? "").TrimEnd('/');

        return ApiResult<object>.Ok(new
        {
            dashboard_id = dashboard.Id,
            name = dashboard.Name,
            kind = dashboard.Kind,
            view_url = $"{baseUrl}/dashboards.html?id={dashboard.Id}",
            endpoints = endpoints.Select(e => new { widget_id = e.WidgetId, title = e.Title }).ToList(),
            html_bytes = System.Text.Encoding.UTF8.GetByteCount(html!),
            updated_at = updatedAt
        });
    }

    /// <summary>
    /// HTML trang custom, scoped theo ownership (join dashboards.user_id). Trả null khi không có
    /// page hoặc dashboard không thuộc user — caller trả 404, không phân biệt 2 trường hợp
    /// (tránh existence oracle). Share route gọi với share.UserId (chủ share) nên cùng path này.
    /// </summary>
    public async Task<string?> GetPageHtmlAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>("""
            SELECT p.html
            FROM dashboard_pages p
            JOIN dashboards d ON d.id = p.dashboard_id
            WHERE d.id = @DashboardId AND d.user_id = @UserId
            """, new { DashboardId = dashboardId, UserId = userId });
    }

    // ============================================================
    // Widget data execution (frozen SQL re-validated + row-capped + cached)
    // ============================================================

    public async Task<ApiResult<object>> GetWidgetDataAsync(Guid userId, Guid dashboardId, Guid widgetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var widget = await GetOwnedWidgetAsync(conn, userId, dashboardId, widgetId);
        if (widget is null || widget.ArchivedAt is not null)
        {
            return ApiResult<object>.Fail(ErrorCodes.WidgetNotFound, "Widget not found.");
        }

        // Cache key embeds updated_at ticks (bumped by every UpdateWidgetAsync write) so an edit
        // always busts the cache, and embeds the widget id so no cross-widget/cross-user bleed is
        // possible (a widget row is only ever reachable once ownership has already been proven by
        // GetOwnedWidgetAsync's dashboard-user join, above).
        var cacheKey = $"widget:{widgetId}:{widget.UpdatedAt.Ticks}";
        if (cache.TryGetValue(cacheKey, out object? cached) && cached is not null)
        {
            return ApiResult<object>.Ok(cached);
        }

        var dataset = await datasetService.GetDatasetRecordAsync(userId, widget.DatasetId, ct);
        if (dataset is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DatasetNotFound, "Dataset not found.");
        }

        // Re-validate the FROZEN sql from the DB row every time it is executed — defense against
        // a tampered/corrupted database row. This does not trust that save-time validation (above)
        // ran, or ran correctly, forever.
        var revalidation = ValidateByDialect(dataset, widget.Sql);
        if (!revalidation.Success)
        {
            logger.LogWarning(
                "Widget {WidgetId} frozen SQL failed re-validation at execution time: {Message}",
                widgetId, revalidation.Message);
            return ApiResult<object>.Fail(ErrorCodes.ValidationError, revalidation.Message!);
        }

        var maxRows = configuration.GetValue<int?>("Dashboard:MaxRowsPerWidget") ?? 1000;
        // BypassAiBudget: widget data goes straight to the browser, not an AI reader, so the AI
        // token-reading budget must not gate it — only the row cap (maxRows) and command timeout
        // (enforced inside the query service) apply.
        var request = new QueryRequest("sql", revalidation.Sql!, new QueryOptions(
            MaxRows: maxRows, ReturnFormat: null, IncludeSql: null, IncludeProfile: null,
            MaxTokens: null, AllowLargeResult: null, ConfirmationId: null, ResponseMode: null,
            BypassAiBudget: true));

        var raw = await ExecuteQueryAsync(userId, dataset, request, ct);
        var outcome = QueryOutcomeReader.Extract(raw);

        if (!outcome.Completed)
        {
            // Don't cache errors — a transient failure (e.g. a momentarily unreachable external
            // DB) should be retried on the widget's next poll, not stuck for refresh_interval_sec.
            return ApiResult<object>.Fail(outcome.ErrorCode!, outcome.ErrorMessage!);
        }

        // Only the compact_table `result` object is cached/returned — never the ai_budget
        // envelope (confirmation ids, safe/hard token limits, etc.). Widget data goes straight to
        // the browser; it is not read by an AI, so none of that machinery is relevant to it.
        cache.Set(cacheKey, outcome.Result!, TimeSpan.FromSeconds(widget.RefreshIntervalSec));
        return ApiResult<object>.Ok(outcome.Result!);
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    /// <summary>
    /// Resolves the dataset a widget reads from, scoped by dashboard ownership. Used by the
    /// endpoint-level schema-token gate on widget SQL updates (UpdateWidgetRequest carries no
    /// dataset_id of its own). Returns null when the widget/dashboard isn't the caller's.
    /// </summary>
    public async Task<Guid?> GetWidgetDatasetIdAsync(Guid userId, Guid dashboardId, Guid widgetId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>("""
            SELECT w.dataset_id
            FROM dashboard_widgets w
            JOIN dashboards d ON d.id = w.dashboard_id
            WHERE d.user_id = @UserId AND w.dashboard_id = @DashboardId AND w.id = @WidgetId
            """, new { UserId = userId, DashboardId = dashboardId, WidgetId = widgetId });
    }

    // Dapper projection row for GetShareViewAsync — deliberately excludes Sql (unlike
    // SelectWidgetSql/SelectOwnedWidgetSql above) since this is the anonymous-viewer payload.
    private sealed record ShareWidgetRow(Guid WidgetId, string Title, string ChartType, string? ChartConfigJson, int Position);

    /// <summary>
    /// Dashboard payload for anonymous share viewers: widget metadata WITHOUT the SQL —
    /// viewers must not learn schema/business logic from queries.
    /// </summary>
    public async Task<ApiResult<object>> GetShareViewAsync(Guid userId, Guid dashboardId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var dashboard = await conn.QuerySingleOrDefaultAsync<Dashboard>(
            SelectDashboardSql + " WHERE id = @Id AND user_id = @UserId",
            new { Id = dashboardId, UserId = userId });
        if (dashboard is null)
        {
            return ApiResult<object>.Fail(ErrorCodes.DashboardNotFound, "Dashboard not found.");
        }

        var hasPage = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dashboard_pages WHERE dashboard_id = @Id",
            new { Id = dashboardId }) > 0;

        var widgets = (await conn.QueryAsync<ShareWidgetRow>("""
            SELECT w.id AS WidgetId, w.title AS Title, w.chart_type AS ChartType,
                   w.chart_config::text AS ChartConfigJson, w.position AS Position
            FROM dashboard_widgets w
            WHERE w.dashboard_id = @DashboardId AND w.archived_at IS NULL
            ORDER BY w.position
            """, new { DashboardId = dashboardId })).ToList();

        return ApiResult<object>.Ok(new
        {
            dashboard_name = dashboard.Name,
            kind = dashboard.Kind,
            has_page = hasPage,
            widgets = widgets.Select(w => new
            {
                widget_id = w.WidgetId,
                title = w.Title,
                chart_type = w.ChartType,
                chart_config = ParseChartConfig(w.ChartConfigJson),
                position = w.Position
            })
        });
    }

    private static Task<DashboardWidget?> GetOwnedWidgetAsync(
        NpgsqlConnection conn, Guid userId, Guid dashboardId, Guid widgetId) =>
        conn.QuerySingleOrDefaultAsync<DashboardWidget>(
            SelectOwnedWidgetSql + " WHERE d.user_id = @UserId AND w.dashboard_id = @DashboardId AND w.id = @WidgetId",
            new { UserId = userId, DashboardId = dashboardId, WidgetId = widgetId });

    /// <summary>Picks the validator that matches how the widget's dataset is queryable: file
    /// datasets (DuckDB over local parquet) use QueryValidator; external_db datasets (a live
    /// customer database we don't own) use the stricter, provider-aware ExternalQueryGuard, with
    /// the provider read from dataset.FileType (which mirrors the connection's provider for
    /// external datasets — see ExternalSchemaService).</summary>
    private QueryValidationResult ValidateByDialect(DatasetRecord dataset, string sql) =>
        string.Equals(dataset.SourceKind, "external_db", StringComparison.OrdinalIgnoreCase)
            ? ExternalQueryGuard.Validate(sql, dataset.FileType)
            : queryValidator.ValidateReadOnlySelect(sql);

    private Task<object> ExecuteQueryAsync(Guid userId, DatasetRecord dataset, QueryRequest request, CancellationToken ct) =>
        string.Equals(dataset.SourceKind, "external_db", StringComparison.OrdinalIgnoreCase)
            ? externalQueryService.QueryAsync(userId, dataset, request, ct)
            : duckDbQueryService.QueryAsync(userId, dataset.Id, request, ct);

    private static object BuildDashboardDto(Dashboard d) => new
    {
        dashboard_id = d.Id,
        name = d.Name,
        description = d.Description,
        kind = d.Kind,
        created_by = d.CreatedBy,
        created_at = d.CreatedAt,
        updated_at = d.UpdatedAt
    };

    private static object BuildWidgetDto(DashboardWidget w) => new
    {
        widget_id = w.Id,
        dashboard_id = w.DashboardId,
        dataset_id = w.DatasetId,
        title = w.Title,
        sql = w.Sql,
        chart_type = w.ChartType,
        chart_config = ParseChartConfig(w.ChartConfigJson),
        refresh_interval_sec = w.RefreshIntervalSec,
        position = w.Position,
        source = w.Source,
        created_by = w.CreatedBy,
        archived_at = w.ArchivedAt,
        created_at = w.CreatedAt,
        updated_at = w.UpdatedAt
    };

    private static object? ParseChartConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }
}

/// <summary>
/// Pure helper that reads the common {status, result, error} shape shared by
/// <see cref="DuckDbQueryService.QueryAsync"/> and <see cref="ExternalQueryService.QueryAsync"/>'s
/// single-dataset responses. Both methods return `object` (an anonymous type per call site, since
/// the shapes differ slightly across success/summary/error branches), so `dynamic` is used to read
/// the handful of members every branch has in common — safe here because DashboardService lives in
/// the same assembly as those anonymous types (anonymous types are assembly-internal), and because
/// this file was written by reading both methods' return statements directly rather than guessing.
/// Extracted to a standalone static class (mirroring DbConnectionMasking) so it is unit-testable
/// without a database, by constructing small anonymous objects that mimic the two shapes.
/// </summary>
public static class QueryOutcomeReader
{
    public static QueryOutcome Extract(object raw)
    {
        string? status;
        object? resultIfCompleted = null;
        string? code = null;
        string? message = null;

        try
        {
            dynamic response = raw;
            status = response.status;

            if (status == "completed")
            {
                resultIfCompleted = response.result;
            }
            else
            {
                try
                {
                    var err = response.error;
                    if (err is not null)
                    {
                        code = err.code;
                        message = err.message;
                    }
                }
                catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // The response shape had no "error" member at all — shouldn't happen given
                    // the two query services' shared contract, but fail safe rather than
                    // throwing into the caller.
                }
            }
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            // Defense-in-depth: if a future refactor ever changes the query services' response
            // shape (or moves it to a different assembly, breaking dynamic's accessibility check
            // on the anonymous type — see QueryOutcomeReaderTests' doc comment), fail closed with
            // a mapped error instead of letting an unhandled exception reach the API pipeline.
            return new QueryOutcome(false, null, ErrorCodes.Internal,
                $"Could not interpret the query engine response: {ex.Message}");
        }

        if (status == "completed")
        {
            return new QueryOutcome(true, resultIfCompleted, null, null);
        }

        // "summary"/"blocked"/token-budget statuses have success = true/false but no real error
        // object (e.g. status "summary" carries error = null) — surface a helpful message rather
        // than propagating null. Widget fetches use MaxRows/LIMIT 1, so this path is expected to
        // be rare in practice, but must still be handled since widget data is not an AI reader and
        // has no way to "confirm" past the AI token-budget gate.
        code ??= ErrorCodes.TokenBudgetConfirmationRequired;
        message ??= $"Query returned status '{status}' with no data — likely blocked by the AI " +
            "token-size safety budget. Narrow the widget SQL (fewer/narrower columns, add a WHERE " +
            "filter or LIMIT) and try again.";

        return new QueryOutcome(false, null, code, message);
    }
}

public readonly record struct QueryOutcome(bool Completed, object? Result, string? ErrorCode, string? ErrorMessage);
