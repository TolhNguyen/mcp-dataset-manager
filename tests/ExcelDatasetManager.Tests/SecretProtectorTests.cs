using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SecretProtectorTests
{
    private static SecretProtector NewProtector(string key) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:MasterKey"] = key })
            .Build());

    private const string ValidKey = "unit-test-master-key-0123456789abcdef";

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = NewProtector(ValidKey);
        const string secret = """{"host":"db.example.com","password":"p@ss"}""";

        var protectedValue = protector.Protect(secret);

        Assert.StartsWith("v1:", protectedValue);
        Assert.DoesNotContain("db.example.com", protectedValue);
        Assert.Equal(secret, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Same_plaintext_produces_different_ciphertexts()
    {
        var protector = NewProtector(ValidKey);
        Assert.NotEqual(protector.Protect("secret"), protector.Protect("secret")); // nonce ngẫu nhiên
    }

    [Fact]
    public void Tampered_ciphertext_throws()
    {
        var protector = NewProtector(ValidKey);
        var value = protector.Protect("secret");
        var bytes = Convert.FromBase64String(value["v1:".Length..]);
        bytes[^1] ^= 0xFF;
        var tampered = "v1:" + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Wrong_key_cannot_unprotect()
    {
        var value = NewProtector(ValidKey).Protect("secret");
        var other = NewProtector("another-master-key-0123456789abcdefgh");

        Assert.ThrowsAny<Exception>(() => other.Unprotect(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void Constructor_rejects_missing_or_short_key(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => NewProtector(key!));
    }

    [Fact]
    public void Unprotect_rejects_values_without_version_prefix()
    {
        var protector = NewProtector(ValidKey);
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect("bm90LXZhbGlk"));
    }
}
