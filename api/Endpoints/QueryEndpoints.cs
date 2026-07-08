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
            async (Guid datasetId, QueryRequest req, ClaimsPrincipal principal,
                DuckDbQueryService duckDbSvc, ExternalQueryService externalSvc,
                DatasetService datasetService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);

            var result = dataset is not null && string.Equals(dataset.SourceKind, "external_db", StringComparison.OrdinalIgnoreCase)
                ? await externalSvc.QueryAsync(userId.Value, dataset, req, ct)
                : await duckDbSvc.QueryAsync(userId.Value, datasetId, req, ct);

            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");

        app.MapPost("/api/query",
            async (MultiQueryRequest req, ClaimsPrincipal principal, DuckDbQueryService duckDbSvc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var ids = req.DatasetIds ?? Array.Empty<Guid>();
            var query = new QueryRequest("sql", req.Sql, req.Options);
            var result = await duckDbSvc.QueryMultiAsync(userId.Value, ids, query, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");
    }
}
