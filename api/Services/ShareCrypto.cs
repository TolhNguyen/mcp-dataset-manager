using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Pure crypto/policy helpers for dashboard share links. DB access lives in
/// DashboardShareService; keep this class dependency-free so it stays unit-testable.
/// </summary>
public static class ShareCrypto
{
    public const string TokenPrefix = "shr_";
    public const int MaxActiveSharesPerDashboard = 10;
    private const int PinIterations = 100_000;

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(20); // 160 bit
        return TokenPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Same shape as ApiKeyAuthenticationHandler.HashKey: SHA-256, uppercase hex.
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string GeneratePin()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinIterations, HashAlgorithmName.SHA256, 32);
        return $"{PinIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPin(string pin, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[1]); expected = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(pin ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static int ClampExpiryDays(int? requested) => Math.Clamp(requested ?? 30, 1, 90);

    /// <summary>Every 5th consecutive failure locks the share for 15min * 2^(n/5 - 1).</summary>
    public static DateTime? NextLockout(int failedPinCount, DateTime nowUtc)
    {
        if (failedPinCount < 5 || failedPinCount % 5 != 0) return null;
        var factor = Math.Min(failedPinCount / 5 - 1, 10); // cap 15min * 2^10 ≈ 10.6 ngày
        return nowUtc.AddMinutes(15 * Math.Pow(2, factor));
    }
}
