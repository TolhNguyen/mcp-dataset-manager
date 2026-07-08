using System.Security.Claims;

namespace ExcelDatasetManager.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public const string AuthMethodClaim = "auth_method";
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub");
        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    /// <summary>True if the principal authenticated with an API key (PAT). JWT sessions do not have this claim.</summary>
    public static bool IsApiKeyPrincipal(this ClaimsPrincipal principal)
        => principal.FindFirstValue(AuthMethodClaim) is not null;
}
