using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardPageGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateHtml_rejects_missing(string? html)
        => Assert.Equal("html is required.", DashboardPageGuard.ValidateHtml(html));

    [Fact]
    public void ValidateHtml_accepts_small_page()
        => Assert.Null(DashboardPageGuard.ValidateHtml("<h1>ok</h1><script>parent.postMessage({type:'edm:ready'},'*')</script>"));

    [Fact]
    public void ValidateHtml_accepts_exactly_at_cap()
        => Assert.Null(DashboardPageGuard.ValidateHtml(new string('a', DashboardPageGuard.MaxHtmlBytes)));

    [Fact]
    public void ValidateHtml_rejects_over_cap()
        => Assert.NotNull(DashboardPageGuard.ValidateHtml(new string('a', DashboardPageGuard.MaxHtmlBytes + 1)));

    [Fact]
    public void ValidateHtml_counts_utf8_bytes_not_chars()
    {
        // 'ă' = 2 bytes UTF-8: nửa cap + 1 ký tự 2-byte là vượt cap dù char count < cap.
        var html = new string('ă', DashboardPageGuard.MaxHtmlBytes / 2) + "x";
        Assert.NotNull(DashboardPageGuard.ValidateHtml(html));
    }
}
