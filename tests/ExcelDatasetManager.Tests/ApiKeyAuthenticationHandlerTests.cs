using System.Security.Claims;
using System.Text.Encodings.Web;
using ExcelDatasetManager.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("hello")]
    [InlineData("Bearer edm_pat_x")]
    [InlineData("edm_datasetstyle")]
    public async Task Rejects_keys_that_are_not_pat_prefixed(string rawKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = rawKey;

        var result = await AuthenticateAsync(context);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Returns_no_result_when_header_is_missing()
    {
        var context = new DefaultHttpContext();

        var result = await AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public void IsApiKeyPrincipal_false_for_jwt_principal_with_no_auth_method_claim()
    {
        var principal = BuildPrincipal(authMethod: null);

        Assert.False(principal.IsApiKeyPrincipal());
    }

    [Fact]
    public void IsApiKeyPrincipal_true_for_user_api_key_pat()
    {
        var principal = BuildPrincipal(authMethod: "user_api_key");

        Assert.True(principal.IsApiKeyPrincipal());
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        var handler = new ApiKeyAuthenticationHandler(
            new OptionsMonitorStub(new ApiKeyAuthenticationOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            dataSource: null!); // These cases fail before the handler touches the DB.

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationOptions.SchemeName,
            displayName: null,
            handlerType: typeof(ApiKeyAuthenticationHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static ClaimsPrincipal BuildPrincipal(string? authMethod)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };

        if (authMethod is not null)
        {
            claims.Add(new Claim(ClaimsPrincipalExtensions.AuthMethodClaim, authMethod));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private sealed class OptionsMonitorStub(ApiKeyAuthenticationOptions options)
        : IOptionsMonitor<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationOptions CurrentValue => options;
        public ApiKeyAuthenticationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<ApiKeyAuthenticationOptions, string?> listener) => null!;
    }
}
