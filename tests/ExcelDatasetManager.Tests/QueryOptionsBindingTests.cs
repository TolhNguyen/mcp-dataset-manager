using System.Text.Json;
using ExcelDatasetManager.Api.Models;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryOptionsBindingTests
{
    private static JsonSerializerOptions AppJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Deserializing_request_body_ignores_bypass_ai_budget_but_binds_other_fields()
    {
        const string json = """{"bypass_ai_budget":true,"max_rows":50}""";

        var options = JsonSerializer.Deserialize<QueryOptions>(json, AppJsonOptions());

        Assert.NotNull(options);
        Assert.Null(options!.BypassAiBudget);
        Assert.Equal(50, options.MaxRows);
    }

    [Fact]
    public void Code_constructed_query_options_can_still_set_bypass_ai_budget()
    {
        var options = new QueryOptions(
            MaxRows: 1000, ReturnFormat: null, IncludeSql: null, IncludeProfile: null,
            MaxTokens: null, AllowLargeResult: null, ConfirmationId: null, ResponseMode: null,
            BypassAiBudget: true);

        Assert.True(options.BypassAiBudget);
    }
}
