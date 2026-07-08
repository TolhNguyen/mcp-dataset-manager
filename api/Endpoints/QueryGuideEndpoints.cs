using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class QueryGuideEndpoints
{
    public static void MapQueryGuideEndpoints(this WebApplication app)
    {
        app.MapGet("/api/query-guide", (QueryGuideService svc) =>
        {
            var (token, content) = svc.GetGuide();
            return Results.Ok(new { guide_token = token, content });
        }).RequireAuthorization("QueryAccess").RequireRateLimiting("query");
    }
}
