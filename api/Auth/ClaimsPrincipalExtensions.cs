using System.Security.Claims;

namespace ExcelDatasetManager.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public const string DatasetIdClaim = "dataset_id";
    public const string AuthMethodClaim = "auth_method";
    public const string CanWriteClaim = "can_write";
    public const string KeyNameClaim = "key_name";

    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub");
        return Guid.TryParse(value, out var userId) ? userId : null;
    }

    public static Guid? GetScopedDatasetId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(DatasetIdClaim);
        return Guid.TryParse(value, out var datasetId) ? datasetId : null;
    }

    public static string? GetKeyName(this ClaimsPrincipal principal)
        => principal.FindFirstValue(KeyNameClaim);

    /// <summary>
    /// True if the caller is allowed to write knowledge (memory) entries.
    ///
    /// JWT-authenticated principals (browser sessions) never carry an <c>auth_method</c>
    /// claim — that claim is only added by <see cref="ApiKeyAuthenticationHandler"/> for
    /// API-key requests — so its absence means "authenticated via JWT", which is always
    /// full-write. User-scoped PATs ("user_api_key") are likewise always full-write since
    /// they act as the user themselves. Only dataset-scoped API keys ("dataset_api_key")
    /// are restricted, and only get write access when their <c>can_write</c> claim is "true".
    /// </summary>
    public static bool CanWriteKnowledge(this ClaimsPrincipal principal)
    {
        var authMethod = principal.FindFirstValue(AuthMethodClaim);

        if (authMethod is null || authMethod == "user_api_key")
        {
            return true;
        }

        return authMethod == "dataset_api_key"
               && principal.FindFirstValue(CanWriteClaim) == "true";
    }
}
