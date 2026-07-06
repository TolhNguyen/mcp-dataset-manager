namespace ExcelDatasetManager.Api.Auth;

public static class JwtCookie
{
    public const string CookieName = "edm_token";

    public static void Set(HttpContext ctx, string token)
    {
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });
    }

    public static void Clear(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            Path = "/"
        });
    }

    public static bool TryGetBearerToken(HttpContext ctx, out string token)
    {
        token = "";
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return false;
        }

        const string prefix = "Bearer ";
        var value = authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = value[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    public static bool IsDownloadPath(PathString path)
    {
        return path.StartsWithSegments("/api/datasets", out var remaining)
               && remaining.Value?.Contains("/download/", StringComparison.OrdinalIgnoreCase) == true;
    }
}
