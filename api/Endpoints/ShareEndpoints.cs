// api/Endpoints/ShareEndpoints.cs
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// The ONLY three routes an anonymous share viewer can reach. Every route re-resolves the
/// share row first (revoke/expiry wins over any cookie), and every invalid token yields a
/// bare 404 — never a reason — so the routes can't be used as an existence oracle.
/// No route here accepts SQL, and no response contains SQL.
/// </summary>
public static class ShareEndpoints
{
    public record PinRequest(string? Pin);

    public static void MapShareEndpoints(this WebApplication app)
    {
        app.MapGet("/share/{token}", async (
            string token, HttpContext ctx, DashboardShareService shares,
            IWebHostEnvironment env, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null) return Results.NotFound();

            ApplyShareHeaders(ctx);
            return Results.File(Path.Combine(env.WebRootPath, "share.html"), "text/html; charset=utf-8");
        });

        app.MapPost("/api/share/{token}/session", async (
            string token, PinRequest req, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null) return Results.NotFound();

            var log = loggerFactory.CreateLogger("ShareEndpoints");
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (share.LockedUntil is DateTime locked && locked > DateTime.UtcNow)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling((locked - DateTime.UtcNow).TotalMinutes));
                return Results.Json(new
                {
                    success = false,
                    error = new { code = "SHARE_LOCKED", message = $"Too many wrong PINs. Try again in {minutes} minute(s)." }
                }, statusCode: 429);
            }

            if (string.IsNullOrEmpty(req.Pin) || !ShareCrypto.VerifyPin(req.Pin, share.PinHash))
            {
                await shares.RegisterPinFailureAsync(share.Id, ct);
                log.LogWarning("Share {ShareId}: wrong PIN from {Ip}", share.Id, ip);
                return Results.Json(new
                {
                    success = false,
                    error = new { code = "SHARE_PIN_INVALID", message = "Wrong PIN." }
                }, statusCode: 401);
            }

            await shares.ResetPinFailuresAsync(share.Id, ct);
            // Path giới hạn /api/share theo spec — cookie không bao giờ đi kèm request nào khác.
            // Secure: sau IIS/ARR (TLS terminate ở proxy) IsHttps chỉ đúng khi app có
            // UseForwardedHeaders; nếu chưa có, thêm ForwardedHeaders middleware trước auth.
            ctx.Response.Cookies.Append(CookieName(share.Id), protector.Protect(share.Id), new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/api/share",
                MaxAge = TimeSpan.FromHours(ShareSessionProtector.SessionHours)
            });
            return Results.NoContent();
        })
        .RequireRateLimiting("share-pin");

        app.MapGet("/api/share/{token}/dashboard", async (
            string token, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector, DashboardService dashboards,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null || !HasValidSession(ctx, protector, share.Id)) return Results.NotFound();

            ApplyShareHeaders(ctx);
            await shares.RecordViewAsync(share.Id, ct);
            loggerFactory.CreateLogger("ShareEndpoints").LogInformation(
                "Share {ShareId} viewed from {Ip}", share.Id, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            var view = await dashboards.GetShareViewAsync(share.UserId, share.DashboardId, ct);
            return view.Success
                ? Results.Ok(new { success = true, data = view.Data })
                : Results.NotFound();
        });

        app.MapGet("/api/share/{token}/widgets/{widgetId:guid}/data", async (
            string token, Guid widgetId, HttpContext ctx,
            DashboardShareService shares, ShareSessionProtector protector, DashboardService dashboards,
            CancellationToken ct) =>
        {
            var share = await shares.ResolveAsync(token, ct);
            if (share is null || !HasValidSession(ctx, protector, share.Id)) return Results.NotFound();

            ApplyShareHeaders(ctx);
            // GetWidgetDataAsync joins widget→dashboard→user, so a widgetId outside this
            // share's dashboard fails ownership inside the service. Runs the FROZEN sql only.
            var result = await dashboards.GetWidgetDataAsync(share.UserId, share.DashboardId, widgetId, ct);
            return result.Success
                ? Results.Ok(new { success = true, data = result.Data })
                : Results.NotFound();
        })
        .RequireRateLimiting("query");
    }

    private static string CookieName(Guid shareId) => $"edm_share_{shareId:N}";

    private static bool HasValidSession(HttpContext ctx, ShareSessionProtector protector, Guid shareId)
        => ctx.Request.Cookies.TryGetValue(CookieName(shareId), out var value)
           && protector.TryUnprotect(value) == shareId;

    private static void ApplyShareHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:";
    }
}
