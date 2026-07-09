using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardExportCryptoTests
{
    [Fact]
    public void Encrypt_then_decrypt_roundtrips()
    {
        var (salt, iv, cipher) = ExportCrypto.Encrypt("1234", """{"a":1}""");
        Assert.Equal("""{"a":1}""", ExportCrypto.Decrypt("1234", salt, iv, cipher));
    }

    [Fact]
    public void Wrong_pin_fails_authentication()
    {
        var (salt, iv, cipher) = ExportCrypto.Encrypt("1234", """{"a":1}""");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ExportCrypto.Decrypt("9999", salt, iv, cipher));
    }

    [Fact]
    public void Cipher_carries_appended_tag()
    {
        var (_, _, cipher) = ExportCrypto.Encrypt("1234", "x");
        Assert.Equal(1 + 16, Convert.FromBase64String(cipher).Length); // 1 byte plaintext + 16 byte GCM tag
    }
}
