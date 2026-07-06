using System.Net;
using System.Security.Claims;
using ExcelDatasetManager.Api.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class RateLimitPartitionKeyTests
{
    [Fact]
    public void Uses_user_id_when_authenticated()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "33333333-3333-3333-3333-333333333333")],
            authenticationType: "Test"));

        Assert.Equal("33333333-3333-3333-3333-333333333333", RateLimitPartitionKey.For(context));
    }

    [Fact]
    public void Falls_back_to_ip_when_anonymous()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        Assert.Equal("203.0.113.7", RateLimitPartitionKey.For(context));
    }

    [Fact]
    public void Falls_back_to_unknown_when_no_ip()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("unknown", RateLimitPartitionKey.For(context));
    }
}
