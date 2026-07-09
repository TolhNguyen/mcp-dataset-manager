using ExcelDatasetManager.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ShareSessionProtectorTests
{
    private static ShareSessionProtector Create()
        => new(DataProtectionProvider.Create("edm-tests"));

    [Fact]
    public void Roundtrip_returns_share_id()
    {
        var p = Create();
        var id = Guid.NewGuid();
        Assert.Equal(id, p.TryUnprotect(p.Protect(id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Invalid_value_returns_null(string? value)
        => Assert.Null(Create().TryUnprotect(value));

    [Fact]
    public void Tampered_value_returns_null()
    {
        var p = Create();
        var cookie = p.Protect(Guid.NewGuid());
        Assert.Null(p.TryUnprotect(cookie[..^2] + "zz"));
    }

    [Fact]
    public void Different_key_ring_returns_null()
    {
        var cookie = Create().Protect(Guid.NewGuid());
        var other = new ShareSessionProtector(DataProtectionProvider.Create("khac"));
        Assert.Null(other.TryUnprotect(cookie));
    }
}
