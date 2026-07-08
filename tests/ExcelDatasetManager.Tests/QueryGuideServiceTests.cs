using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryGuideServiceTests
{
    [Fact]
    public void Guide_token_matches_content_and_is_stable()
    {
        var svc = new QueryGuideService(storageDir: null);
        var (token, content) = svc.GetGuide();
        Assert.StartsWith("gd_", token);
        Assert.Equal(15, token.Length);
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains(token, content);
        Assert.Equal(token, svc.CurrentToken());

        var (token2, _) = svc.GetGuide();
        Assert.Equal(token, token2);
    }

    [Fact]
    public void File_override_changes_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), "edm-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "query-guide.md"), "# Custom guide\nHello.");
        try
        {
            var svc = new QueryGuideService(storageDir: dir);
            var (token, content) = svc.GetGuide();
            Assert.Contains("Custom guide", content);

            var svcDefault = new QueryGuideService(storageDir: null);
            Assert.NotEqual(svcDefault.CurrentToken(), token);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
