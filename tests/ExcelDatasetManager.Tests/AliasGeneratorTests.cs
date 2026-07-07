using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class AliasGeneratorTests
{
    [Theory]
    [InlineData("Doanh Thu 2026", "doanh_thu_2026")]
    [InlineData("orders", "orders")]
    [InlineData("  Sales Report  ", "sales_report")]
    [InlineData("A/B  C", "a_b_c")]
    [InlineData("___weird___", "weird")]
    public void Slugify_produces_lowercase_underscore_slug(string name, string expected)
    {
        Assert.Equal(expected, AliasGenerator.Slugify(name));
    }

    [Fact]
    public void Slugify_folds_common_vietnamese_accents()
    {
        // Accented chars fold to ASCII so the slug is stable and readable.
        var slug = AliasGenerator.Slugify("Đơn Hàng Tháng");
        Assert.Equal("don_hang_thang", slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("日本語")]
    public void Slugify_falls_back_to_ds_when_empty(string name)
    {
        Assert.Equal("ds", AliasGenerator.Slugify(name));
    }

    [Fact]
    public void Slugify_caps_length()
    {
        var slug = AliasGenerator.Slugify(new string('a', 200));
        Assert.True(slug.Length <= 55, $"slug length {slug.Length} should be <= 55");
    }

    [Fact]
    public void MakeUnique_returns_base_when_no_collision()
    {
        Assert.Equal("orders", AliasGenerator.MakeUnique("orders", new HashSet<string>()));
    }

    [Fact]
    public void MakeUnique_suffixes_on_collision()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "orders" };
        Assert.Equal("orders_2", AliasGenerator.MakeUnique("orders", existing));
    }

    [Fact]
    public void MakeUnique_increments_until_free()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "orders", "orders_2", "orders_3" };
        Assert.Equal("orders_4", AliasGenerator.MakeUnique("orders", existing));
    }
}
