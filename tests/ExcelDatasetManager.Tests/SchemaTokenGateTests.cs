using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class SchemaTokenGateTests
{
    [Fact]
    public void Missing_token_builds_context_required()
    {
        var err = SchemaTokenGate.BuildGateError(provided: null, expected: "st_abc123def456", datasetId: Guid.NewGuid());
        var json = System.Text.Json.JsonSerializer.Serialize(err);
        Assert.Contains("CONTEXT_REQUIRED", json);
        Assert.Contains("assistant_instruction", json);
    }

    [Fact]
    public void Wrong_token_builds_schema_changed()
    {
        var err = SchemaTokenGate.BuildGateError(provided: "st_000000000000", expected: "st_abc123def456", datasetId: Guid.NewGuid());
        var json = System.Text.Json.JsonSerializer.Serialize(err);
        Assert.Contains("SCHEMA_CHANGED", json);
    }

    [Fact]
    public void Matching_token_returns_null()
    {
        var err = SchemaTokenGate.BuildGateError(provided: "st_abc123def456", expected: "st_abc123def456", datasetId: Guid.NewGuid());
        Assert.Null(err);
    }

    // Widget update chỉ bị gate khi caller là PAT VÀ request thật sự viết SQL mới;
    // đổi title/position/chart mà bắt nộp schema_token là over-strict.
    [Theory]
    [InlineData(true, "SELECT 1", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "   ", false)]
    [InlineData(false, "SELECT 1", false)]
    public void ShouldGateWidgetUpdate_only_for_api_key_callers_writing_sql(bool isApiKey, string? sql, bool expected)
    {
        Assert.Equal(expected, SchemaTokenGate.ShouldGateWidgetUpdate(isApiKey, sql));
    }
}

public class WidgetRequestContractTests
{
    private static readonly System.Text.Json.JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void CreateWidgetRequest_binds_schema_token_from_body()
    {
        var req = System.Text.Json.JsonSerializer.Deserialize<ExcelDatasetManager.Api.Models.CreateWidgetRequest>(
            """{"dataset_id":"5f0b6f3a-9a5e-4b7e-8c2d-1e2f3a4b5c6d","sql":"SELECT 1","schema_token":"st_abc123def456"}""",
            SnakeCase);
        Assert.Equal("st_abc123def456", req!.SchemaToken);
    }

    [Fact]
    public void UpdateWidgetRequest_binds_schema_token_from_body()
    {
        var req = System.Text.Json.JsonSerializer.Deserialize<ExcelDatasetManager.Api.Models.UpdateWidgetRequest>(
            """{"sql":"SELECT 1","schema_token":"st_abc123def456"}""",
            SnakeCase);
        Assert.Equal("st_abc123def456", req!.SchemaToken);
    }

    [Fact]
    public void CreateWidgetByDashboardNameRequest_binds_schema_token_from_body()
    {
        var req = System.Text.Json.JsonSerializer.Deserialize<ExcelDatasetManager.Api.Models.CreateWidgetByDashboardNameRequest>(
            """{"dashboard_name":"KPI","sql":"SELECT 1","schema_token":"st_abc123def456"}""",
            SnakeCase);
        Assert.Equal("st_abc123def456", req!.SchemaToken);
    }
}
