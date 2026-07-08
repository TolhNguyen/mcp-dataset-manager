using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class ContextEndpoints
{
    public static void MapContextEndpoints(this WebApplication app)
    {
        app.MapGet("/api/context", async (
            string? dataset_ids, string? tables, string? detail, string? guide_token,
            ClaimsPrincipal principal, ContextService svc, QueryGuideService guide, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            if (principal.IsApiKeyPrincipal()
                && !SchemaTokenLikeMatch(guide_token, guide.CurrentToken()))
            {
                return Results.Json(new
                {
                    success = false,
                    error = new
                    {
                        code = "GUIDE_REQUIRED",
                        message = "Call get_query_guide and READ it first, then pass the guide_token it returns.",
                        assistant_instruction = "Do not guess schema or fabricate data. Call get_query_guide, read it, then retry get_context with the guide_token."
                    }
                }, statusCode: 400);
            }

            var ids = ParseGuidCsv(dataset_ids);
            if (ids is null)
            {
                return Results.BadRequest(new { success = false, error = new { code = "VALIDATION_ERROR", message = "dataset_ids không hợp lệ." } });
            }

            var tableFilter = string.IsNullOrWhiteSpace(tables)
                ? null
                : tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mode = string.Equals(detail, "summary", StringComparison.OrdinalIgnoreCase) ? "summary" : "full";

            var result = await svc.BuildAsync(userId.Value, new ContextRequest(ids, tableFilter, mode), ct);
            return result.Success
                ? Results.Ok(result.Data)
                : result.Error?.Code == "DATASET_NOT_FOUND"
                    ? Results.NotFound(new { success = false, error = result.Error })
                    : Results.BadRequest(new { success = false, error = result.Error });
        }).RequireAuthorization("QueryAccess").RequireRateLimiting("query");
    }

    private static Guid[]? ParseGuidCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new Guid[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Guid.TryParse(parts[i], out ids[i])) return null;
        }
        return ids.Length == 0 ? null : ids;
    }

    private static bool SchemaTokenLikeMatch(string? provided, string expected)
        => SchemaTokenService.Matches(provided, expected);
}
