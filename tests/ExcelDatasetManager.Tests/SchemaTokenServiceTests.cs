using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SchemaTokenServiceTests
{
    private static (string, IReadOnlyList<(string, string)>) T(string name, params (string, string)[] cols)
        => (name, cols);

    [Fact]
    public void Compute_is_stable_for_same_schema()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        Assert.Equal(a, b);
        Assert.StartsWith("st_", a);
        Assert.Equal(15, a.Length);
    }

    [Fact]
    public void Compute_is_order_independent_across_tables()
    {
        var a = SchemaTokenService.Compute(new[] { T("a", ("x", "INT")), T("b", ("y", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("b", ("y", "INT")), T("a", ("x", "INT")) });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_changes_when_column_added()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT"), ("amount", "DECIMAL")) });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_changes_when_type_changes()
    {
        var a = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        var b = SchemaTokenService.Compute(new[] { T("orders", ("id", "BIGINT")) });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Matches_true_only_for_equal_token()
    {
        var t = SchemaTokenService.Compute(new[] { T("orders", ("id", "INT")) });
        Assert.True(SchemaTokenService.Matches(t, t));
        Assert.True(SchemaTokenService.Matches("  " + t + " ", t));
        Assert.False(SchemaTokenService.Matches("st_000000000000", t));
        Assert.False(SchemaTokenService.Matches(null, t));
        Assert.False(SchemaTokenService.Matches("", t));
    }
}
