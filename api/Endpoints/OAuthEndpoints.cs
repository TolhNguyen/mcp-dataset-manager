using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;

namespace ExcelDatasetManager.Api.Endpoints;

public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        var publicUrl = (app.Configuration["Oauth:PublicUrl"] ?? "http://localhost").TrimEnd('/');

        // ---- Discovery metadata (RFC 8414 + RFC 9728), anonymous ----

        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Ok(new
        {
            issuer = publicUrl,
            authorization_endpoint = $"{publicUrl}/oauth/authorize",
            token_endpoint = $"{publicUrl}/api/oauth/token",
            registration_endpoint = $"{publicUrl}/api/oauth/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }
        }));

        var prm = () => Results.Ok(new
        {
            resource = $"{publicUrl}/mcp",
            authorization_servers = new[] { publicUrl },
            bearer_methods_supported = new[] { "header" }
        });
        app.MapGet("/.well-known/oauth-protected-resource", prm);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", prm);

        // ---- Dynamic Client Registration (RFC 7591), anonymous + rate limited ----

        app.MapPost("/api/oauth/register",
            async (OAuthRegisterRequest req, OAuthService svc, CancellationToken ct) =>
        {
            var result = await svc.RegisterClientAsync(req.ClientName, req.RedirectUris ?? [], ct);
            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            var client = result.Value!;
            return Results.Created($"/api/oauth/register/{client.ClientId}", new
            {
                client_id = client.ClientId,
                client_name = client.ClientName,
                redirect_uris = client.RedirectUris,
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code" },
                response_types = new[] { "code" }
            });
        }).RequireRateLimiting("auth");

        // ---- Authorize page (HTML; JS trong trang tự xử lý login + approve) ----

        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
            Results.File(
                Path.Combine(app.Environment.WebRootPath, "oauth-authorize.html"),
                "text/html; charset=utf-8"));

        // ---- Approve (đăng nhập bằng JWT/cookie; PAT không được mint token mới) ----

        app.MapPost("/api/oauth/authorize/approve",
            async (OAuthApproveRequest req, ClaimsPrincipal principal, OAuthService svc, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.ClientId)
                || string.IsNullOrWhiteSpace(req.RedirectUri)
                || string.IsNullOrWhiteSpace(req.CodeChallenge))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "client_id, redirect_uri and code_challenge are required."
                });
            }

            if (!string.Equals(req.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "Only code_challenge_method=S256 is supported."
                });
            }

            var result = await svc.CreateAuthorizationCodeAsync(
                userId.Value, req.ClientId, req.RedirectUri, req.CodeChallenge, ct);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            var separator = req.RedirectUri.Contains('?') ? '&' : '?';
            var redirectTo = $"{req.RedirectUri}{separator}code={Uri.EscapeDataString(result.Value!)}";
            if (!string.IsNullOrEmpty(req.State))
            {
                redirectTo += $"&state={Uri.EscapeDataString(req.State)}";
            }

            return Results.Ok(new { redirect_to = redirectTo });
        }).RequireAuthorization("JwtOnly").RequireRateLimiting("auth");

        // ---- Token endpoint (form-urlencoded per OAuth spec), anonymous + rate limited ----

        app.MapPost("/api/oauth/token", async (HttpContext ctx, OAuthService svc, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    error_description = "Content-Type must be application/x-www-form-urlencoded."
                });
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            var grantType = form["grant_type"].ToString();
            if (grantType != "authorization_code")
            {
                return Results.BadRequest(new
                {
                    error = "unsupported_grant_type",
                    error_description = "Only authorization_code is supported."
                });
            }

            var result = await svc.ExchangeCodeAsync(
                clientId: form["client_id"].ToString(),
                redirectUri: form["redirect_uri"].ToString(),
                code: form["code"].ToString(),
                codeVerifier: form["code_verifier"].ToString(),
                ct);

            if (!result.Success)
            {
                return Results.BadRequest(new { error = result.Error, error_description = result.ErrorDescription });
            }

            return Results.Ok(new
            {
                access_token = result.Value,
                token_type = "Bearer"
            });
        }).RequireRateLimiting("auth");
    }
}
