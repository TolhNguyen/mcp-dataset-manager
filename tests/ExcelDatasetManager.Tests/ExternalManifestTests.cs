using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ExternalManifestTests
{
    [Fact]
    public void Build_includes_dataset_name_dialect_warning_table_name_columns_and_samples()
    {
        var columns = new List<ExternalColumnInfo>
        {
            new("id", "integer", false),
            new("name", "varchar", true),
        };

        var table = new ExternalManifestTable(
            QueryableName: "public.customers",
            SourceLabel: "public.customers",
            Columns: columns,
            SampleRows:
            [
                new object?[] { 1, "Alice" },
                new object?[] { 2, "Bob" },
            ]);

        var manifest = ExternalManifestBuilder.Build("Customers", ExternalDbProviders.MySql, [table]);

        // Dataset name.
        Assert.Contains("Customers", manifest);

        // Provider + explicit dialect line.
        Assert.Contains("Write MySQL dialect SQL", manifest);

        // LIVE query warning.
        Assert.Contains(
            "Queries run LIVE against the source database — always prefer aggregates and LIMIT",
            manifest);

        // QueryableName marked as the exact SQL name to use.
        Assert.Contains("public.customers", manifest);
        Assert.Contains("use this exact name in SQL", manifest);

        // Columns (name/type/nullable).
        Assert.Contains("id", manifest);
        Assert.Contains("integer", manifest);
        Assert.Contains("name", manifest);
        Assert.Contains("varchar", manifest);

        // Sample rows rendered as an MD table.
        Assert.Contains("Alice", manifest);
        Assert.Contains("Bob", manifest);
    }

    [Fact]
    public void Build_reports_no_samples_when_none_provided()
    {
        var table = new ExternalManifestTable(
            QueryableName: "dbo.orders",
            SourceLabel: "dbo.orders",
            Columns: [new ExternalColumnInfo("order_id", "int", false)],
            SampleRows: []);

        var manifest = ExternalManifestBuilder.Build("Orders", ExternalDbProviders.MsSql, [table]);

        Assert.Contains("Write SQL Server", manifest);
        Assert.Contains("No sample rows available", manifest);
    }
}
