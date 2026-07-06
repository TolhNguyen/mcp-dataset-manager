using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class DatasetEndpoints
{
    public static void MapDatasetEndpoints(this WebApplication app)
    {
        var datasets = app.MapGroup("/api/datasets").RequireAuthorization();

        datasets.MapGet("/", async (ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var payload = await svc.ListAsync(userId, ct);
            return Results.Ok(new { success = true, limit = payload.Limit, datasets = payload.Datasets });
        });

        datasets.MapPost("/", async (HttpContext ctx, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;

            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    success = false,
                    error = new { code = ErrorCodes.InvalidRequest, message = "Content-Type must be multipart/form-data." }
                });
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var name = form["name"].ToString();

            var result = await svc.UploadAsync(userId, name, file, ct);

            return result.Success
                ? Results.Ok(new { success = true, dataset = result.Data, message = "Dataset uploaded successfully and is being processed." })
                : Results.BadRequest(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");

        datasets.MapGet("/{datasetId:guid}", async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.GetDetailAsync(userId, datasetId, ct);
            return result.Success
                ? Results.Ok(new { success = true, dataset = result.Data!.Dataset, tables = result.Data.Tables })
                : Results.NotFound(new { success = false, error = result.Error });
        });

        datasets.MapDelete("/{datasetId:guid}", async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var result = await svc.DeleteAsync(userId, datasetId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound(new { success = false, error = result.Error });
        }).RequireAuthorization("JwtOnly");

        datasets.MapGet("/{datasetId:guid}/download/original",
            async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var dl = await svc.GetOriginalDownloadAsync(userId, datasetId, ct);
            if (dl is null) return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset or file not found." } });
            return Results.File(dl.Path, dl.ContentType, dl.DownloadName);
        });

        datasets.MapGet("/{datasetId:guid}/download/manifest",
            async (Guid datasetId, ClaimsPrincipal principal, DatasetService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId()!.Value;
            var dl = await svc.GetManifestDownloadAsync(userId, datasetId, ct);
            if (dl is null) return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Manifest not available yet." } });
            return Results.File(dl.Path, "text/markdown; charset=utf-8", dl.DownloadName);
        });
    }
}
