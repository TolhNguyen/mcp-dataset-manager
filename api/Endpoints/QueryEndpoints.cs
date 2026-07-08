using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Npgsql;

namespace ExcelDatasetManager.Api.Endpoints;

public static class QueryEndpoints
{
    public static void MapQueryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/datasets/{datasetId:guid}/query",
            async (Guid datasetId, QueryRequest req, ClaimsPrincipal principal,
                DuckDbQueryService duckDbSvc, ExternalQueryService externalSvc,
                DatasetService datasetService, NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);

            if (principal.IsApiKeyPrincipal() && dataset is not null)
            {
                var expected = await SchemaTokenGate.ComputeCurrentAsync(dataSource, datasetId, ct);
                var gateError = SchemaTokenGate.BuildGateError(req.Options?.SchemaToken, expected, datasetId);
                if (gateError is not null) return Results.Json(gateError, statusCode: 400);
            }

            var result = dataset is not null && string.Equals(dataset.SourceKind, "external_db", StringComparison.OrdinalIgnoreCase)
                ? await externalSvc.QueryAsync(userId.Value, dataset, req, ct)
                : await duckDbSvc.QueryAsync(userId.Value, datasetId, req, ct);

            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");

        app.MapPost("/api/query",
            async (MultiQueryRequest req, ClaimsPrincipal principal, DuckDbQueryService duckDbSvc,
                NpgsqlDataSource dataSource, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var ids = req.DatasetIds ?? Array.Empty<Guid>();
            if (principal.IsApiKeyPrincipal())
            {
                foreach (var id in ids)
                {
                    var expected = await SchemaTokenGate.ComputeCurrentAsync(dataSource, id, ct);
                    var provided = req.SchemaTokens is not null && req.SchemaTokens.TryGetValue(id.ToString(), out var t) ? t : null;
                    var gateError = SchemaTokenGate.BuildGateError(provided, expected, id);
                    if (gateError is not null) return Results.Json(gateError, statusCode: 400);
                }
            }

            var query = new QueryRequest("sql", req.Sql, req.Options);
            var result = await duckDbSvc.QueryMultiAsync(userId.Value, ids, query, ct);
            return Results.Ok(result);
        })
        .RequireAuthorization("QueryAccess")
        .RequireRateLimiting("query");
    }
}
