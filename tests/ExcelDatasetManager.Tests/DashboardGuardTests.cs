using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DashboardGuardTests
{
    // ---------- ValidateCreate ----------

    [Fact]
    public void ValidateCreate_accepts_valid_widget()
    {
        var error = DashboardGuard.ValidateCreate("Revenue by month", "SELECT * FROM sales", "line");
        Assert.Null(error);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("line")]
    [InlineData("bar")]
    [InlineData("pie")]
    [InlineData("stat")]
    public void ValidateCreate_accepts_all_known_chart_types(string chartType)
    {
        var error = DashboardGuard.ValidateCreate("Title", "SELECT 1", chartType);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_rejects_chart_type_not_in_set()
    {
        var error = DashboardGuard.ValidateCreate("Title", "SELECT 1", "scatter");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_null_chart_type()
    {
        var error = DashboardGuard.ValidateCreate("Title", "SELECT 1", null);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_empty_title()
    {
        var error = DashboardGuard.ValidateCreate("", "SELECT 1", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_whitespace_only_title()
    {
        var error = DashboardGuard.ValidateCreate("   ", "SELECT 1", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_null_title()
    {
        var error = DashboardGuard.ValidateCreate(null, "SELECT 1", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_title_over_255_chars()
    {
        var title = new string('a', 256);
        var error = DashboardGuard.ValidateCreate(title, "SELECT 1", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_accepts_title_exactly_255_chars()
    {
        var title = new string('a', 255);
        var error = DashboardGuard.ValidateCreate(title, "SELECT 1", "table");
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_rejects_empty_sql()
    {
        var error = DashboardGuard.ValidateCreate("Title", "", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_whitespace_only_sql()
    {
        var error = DashboardGuard.ValidateCreate("Title", "   ", "table");
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateCreate_rejects_null_sql()
    {
        var error = DashboardGuard.ValidateCreate("Title", null, "table");
        Assert.NotNull(error);
    }

    // ---------- ClampRefresh ----------

    [Fact]
    public void ClampRefresh_null_defaults_to_60()
    {
        Assert.Equal(60, DashboardGuard.ClampRefresh(null));
    }

    [Fact]
    public void ClampRefresh_below_minimum_clamps_to_30()
    {
        Assert.Equal(30, DashboardGuard.ClampRefresh(5));
    }

    [Fact]
    public void ClampRefresh_above_minimum_passes_through()
    {
        Assert.Equal(120, DashboardGuard.ClampRefresh(120));
    }
}
