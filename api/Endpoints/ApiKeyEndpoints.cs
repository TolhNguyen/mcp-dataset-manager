using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var userKeys = app.MapGroup("/api/user/api-keys").RequireAuthorization("JwtOnly");

        userKeys.MapPost("/", async (CreateUserApiKeyRequest req, ClaimsPrincipal principal, UserApiKeyService svc, CancellationToken ct) =>
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
