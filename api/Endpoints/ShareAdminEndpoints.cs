// api/Endpoints/ShareAdminEndpoints.cs
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// Owner-side share management. JWT and PAT both allowed (the user explicitly wants the AI
/// to create and revoke share links); everything is scoped by user_id, and the token+PIN
/// pair appears exactly once — in the create response.
/// </summary>
public static class ShareAdminEndpoints
{
    public record CreateShareRequest(string? Pin, int? ExpiresInDays);

    public static void MapShareAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/dashboards/{id:guid}/shares", async (
            Guid id, CreateShareRequest req, ClaimsPrincipal principal,
            DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var createdBy = principal.IsApiKeyPrincipal()
                ? "ai"
                : $"user:{principal.FindFirstValue(ClaimTypes.Email) ?? userId.Value.ToString()}";

            var result = await shares.CreateAsync(userId.Value, id, req.Pin, req.ExpiresInDays, createdBy, ct);
            if (result.Success)
            {
                return Results.Ok(new { success = true, data = result.Data });
            }

            return result.Error?.Code == ErrorCodes.DashboardNotFound
                ? Results.NotFound(new { success = false, error = result.Error })
                : Results.BadRequest(new { success = false, error = result.Error });
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapGet("/api/dashboards/{id:guid}/shares", async (
            Guid id, ClaimsPrincipal principal, DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await shares.ListAsync(userId.Value, id, ct);
            return Results.Ok(new { success = true, data = result.Data });
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/shares/{shareId:guid}", async (
            Guid shareId, ClaimsPrincipal principal, DashboardShareService shares, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var revoked = await shares.RevokeAsync(userId.Value, shareId, ct);
            return revoked
                ? Results.Ok(new { success = true, data = new { revoked = true, share_id = shareId } })
                : Results.NotFound(new { success = false, error = new { code = ErrorCodes.ShareNotFound, message = "Share not found." } });
        })
        .RequireAuthorization("KnowledgeWrite");
    }
}
