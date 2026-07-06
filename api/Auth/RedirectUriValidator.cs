namespace ExcelDatasetManager.Api.Auth;

public static class RedirectUriValidator
{
    /// <summary>Chỉ nhận https tuyệt đối; http chỉ cho localhost/127.0.0.1 (dev).</summary>
    public static bool IsAllowed(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttps) return true;

        return parsed.Scheme == Uri.UriSchemeHttp
               && (parsed.Host == "localhost" || parsed.Host == "127.0.0.1");
    }
}
