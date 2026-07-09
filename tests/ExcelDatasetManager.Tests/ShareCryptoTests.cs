using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ShareCryptoTests
{
    [Fact]
    public void GenerateToken_has_prefix_and_40_hex()
    {
        var t = ShareCrypto.GenerateToken();
        Assert.StartsWith("shr_", t);
        Assert.Matches("^shr_[0-9a-f]{40}$", t);
        Assert.NotEqual(t, ShareCrypto.GenerateToken()); // random
    }

    [Fact]
    public void HashToken_is_stable_and_not_identity()
    {
        var t = ShareCrypto.GenerateToken();
        Assert.Equal(ShareCrypto.HashToken(t), ShareCrypto.HashToken(t));
        Assert.NotEqual(t, ShareCrypto.HashToken(t));
    }

    [Fact]
    public void GeneratePin_is_6_digits()
        => Assert.Matches("^[0-9]{6}$", ShareCrypto.GeneratePin());

    [Fact]
    public void VerifyPin_roundtrip_and_reject_wrong()
    {
        var stored = ShareCrypto.HashPin("482913");
        Assert.True(ShareCrypto.VerifyPin("482913", stored));
        Assert.False(ShareCrypto.VerifyPin("482914", stored));
        Assert.False(ShareCrypto.VerifyPin("", stored));
    }

    [Fact]
    public void HashPin_uses_unique_salt()
        => Assert.NotEqual(ShareCrypto.HashPin("482913"), ShareCrypto.HashPin("482913"));

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(14, 14)]
    [InlineData(365, 90)]
    public void ClampExpiryDays(int? input, int expected)
        => Assert.Equal(expected, ShareCrypto.ClampExpiryDays(input));

    [Theory]
    [InlineData(4, null)]              // chưa chạm bội 5
    [InlineData(5, 15)]                // 15min * 2^0
    [InlineData(10, 30)]               // 15min * 2^1
    [InlineData(15, 60)]
    [InlineData(7, null)]
    public void NextLockout_backoff(int failed, int? minutes)
    {
        var now = new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc);
        var result = ShareCrypto.NextLockout(failed, now);
        if (minutes is null) Assert.Null(result);
        else Assert.Equal(now.AddMinutes(minutes.Value), result);
    }
}
