using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class QueryEndpoints
{
    public static void MapQueryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/datasets/{datasetId:guid}/query",
            async (Guid datasetId, QueryRequest req, ClaimsPrincipal principal, DuckDbQueryService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var scopedDatasetId = principal.GetScopedDatasetId();
            if (scopedDatasetId is not null && scopedDatasetId != datasetId)
            {
                return Results.Forbid();
            }

            var result = await svc.QueryAsync(userId.Value, datasetId, req, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");
    }
}
