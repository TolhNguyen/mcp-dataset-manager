using Xunit;

namespace ExcelDatasetManager.Tests;

public class ConnectionsWizardStaticTests
{
    [Fact]
    public void CreateDatasetWizard_has_search_select_all_and_fixed_shell()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "connections.html"));
        var script = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "js", "connections.js"));
        var css = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "css", "style.css"));

        Assert.Contains("wizard-modal", html);
        Assert.Contains("wizard-modal-body", html);
        Assert.Contains("wizard-modal-footer", html);
        Assert.Contains("wizardTableSearchInput", html);
        Assert.Contains("wizardSelectVisibleTablesInput", html);
        Assert.Contains("wizardTableSelectionSummary", html);

        Assert.Contains("wizardTables:", script);
        Assert.Contains("wizardTableFilter:", script);
        Assert.Contains("filterWizardTables", script);
        Assert.Contains("toggleVisibleWizardTables", script);
        Assert.Contains("updateWizardTableSelectionSummary", script);

        Assert.Contains(".wizard-modal", css);
        Assert.Contains("max-height: calc(100vh - 32px)", css);
        Assert.Contains(".wizard-modal-body", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains(".wizard-table-list", css);
    }

    [Fact]
    public void AllModals_keep_header_and_footer_visible_with_only_body_scrolling()
    {
        var root = FindRepositoryRoot();
        var connectionsHtml = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "connections.html"));
        var dashboardsHtml = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "dashboards.html"));
        var css = File.ReadAllText(Path.Combine(root, "api", "wwwroot", "css", "style.css"));

        Assert.Equal(4, Count(connectionsHtml + dashboardsHtml, "class=\"modal-overlay\""));
        Assert.Equal(4, Count(connectionsHtml + dashboardsHtml, "class=\"modal-body\"") +
                        Count(connectionsHtml + dashboardsHtml, "class=\"wizard-modal-body\""));
        Assert.Equal(4, Count(connectionsHtml + dashboardsHtml, "modal-footer"));

        Assert.Contains(".modal {", css);
        Assert.Contains("max-height: calc(100vh - 32px)", css);
        Assert.Contains("overflow: hidden", css);
        Assert.Contains(".modal-body", css);
        Assert.Contains("overflow-y: auto", css);
        Assert.Contains(".modal-footer", css);
        Assert.Contains("flex: 0 0 auto", css);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "api", "wwwroot")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
