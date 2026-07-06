using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var datasets = app.MapGroup("/api/datasets").RequireAuthorization();

        datasets.MapPost("/{datasetId:guid}/api-keys",
            async (Guid datasetId, CreateDatasetApiKeyRequest req, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.CreateAsync(userId, datasetId, req.Name, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.BadRequest(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");

        datasets.MapGet("/{datasetId:guid}/api-keys",
            async (Guid datasetId, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.ListAsync(userId, datasetId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");

        datasets.MapDelete("/{datasetId:guid}/api-keys/{keyId:guid}",
            async (Guid datasetId, Guid keyId, ClaimsPrincipal principal, DatasetApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.RevokeAsync(userId, datasetId, keyId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");

        var userKeys = app.MapGroup("/api/user/api-keys").RequireAuthorization("JwtOnly");

        userKeys.MapPost("/", async (CreateDatasetApiKeyRequest req, ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.CreateAsync(userId, req.Name, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        userKeys.MapGet("/", async (ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.ListAsync(userId, ct);
            return Results.Ok(new { success = true, data = result.Data });
        });

        userKeys.MapDelete("/{tokenId:guid}", async (Guid tokenId, ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.RevokeAsync(userId, tokenId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound(new { success = false, error = result.Error });
        });
    }
}
