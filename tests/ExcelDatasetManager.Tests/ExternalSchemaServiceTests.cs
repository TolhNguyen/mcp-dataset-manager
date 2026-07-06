using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ExternalSchemaServiceTests
{
    [Fact]
    public void DedupTables_removes_duplicates_preserving_first_seen_order()
    {
        var result = ExternalSchemaService.DedupTables(["public.orders", "public.customers", "public.orders"]);

        Assert.Equal(["public.orders", "public.customers"], result);
    }

    [Fact]
    public void DedupTables_is_case_sensitive_ordinal_comparison()
    {
        var result = ExternalSchemaService.DedupTables(["public.Orders", "public.orders"]);

        Assert.Equal(["public.Orders", "public.orders"], result);
    }

    [Fact]
    public void DedupTables_returns_empty_for_empty_input()
    {
        var result = ExternalSchemaService.DedupTables([]);

        Assert.Empty(result);
    }
}
