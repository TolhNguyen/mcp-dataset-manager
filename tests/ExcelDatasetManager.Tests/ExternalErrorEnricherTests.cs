using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ExternalErrorEnricherTests
{
    private static readonly string[] Tables = { "sync_fb_campaigns", "sync_fb_campaigns_days", "sync_tt_campaigns" };
    private static readonly string[] Columns = { "campaign_id", "project_id", "spend" };

    [Fact]
    public void Mssql_invalid_object_lists_tables_and_suggests()
    {
        var d = ExternalErrorEnricher.Enrich("mssql",
            "Invalid object name 'dbo.sync_fb_campaigns_report_days'.", Tables, Columns);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("available_tables", json);
        Assert.Contains("sync_fb_campaigns", json);
    }

    [Fact]
    public void Bigquery_not_found_table_lists_tables()
    {
        var d = ExternalErrorEnricher.Enrich("bigquery",
            "Not found: Table hv-data:ds.sync_xx was not found", Tables, Columns);
        Assert.NotNull(d);
        Assert.Contains("available_tables", System.Text.Json.JsonSerializer.Serialize(d));
    }

    [Fact]
    public void Bigquery_syntax_error_returns_dialect_hint()
    {
        var d = ExternalErrorEnricher.Enrich("bigquery",
            "Syntax error: Unexpected end of script at [62:2]", Tables, Columns);
        var json = System.Text.Json.JsonSerializer.Serialize(d);
        Assert.Contains("dialect", json);
    }

    [Fact]
    public void Unrecognized_message_returns_null()
    {
        var d = ExternalErrorEnricher.Enrich("mysql", "Some unrelated driver error", Tables, Columns);
        Assert.Null(d);
    }
}
