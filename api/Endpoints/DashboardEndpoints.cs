using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// HTTP surface for dashboards + widgets. Mirrors the ownership + scoped-key checks used by
/// <see cref="KnowledgeEndpoints"/>: ownership itself is enforced inside
/// <see cref="DashboardService"/> (every query is scoped by user_id), while a dataset-scoped API
/// key is additionally restricted here so it can only create/update widgets against the one
/// dataset it was minted for. The widget "data" route is JwtOnly (browser-only reads) and rate
/// limited — the browser never sends SQL, only dashboard_id/widget_id, so there is nothing for a
/// scoped key to abuse there in the first place.
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        // ============================================================
        // Dashboards
        // ============================================================

        app.MapPost("/api/dashboards", async (
            CreateDashboardRequest req,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await dashboardService.CreateDashboardAsync(userId.Value, req.Name, req.Description, source, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly");

        app.MapGet("/api/dashboards", async (
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await dashboardService.ListDashboardsAsync(userId.Value, ct);
            return Results.Ok(new { success = true, data = result.Data });
        })
        .RequireAuthorization("JwtOnly");

        app.MapGet("/api/dashboards/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await dashboardService.GetDashboardAsync(userId.Value, id, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly");

        app.MapDelete("/api/dashboards/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await dashboardService.DeleteDashboardAsync(userId.Value, id, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly");

        // ============================================================
        // Widgets
        // ============================================================

        app.MapPost("/api/dashboards/{id:guid}/widgets", async (
            Guid id, CreateWidgetRequest req,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null && req.DatasetId is not null && scopedDatasetId != req.DatasetId)
            {
                return Results.Forbid();
            }

            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await dashboardService.CreateWidgetAsync(userId.Value, id, req, source, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapPut("/api/dashboards/{id:guid}/widgets/{wid:guid}", async (
            Guid id, Guid wid, UpdateWidgetRequest req,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null)
            {
                // UpdateWidgetRequest carries no dataset_id (a widget never changes dataset on
                // update), so a scoped key's match must be checked against the widget's existing
                // dataset_id rather than the request body.
                var existing = await dashboardService.GetDashboardAsync(userId.Value, id, ct);
                if (!existing.Success)
                {
                    return MapWriteResult(existing);
                }

                var widgetDatasetId = FindWidgetDatasetId(existing.Data, wid);
                if (widgetDatasetId is null || widgetDatasetId != scopedDatasetId)
                {
                    return Results.Forbid();
                }
            }

            var result = await dashboardService.UpdateWidgetAsync(userId.Value, id, wid, req, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/dashboards/{id:guid}/widgets/{wid:guid}", async (
            Guid id, Guid wid,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null)
            {
                var existing = await dashboardService.GetDashboardAsync(userId.Value, id, ct);
                if (!existing.Success)
                {
                    return MapWriteResult(existing);
                }

                var widgetDatasetId = FindWidgetDatasetId(existing.Data, wid);
                if (widgetDatasetId is null || widgetDatasetId != scopedDatasetId)
                {
                    return Results.Forbid();
                }
            }

            var result = await dashboardService.ArchiveWidgetAsync(userId.Value, id, wid, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/dashboards/{id:guid}/widgets/{wid:guid}/hard", async (
            Guid id, Guid wid,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await dashboardService.HardDeleteWidgetAsync(userId.Value, id, wid, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly");

        // Browser-only widget data reads: JWT only (no scoped key reaches this route at all), and
        // rate limited like every other query-executing route.
        app.MapGet("/api/dashboards/{id:guid}/widgets/{wid:guid}/data", async (
            Guid id, Guid wid,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await dashboardService.GetWidgetDataAsync(userId.Value, id, wid, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly")
        .RequireRateLimiting("query");

        // MCP convenience: create a widget, auto-creating the dashboard by name if it doesn't
        // exist yet, so an agent can address a dashboard by name without a prior lookup call.
        app.MapPost("/api/dashboards/widgets", async (
            CreateWidgetByDashboardNameRequest req,
            ClaimsPrincipal principal, DashboardService dashboardService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null && req.DatasetId is not null && scopedDatasetId != req.DatasetId)
            {
                return Results.Forbid();
            }

            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var ensured = await dashboardService.EnsureDashboardByNameAsync(userId.Value, req.DashboardName, source, actor, ct);
            if (!ensured.Success)
            {
                return MapWriteResult(ensured);
            }

            var dashboardId = FindDashboardId(ensured.Data);
            if (dashboardId is null)
            {
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.Internal, message = "Could not resolve dashboard id." } });
            }

            var createReq = new CreateWidgetRequest(
                req.DatasetId, req.Title, req.Sql, req.ChartType, req.ChartConfig, req.RefreshIntervalSec);

            var result = await dashboardService.CreateWidgetAsync(userId.Value, dashboardId.Value, createReq, source, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    /// <summary>
    /// Derives (source, actor) for a dashboard/widget write. Dataset-scoped API keys are
    /// attributed to "ai" with the key's display name; JWT sessions and user-scoped PATs are
    /// attributed to "user" with the account email when the JWT carries it, else falling back to
    /// "user:{userId}". Mirrors KnowledgeEndpoints.ResolveSourceAndActor.
    /// </summary>
    private static (string Source, string Actor) ResolveSourceAndActor(ClaimsPrincipal principal, Guid userId)
    {
        var authMethod = principal.FindFirstValue(ClaimsPrincipalExtensions.AuthMethodClaim);

        if (authMethod == "dataset_api_key")
        {
            var keyName = principal.GetKeyName() ?? "key";
            return ("ai", $"ai:{keyName}");
        }

        var email = principal.FindFirstValue(ClaimTypes.Email);
        var actor = !string.IsNullOrWhiteSpace(email) ? email : $"user:{userId}";
        return ("user", actor);
    }

    /// <summary>
    /// Reads dashboard_id back out of CreateOrEnsureDashboardAsync's anonymous DTO. Same-assembly
    /// `dynamic` access, mirroring <see cref="DashboardService.QueryOutcomeReader"/>'s precedent
    /// for reading anonymous-type response shapes without a public record.
    /// </summary>
    private static Guid? FindDashboardId(object? data)
    {
        if (data is null) return null;
        try
        {
            dynamic dto = data;
            return (Guid)dto.dashboard_id;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a widget's dataset_id out of GetDashboardAsync's { dashboard, widgets } DTO, for
    /// scoped-key match checks on update/archive routes where the request body carries no
    /// dataset_id of its own. Returns null if the widget isn't found among the dashboard's
    /// (active) widgets — the caller treats that as "not this key's dataset" and forbids.
    /// </summary>
    private static Guid? FindWidgetDatasetId(object? data, Guid widgetId)
    {
        if (data is null) return null;
        try
        {
            dynamic dto = data;
            foreach (dynamic widget in dto.widgets)
            {
                if ((Guid)widget.widget_id == widgetId)
                {
                    return (Guid)widget.dataset_id;
                }
            }
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }

        return null;
    }

    private static IResult MapWriteResult(ApiResult<object> result)
    {
        if (result.Success)
        {
            return Results.Ok(new { success = true, data = result.Data });
        }

        return result.Error?.Code switch
        {
            ErrorCodes.DashboardNotFound => Results.NotFound(new { success = false, error = result.Error }),
            ErrorCodes.WidgetNotFound => Results.NotFound(new { success = false, error = result.Error }),
            ErrorCodes.DatasetNotFound => Results.NotFound(new { success = false, error = result.Error }),
            ErrorCodes.ValidationError => Results.BadRequest(new { success = false, error = result.Error }),
            ErrorCodes.DashboardLimitReached => Results.BadRequest(new { success = false, error = result.Error }),
            ErrorCodes.WidgetLimitReached => Results.BadRequest(new { success = false, error = result.Error }),
            _ => Results.BadRequest(new { success = false, error = result.Error })
        };
    }
}
