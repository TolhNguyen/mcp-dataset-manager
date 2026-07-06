using ExcelDatasetManager.Api.Auth;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class PkceTests
{
    // RFC 7636 Appendix B test vector
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Accepts_rfc7636_test_vector()
    {
        Assert.True(Pkce.VerifyS256(Verifier, Challenge));
    }

    [Theory]
    [InlineData("wrong-verifier-wrong-verifier-wrong-verifier", Challenge)]
    [InlineData(Verifier, "wrong-challenge")]
    [InlineData("", Challenge)]
    [InlineData(Verifier, "")]
    public void Rejects_mismatches_and_empty(string verifier, string challenge)
    {
        Assert.False(Pkce.VerifyS256(verifier, challenge));
    }

    [Fact]
    public void Base64UrlEncode_has_no_padding_or_url_unsafe_chars()
    {
        var encoded = Pkce.Base64UrlEncode(new byte[] { 0xfb, 0xff, 0x3e, 0x00, 0x01 });
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
    }
}
