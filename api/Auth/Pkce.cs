using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Auth;

public static class Pkce
{
    /// <summary>RFC 7636: challenge = BASE64URL(SHA256(ASCII(verifier))), method S256 only.</summary>
    public static bool VerifyS256(string codeVerifier, string codeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(codeChallenge))
        {
            return false;
        }

        var computed = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(codeChallenge));
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
