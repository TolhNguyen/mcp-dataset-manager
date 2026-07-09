using Microsoft.AspNetCore.DataProtection;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Protects the viewer-session cookie for dashboard shares. The payload only carries the
/// share id + expiry; revocation still wins because every viewer call re-resolves the share
/// row from the DB.
/// </summary>
public class ShareSessionProtector(IDataProtectionProvider provider)
{
    public const int SessionHours = 12;
    private readonly IDataProtector _protector = provider.CreateProtector("edm.dashboard-share.v1");

    public string Protect(Guid shareId)
        => _protector.Protect($"{shareId:N}|{DateTime.UtcNow.AddHours(SessionHours).Ticks}");

    public Guid? TryUnprotect(string? cookieValue)
    {
        if (string.IsNullOrWhiteSpace(cookieValue)) return null;
        string plain;
        try { plain = _protector.Unprotect(cookieValue); }
        catch (Exception) { return null; } // CryptographicException on tamper/wrong key

        var parts = plain.Split('|');
        if (parts.Length != 2
            || !Guid.TryParseExact(parts[0], "N", out var shareId)
            || !long.TryParse(parts[1], out var ticks)
            || new DateTime(ticks, DateTimeKind.Utc) < DateTime.UtcNow)
        {
            return null;
        }

        return shareId;
    }
}
