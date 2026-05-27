using System.Security.Claims;

namespace ExcelDatasetManager.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public const string DatasetIdClaim = "dataset_id";

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
}
