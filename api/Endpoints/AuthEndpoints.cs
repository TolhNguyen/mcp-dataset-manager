using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        auth.MapPost("/register", async (RegisterRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
        {
            var result = await svc.RegisterAsync(req, ct);
            if (result.Success)
            {
                JwtCookie.Set(ctx, result.Data!.Token);
            }

            return result.Success
                ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, AuthService svc, CancellationToken ct) =>
        {
            var result = await svc.LoginAsync(req, ct);
            if (result.Success)
            {
                JwtCookie.Set(ctx, result.Data!.Token);
            }

            return result.Success
                ? Results.Ok(new { success = true, user = result.Data!.User, token = result.Data.Token })
                : Results.BadRequest(new { success = false, error = result.Error });
        });

        auth.MapGet("/me", async (ClaimsPrincipal principal, AuthService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MeAsync(userId.Value, ct);
            return result.Success
                ? Results.Ok(new { success = true, user = result.Data })
                : Results.Unauthorized();
        }).RequireAuthorization();

        auth.MapPost("/logout", (HttpContext ctx) =>
        {
            JwtCookie.Clear(ctx);
            return Results.Ok(new
            {
                success = true,
                message = "Token issuance is stateless — client should discard its token."
            });
        }).RequireAuthorization();
    }
}
