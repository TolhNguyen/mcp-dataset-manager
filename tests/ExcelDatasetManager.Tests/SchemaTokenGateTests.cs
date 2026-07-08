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
}
