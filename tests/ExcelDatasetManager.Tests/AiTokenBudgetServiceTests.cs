using ExcelDatasetManager.Api.Models;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class AiTokenBudgetServiceTests
{
    [Fact]
    public void Allows_safe_result()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 50, hardMaxTokens: 100);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, null));

        Assert.Equal("safe", decision.Status);
        Assert.False(decision.RequiresConfirmation);
        Assert.False(decision.Blocked);
    }

    [Fact]
    public void Requires_confirmation_above_safe_threshold_then_accepts_confirmation()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 100);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 40) } } };
        var options = new QueryOptions(null, null, null, null, null, null, null, "ai_safe");

        var decision = service.Decide(payload, options);

        Assert.Equal("requires_confirmation", decision.Status);
        Assert.True(decision.RequiresConfirmation);
        Assert.False(decision.Blocked);
        Assert.NotNull(decision.ConfirmationId);

        var confirmed = service.Decide(payload, options with
        {
            AllowLargeResult = true,
            ConfirmationId = decision.ConfirmationId,
            ResponseMode = "raw"
        });

        Assert.Equal("confirmed", confirmed.Status);
        Assert.False(confirmed.RequiresConfirmation);
    }

    [Fact]
    public void Blocks_above_hard_threshold()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 10);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 100) } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "raw"));

        Assert.Equal("blocked", decision.Status);
        Assert.True(decision.Blocked);
        Assert.False(decision.RequiresConfirmation);
    }

    [Fact]
    public void Summary_mode_never_returns_raw_rows()
    {
        var service = TestData.NewAiTokenBudgetService(safeMaxTokens: 1000, hardMaxTokens: 2000);
        var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

        var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "summary"));

        Assert.Equal("summary", decision.Status);
        Assert.False(decision.AllowRaw);
    }
}
