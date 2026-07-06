using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// Management endpoints for external ("live-query") database connections and the datasets built
/// on top of them. All routes require a JWT (kept out of API-key/PAT reach, same reasoning as
/// other management endpoints in <see cref="DatasetEndpoints"/>): connections hold credentials to
/// a user's own database and must not be creatable/testable/deletable via a dataset-scoped key.
/// </summary>
public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this WebApplication app)
    {
        var connections = app.MapGroup("/api/connections").RequireAuthorization("JwtOnly");

        connections.MapPost("/", async (
            CreateConnectionRequest req, ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.CreateAsync(
                userId, req.Name ?? "", req.Provider ?? "", req.Config ?? default, ct);

            return result.Success
                ? Results.Ok(new { success = true, connection = result.Data })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        connections.MapGet("/", async (ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var list = await svc.ListAsync(userId, ct);
            return Results.Ok(new { success = true, connections = list });
        });

        connections.MapPut("/{id:guid}", async (
            Guid id, UpdateConnectionRequest req, ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.UpdateAsync(userId, id, req.Name, req.Config, ct);

            return result.Success
                ? Results.Ok(new { success = true, connection = result.Data })
                : result.Error?.Code == ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        });

        connections.MapDelete("/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.DeleteAsync(userId, id, ct);

            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : result.Error?.Code == ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        });

        connections.MapPost("/{id:guid}/test", async (
            Guid id, ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.TestAsync(userId, id, ct);

            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : result.Error?.Code == ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        });

        connections.MapGet("/{id:guid}/tables", async (
            Guid id, ClaimsPrincipal principal, DbConnectionService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.ListRemoteTablesAsync(userId, id, ct);

            return result.Success
                ? Results.Ok(new { success = true, tables = result.Data })
                : result.Error?.Code == ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        });

        connections.MapPost("/{id:guid}/datasets", async (
            Guid id, CreateExternalDatasetRequest req, ClaimsPrincipal principal, ExternalSchemaService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.CreateExternalDatasetAsync(
                userId, id, req.Name ?? "", req.Tables ?? [], req.IncludeSamples ?? false, ct);

            return result.Success
                ? Results.Ok(new { success = true, dataset = result.Data })
                : result.Error?.Code == ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        });

        // Lives under /api/datasets, not /api/connections — kept in this file because it's part of
        // the external-dataset lifecycle handled by ExternalSchemaService, alongside creation above.
        app.MapPost("/api/datasets/{id:guid}/refresh-schema", async (
            Guid id, ClaimsPrincipal principal, ExternalSchemaService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.RefreshSchemaAsync(userId, id, ct);

            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : result.Error?.Code is ErrorCodes.DatasetNotFound or ErrorCodes.ConnectionNotFound
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");
    }
}
