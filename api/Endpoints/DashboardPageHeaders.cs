namespace ExcelDatasetManager.Api.Endpoints;

/// <summary>
/// Headers cho MỌI response serve HTML do AI dựng (owner raw route + share page route).
/// CSP `sandbox allow-scripts` (KHÔNG allow-same-origin) sandbox chính DOCUMENT được trả về —
/// tức là kể cả khi user mở URL này top-level (không qua iframe), trang vẫn chạy trong opaque
/// origin: không cookie, không localStorage, không gọi được API cùng origin. Vì sandbox nằm ở
/// response chứ không ở thẻ iframe, shell KHÔNG đặt sandbox attribute (đặt sẽ chặn cookie phiên
/// ngay từ request nạp iframe — xem js/page-embed.js).
/// script-src cho phép inline + cdnjs.cloudflare.com (thói quen artifact của Claude: Chart.js…);
/// connect-src 'none' chặn mọi fetch/XHR/WebSocket từ bên trong trang — data chỉ vào được qua
/// postMessage từ shell.
/// </summary>
public static class DashboardPageHeaders
{
    public const string Csp =
        "sandbox allow-scripts; default-src 'none'; " +
        "script-src 'unsafe-inline' https://cdnjs.cloudflare.com; " +
        "style-src 'unsafe-inline' https://cdnjs.cloudflare.com; " +
        "img-src data: blob:; font-src data: https://cdnjs.cloudflare.com; " +
        "connect-src 'none'";

    public static void Apply(HttpContext ctx)
    {
        ctx.Response.Headers["Content-Security-Policy"] = Csp;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Cache-Control"] = "no-store";
    }
}
