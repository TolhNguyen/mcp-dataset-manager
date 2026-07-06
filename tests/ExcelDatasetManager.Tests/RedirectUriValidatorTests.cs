using ExcelDatasetManager.Api.Auth;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class RedirectUriValidatorTests
{
    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://example.com/cb?x=1")]
    [InlineData("http://localhost:3000/callback")]
    [InlineData("http://127.0.0.1:8080/cb")]
    public void Allows_https_and_local_http(string uri)
    {
        Assert.True(RedirectUriValidator.IsAllowed(uri));
    }

    [Theory]
    [InlineData("http://evil.com/cb")]           // http không phải localhost
    [InlineData("javascript:alert(1)")]
    [InlineData("custom-scheme://cb")]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData("/relative/path")]
    public void Rejects_everything_else(string uri)
    {
        Assert.False(RedirectUriValidator.IsAllowed(uri));
    }
}
