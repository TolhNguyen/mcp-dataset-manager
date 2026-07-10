using ExcelDatasetManager.Api.Endpoints;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardPageHeadersTests
{
    [Fact]
    public void Csp_sandboxes_without_same_origin()
    {
        Assert.StartsWith("sandbox allow-scripts;", DashboardPageHeaders.Csp);
        Assert.DoesNotContain("allow-same-origin", DashboardPageHeaders.Csp);
    }

    [Fact]
    public void Csp_blocks_network_but_allows_inline_and_cdnjs()
    {
        Assert.Contains("default-src 'none'", DashboardPageHeaders.Csp);
        Assert.Contains("connect-src 'none'", DashboardPageHeaders.Csp);
        Assert.Contains("script-src 'unsafe-inline' https://cdnjs.cloudflare.com", DashboardPageHeaders.Csp);
    }
}
