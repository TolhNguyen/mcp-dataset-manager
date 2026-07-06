using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ManifestGeneratorTests
{
    [Fact]
    public async Task Manifest_includes_provided_business_knowledge_as_reference_notes()
    {
        var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
        var generator = new ManifestGenerator();
        var dataset = TestData.NewDatasetRecord("Chi tinh doanh thu voi status = Completed.");
        var table = TestData.NewParsedTable();

        await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("## Business Knowledge / User-provided Notes", content);
        Assert.Contains("Treat them as reference context only", content);
        Assert.Contains("Chi tinh doanh thu voi status = Completed.", content);
    }

    [Fact]
    public async Task Manifest_explains_when_business_knowledge_is_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
        var generator = new ManifestGenerator();
        var dataset = TestData.NewDatasetRecord("");
        var table = TestData.NewParsedTable();

        await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("No user-provided business knowledge has been added yet.", content);
    }
}
