using System.Security.Claims;
using System.Security.Cryptography;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Caching.Memory;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>Snapshot export. The HTML is parked in IMemoryCache behind a random one-time
/// token for 10 minutes instead of being streamed back through MCP (token bloat).</summary>
public static class ExportEndpoints
{
    public record ExportRequest(string? Pin);

    // IMemoryCache is single-instance per process (no distributed cache here), so a plain
    // in-process lock is sufficient to make the take-and-remove pair atomic and prevent two
    // concurrent requests from both retrieving the same one-time download.
    private static readonly object DownloadLock = new();

    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/dashboards/{id:guid}/export", async (
            Guid id, ExportRequest req, ClaimsPrincipal principal,
            DashboardExportService export, IMemoryCache cache, IConfiguration config, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            if (req.Pin is not null && (req.Pin.Length < 4 || req.Pin.Length > 32))
            {
                return Results.BadRequest(new { success = false, error = new { code = "VALIDATION_ERROR", message = "PIN must be 4-32 characters." } });
            }

            var html = await export.BuildHtmlAsync(userId.Value, id, req.Pin, ct);
            if (!html.Success)
            {
                return Results.NotFound(new { success = false, error = html.Error });
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
            cache.Set($"export:{token}", html.Data!, TimeSpan.FromMinutes(10));

            var publicUrl = (config["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    download_url = $"{publicUrl}/api/exports/{token}",
                    expires_in_sec = 600,
                    one_time = true,
                    encrypted = req.Pin is not null
                }
            });
        })
        .RequireAuthorization("KnowledgeWrite")
        .RequireRateLimiting("query");

        app.MapGet("/api/exports/{token}", (string token, HttpContext ctx, IMemoryCache cache) =>
        {
            var key = $"export:{token}";
            string? html;
            lock (DownloadLock)
            {
                if (!cache.TryGetValue(key, out html) || html is null) return Results.NotFound();
                cache.Remove(key); // one-time
            }

            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", "dashboard-snapshot.html");
        });
    }
}
