using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class MigrationScriptLoaderTests
{
    [Fact]
    public void Loads_baseline_migration_from_assembly()
    {
        var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);

        Assert.NotEmpty(scripts);
        Assert.Equal(1, scripts[0].Version);
        Assert.Equal("0001_baseline.sql", scripts[0].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS users", scripts[0].Sql);
    }

    [Fact]
    public void Scripts_are_ordered_by_version_without_duplicates()
    {
        var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);

        var versions = scripts.Select(s => s.Version).ToList();
        Assert.Equal(versions.OrderBy(v => v).ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }
}
