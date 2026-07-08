using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// HTTP surface for dataset "knowledge" (memory) entries: notes/facts attached to a dataset that
/// get surfaced to AI callers alongside query results. Mirrors the ownership + scoped-key checks
/// used by <see cref="QueryEndpoints"/> and <see cref="ConnectionEndpoints"/> on every route,
/// including the cross-dataset search endpoint where every requested dataset id must pass.
/// </summary>
public static class KnowledgeEndpoints
{
    private const int DefaultSearchLimit = 5;
    private const int MaxSearchLimit = 20;

    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/datasets/{datasetId:guid}/knowledge", async (
            Guid datasetId, bool? include_archived, string? kind,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            var result = await knowledgeService.ListAsync(datasetId, include_archived ?? false, kind, ct);
            return Results.Ok(new { success = true, data = result.Data });
        })
        .RequireAuthorization("QueryAccess");

        app.MapPost("/api/datasets/{datasetId:guid}/knowledge", async (
            Guid datasetId, CreateKnowledgeRequest req,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            if (principal.IsApiKeyPrincipal() && !dataset.AiCanWriteKnowledge)
            {
                return KnowledgeWriteDisabled();
            }

            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await knowledgeService.CreateAsync(datasetId, req, source, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        // Upload a .md/.txt document; split by heading into knowledge entries (kind=document).
        app.MapPost("/api/datasets/{datasetId:guid}/knowledge/documents", async (
            Guid datasetId, HttpContext ctx,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            if (principal.IsApiKeyPrincipal() && !dataset.AiCanWriteKnowledge)
            {
                return KnowledgeWriteDisabled();
            }

            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = "Content-Type phải là multipart/form-data." } });
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = "Cần một file .md hoặc .txt." } });
            }

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".md" or ".txt"))
            {
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = "Chỉ hỗ trợ .md hoặc .txt." } });
            }
            if (file.Length > 1024 * 1024)
            {
                return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = "File vượt quá 1MB." } });
            }

            string text;
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                text = await reader.ReadToEndAsync(ct);
            }

            var sections = DocumentImporter.Split(text, Path.GetFileNameWithoutExtension(file.FileName));
            var (source, actor) = ResolveSourceAndActor(principal, userId.Value);

            var imported = 0;
            var skipped = 0;
            foreach (var (title, content) in sections)
            {
                var res = await knowledgeService.CreateAsync(
                    datasetId, new CreateKnowledgeRequest("document", title, content, false), source, actor, ct);
                if (res.Success) imported++;
                else { skipped++; if (res.Error?.Code == ErrorCodes.KnowledgeLimitReached) break; }
            }

            return Results.Ok(new { success = true, data = new { imported, skipped } });
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapPut("/api/datasets/{datasetId:guid}/knowledge/{entryId:guid}", async (
            Guid datasetId, Guid entryId, UpdateKnowledgeRequest req,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            if (principal.IsApiKeyPrincipal() && !dataset.AiCanWriteKnowledge)
            {
                return KnowledgeWriteDisabled();
            }

            var (_, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await knowledgeService.UpdateAsync(datasetId, entryId, req, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/datasets/{datasetId:guid}/knowledge/{entryId:guid}", async (
            Guid datasetId, Guid entryId,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            var (_, actor) = ResolveSourceAndActor(principal, userId.Value);

            var result = await knowledgeService.ArchiveAsync(datasetId, entryId, actor, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("KnowledgeWrite");

        app.MapDelete("/api/datasets/{datasetId:guid}/knowledge/{entryId:guid}/hard", async (
            Guid datasetId, Guid entryId,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
            if (dataset is null)
            {
                return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = "Dataset not found." } });
            }

            var result = await knowledgeService.HardDeleteAsync(datasetId, entryId, ct);
            return MapWriteResult(result);
        })
        .RequireAuthorization("JwtOnly");

        app.MapGet("/api/knowledge/search", async (
            string? dataset_ids, string? q, int? limit,
            ClaimsPrincipal principal, DatasetService datasetService, KnowledgeService knowledgeService, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var ids = (dataset_ids ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            var datasetIds = new List<Guid>(ids.Length);
            foreach (var idText in ids)
            {
                if (!Guid.TryParse(idText, out var datasetId))
                {
                    return Results.BadRequest(new { success = false, error = new { code = ErrorCodes.ValidationError, message = $"Invalid dataset id: {idText}" } });
                }

                var dataset = await datasetService.GetDatasetRecordAsync(userId.Value, datasetId, ct);
                if (dataset is null)
                {
                    return Results.NotFound(new { success = false, error = new { code = ErrorCodes.DatasetNotFound, message = $"Dataset not found: {datasetId}" } });
                }

                datasetIds.Add(datasetId);
            }

            var clampedLimit = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaxSearchLimit);

            var result = await knowledgeService.SearchAsync(datasetIds.ToArray(), q ?? "", clampedLimit, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.BadRequest(new { success = false, error = result.Error });
        })
        .RequireAuthorization("QueryAccess");
    }

    // ============================================================
    // Internal helpers
    // ============================================================

    /// <summary>
    /// Derives (source, actor) for a knowledge write. Dataset-scoped API keys are attributed to
    /// "ai" with the key's display name; JWT sessions and user-scoped PATs are attributed to
    /// "user" with the account email when the JWT carries it (issued at login — see
    /// AuthService.CreateToken), else falling back to "user:{userId}" (PATs currently don't
    /// carry an email claim).
    /// </summary>
    private static (string Source, string Actor) ResolveSourceAndActor(ClaimsPrincipal principal, Guid userId)
    {
        var email = principal.FindFirstValue(ClaimTypes.Email);
        var actor = !string.IsNullOrWhiteSpace(email) ? email : $"user:{userId}";
        return ("user", actor);
    }

    private static IResult KnowledgeWriteDisabled() =>
        Results.Json(new
        {
            success = false,
            error = new
            {
                code = ErrorCodes.KnowledgeWriteDisabled,
                message = "The dataset owner disabled AI knowledge writes for this dataset.",
                assistant_instruction = "Report this to the user verbatim. Do not retry."
            }
        }, statusCode: 403);

    private static IResult MapWriteResult(ApiResult<object> result)
    {
        if (result.Success)
        {
            return Results.Ok(new { success = true, data = result.Data });
        }

        return result.Error?.Code switch
        {
            ErrorCodes.KnowledgeNotFound => Results.NotFound(new { success = false, error = result.Error }),
            ErrorCodes.ValidationError => Results.BadRequest(new { success = false, error = result.Error }),
            ErrorCodes.KnowledgeLimitReached => Results.BadRequest(new { success = false, error = result.Error }),
            _ => Results.BadRequest(new { success = false, error = result.Error })
        };
    }
}
