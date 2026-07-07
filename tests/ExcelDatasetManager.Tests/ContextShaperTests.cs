using System.Text.Json;
using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ContextShaperTests
{
    private static int Estimate(object o) =>
        Math.Max(1, JsonSerializer.Serialize(o).Length / 4);

    private static ContextDatasetInput Sample(int knowledgeCount = 2) => new(
        DatasetId: Guid.NewGuid(),
        Name: "Sales",
        Alias: "sales",
        SourceKind: "external_db",
        Provider: "mysql",
        Dialect: "mysql",
        Tables:
        [
            new ContextTableInput(
                TableName: "orders",
                QualifiedName: "sales.orders",
                Columns: [new ContextColumnInput("order_id", "VARCHAR", "Mã đơn", ["ma don"])],
                SampleRows: [new object?[] { "ORD-001", 1200000 }]),
            new ContextTableInput(
                TableName: "customers",
                QualifiedName: "sales.customers",
                Columns: [new ContextColumnInput("name", "VARCHAR", "Tên", [])],
                SampleRows: [new object?[] { "Anh Ba" }])
        ],
        Knowledge:
        [
            new ContextKnowledgeInput("metric_definition", "Doanh thu", "Sau chiết khấu", "ai", true),
            new ContextKnowledgeInput("note", "Ghi chú", "Nội dung", "user", false)
        ],
        ActiveKnowledgeCount: knowledgeCount);

    private static JsonElement ToJson(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    [Fact]
    public void Full_includes_sample_rows_and_aliases()
    {
        var res = ContextShaper.Shape([Sample()], null, "full", safeMaxTokens: 100000, Estimate);
        Assert.False(res.Downgraded);
        var t0 = ToJson(res.Payload).GetProperty("datasets")[0].GetProperty("tables")[0];
        Assert.Equal(JsonValueKind.Array, t0.GetProperty("sample_rows").ValueKind);
        Assert.Equal(JsonValueKind.Array, t0.GetProperty("columns")[0].GetProperty("aliases").ValueKind);
    }

    [Fact]
    public void Summary_omits_sample_rows_and_aliases()
    {
        var res = ContextShaper.Shape([Sample()], null, "summary", safeMaxTokens: 100000, Estimate);
        var t0 = ToJson(res.Payload).GetProperty("datasets")[0].GetProperty("tables")[0];
        Assert.Equal(JsonValueKind.Null, t0.GetProperty("sample_rows").ValueKind);
        Assert.False(t0.GetProperty("columns")[0].TryGetProperty("aliases", out _));
    }

    [Fact]
    public void Summary_keeps_only_pinned_knowledge()
    {
        var res = ContextShaper.Shape([Sample()], null, "summary", safeMaxTokens: 100000, Estimate);
        var knowledge = ToJson(res.Payload).GetProperty("datasets")[0].GetProperty("knowledge");
        Assert.Equal(1, knowledge.GetArrayLength());
        Assert.True(knowledge[0].GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public void Oversized_full_downgrades_to_summary_with_warning()
    {
        var res = ContextShaper.Shape([Sample()], null, "full", safeMaxTokens: 1, Estimate);
        Assert.True(res.Downgraded);
        var root = ToJson(res.Payload);
        Assert.Equal(JsonValueKind.String, root.GetProperty("warning").ValueKind);
        var t0 = root.GetProperty("datasets")[0].GetProperty("tables")[0];
        Assert.Equal(JsonValueKind.Null, t0.GetProperty("sample_rows").ValueKind); // now summary
    }

    [Fact]
    public void Tables_filter_drops_other_tables()
    {
        var filter = new HashSet<string> { "orders" };
        var res = ContextShaper.Shape([Sample()], filter, "full", safeMaxTokens: 100000, Estimate);
        var tables = ToJson(res.Payload).GetProperty("datasets")[0].GetProperty("tables");
        Assert.Equal(1, tables.GetArrayLength());
        Assert.Equal("orders", tables[0].GetProperty("table_name").GetString());
    }

    [Fact]
    public void Memory_instructions_report_total_active_count()
    {
        var res = ContextShaper.Shape([Sample(knowledgeCount: 12)], null, "full", safeMaxTokens: 100000, Estimate);
        var mi = ToJson(res.Payload).GetProperty("memory_instructions").GetString();
        Assert.Contains("12 entries", mi);
    }

    [Fact]
    public void Qualified_name_is_alias_dot_table()
    {
        var res = ContextShaper.Shape([Sample()], null, "full", safeMaxTokens: 100000, Estimate);
        var t0 = ToJson(res.Payload).GetProperty("datasets")[0].GetProperty("tables")[0];
        Assert.Equal("sales.orders", t0.GetProperty("qualified_name").GetString());
    }

    [Fact]
    public void Token_estimate_present_and_positive()
    {
        var res = ContextShaper.Shape([Sample()], null, "full", safeMaxTokens: 100000, Estimate);
        var te = ToJson(res.Payload).GetProperty("token_estimate").GetInt32();
        Assert.True(te > 0);
        Assert.Equal(res.TokenEstimate, te);
    }
}
