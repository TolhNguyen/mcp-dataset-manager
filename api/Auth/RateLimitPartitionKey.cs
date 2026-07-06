using System.Security.Claims;

namespace ExcelDatasetManager.Api.Auth;

public static class RateLimitPartitionKey
{
    /// <summary>
    /// Partition key for rate limiting: authenticated user id when available,
    /// otherwise the caller IP (requires ForwardedHeaders behind a proxy), otherwise "unknown".
    /// </summary>
    public static string For(HttpContext context) =>
        context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
