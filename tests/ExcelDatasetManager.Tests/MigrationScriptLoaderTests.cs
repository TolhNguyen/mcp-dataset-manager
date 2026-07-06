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

    [Fact]
    public void Loads_external_connections_migration()
    {
        var scripts = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly);
        var m3 = scripts.Single(s => s.Version == 3);
        Assert.Contains("CREATE TABLE IF NOT EXISTS db_connections", m3.Sql);
        Assert.Contains("max_datasets", m3.Sql);
    }

    [Fact]
    public void Loads_knowledge_migration()
    {
        var m4 = MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly).Single(s => s.Version == 4);
        Assert.Contains("dataset_knowledge_entries", m4.Sql);
        Assert.Contains("can_write", m4.Sql);
        Assert.Contains("DROP COLUMN IF EXISTS business_knowledge", m4.Sql);
    }
}
