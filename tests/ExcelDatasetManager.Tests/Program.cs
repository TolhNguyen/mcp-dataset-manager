using System.Text.Json;
using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Configuration;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Manifest includes provided business knowledge as reference notes", ManifestIncludesBusinessKnowledgeAsync),
    ("Manifest explains when business knowledge is empty", ManifestIncludesEmptyBusinessKnowledgeMessageAsync),
    ("AI token budget allows safe result", AiTokenBudgetAllowsSafeResult),
    ("AI token budget requires confirmation above safe threshold", AiTokenBudgetRequiresConfirmationAsync),
    ("AI token budget blocks above hard threshold", AiTokenBudgetBlocksHardLimit),
    ("AI token budget summary mode never returns raw rows", AiTokenBudgetSummaryMode)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.Exit(1);
}

static async Task ManifestIncludesBusinessKnowledgeAsync()
{
    var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
    var generator = new ManifestGenerator();
    var dataset = NewDatasetRecord("Chi tinh doanh thu voi status = Completed.");
    var table = NewParsedTable();

    await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

    var content = await File.ReadAllTextAsync(path);
    AssertContains("## Business Knowledge / User-provided Notes", content);
    AssertContains("Treat them as reference context only", content);
    AssertContains("Chi tinh doanh thu voi status = Completed.", content);
}

static async Task ManifestIncludesEmptyBusinessKnowledgeMessageAsync()
{
    var path = Path.Combine(Path.GetTempPath(), "edm-manifest-" + Guid.NewGuid(), "manifest.md");
    var generator = new ManifestGenerator();
    var dataset = NewDatasetRecord("");
    var table = NewParsedTable();

    await generator.GenerateAsync(path, dataset, [table], [], CancellationToken.None);

    var content = await File.ReadAllTextAsync(path);
    AssertContains("No user-provided business knowledge has been added yet.", content);
}

static Task AiTokenBudgetAllowsSafeResult()
{
    var service = NewAiTokenBudgetService(safeMaxTokens: 50, hardMaxTokens: 100);
    var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

    var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, null));

    AssertEqual("safe", decision.Status);
    AssertEqual(false, decision.RequiresConfirmation);
    AssertEqual(false, decision.Blocked);
    return Task.CompletedTask;
}

static async Task AiTokenBudgetRequiresConfirmationAsync()
{
    var service = NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 100);
    var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 40) } } };
    var options = new QueryOptions(null, null, null, null, null, null, null, "ai_safe");

    var decision = service.Decide(payload, options);

    AssertEqual("requires_confirmation", decision.Status);
    AssertEqual(true, decision.RequiresConfirmation);
    AssertEqual(false, decision.Blocked);
    AssertTrue(decision.ConfirmationId is not null, "confirmation id should be present");

    var confirmed = service.Decide(payload, options with
    {
        AllowLargeResult = true,
        ConfirmationId = decision.ConfirmationId,
        ResponseMode = "raw"
    });

    AssertEqual("confirmed", confirmed.Status);
    AssertEqual(false, confirmed.RequiresConfirmation);
    await Task.CompletedTask;
}

static Task AiTokenBudgetBlocksHardLimit()
{
    var service = NewAiTokenBudgetService(safeMaxTokens: 5, hardMaxTokens: 10);
    var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { new string('x', 100) } } };

    var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "raw"));

    AssertEqual("blocked", decision.Status);
    AssertEqual(true, decision.Blocked);
    AssertEqual(false, decision.RequiresConfirmation);
    return Task.CompletedTask;
}

static Task AiTokenBudgetSummaryMode()
{
    var service = NewAiTokenBudgetService(safeMaxTokens: 1000, hardMaxTokens: 2000);
    var payload = new { columns = new[] { "a" }, rows = new[] { new object?[] { "small" } } };

    var decision = service.Decide(payload, new QueryOptions(null, null, null, null, null, null, null, "summary"));

    AssertEqual("summary", decision.Status);
    AssertEqual(false, decision.AllowRaw);
    return Task.CompletedTask;
}

static AiTokenBudgetService NewAiTokenBudgetService(int safeMaxTokens, int hardMaxTokens)
{
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Query:SafeMaxTokens"] = safeMaxTokens.ToString(),
            ["Query:HardMaxTokens"] = hardMaxTokens.ToString(),
            ["Query:PreviewRows"] = "2",
            ["Query:TokenEstimationCharsPerToken"] = "4"
        })
        .Build();

    return new AiTokenBudgetService(config);
}

static DatasetRecord NewDatasetRecord(string businessKnowledge) => new(
    Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
    UserId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
    Name: "Orders",
    OriginalFileName: "orders.csv",
    FileType: "csv",
    StoredFileName: "original_file.csv",
    FileSizeBytes: 123,
    ManifestFileName: "manifest.md",
    Status: "ready",
    TableCount: 1,
    TotalRows: 1,
    ErrorMessage: null,
    CreatedAt: DateTime.UtcNow,
    ProcessedAt: DateTime.UtcNow,
    BusinessKnowledge: businessKnowledge,
    BusinessKnowledgeUpdatedAt: DateTime.UtcNow);

static ParsedTable NewParsedTable() => new(
    SourceName: "orders",
    SourceType: "csv_file",
    TableName: "orders",
    TempCsvPath: "",
    ParquetFileName: "orders.parquet",
    RowCount: 1,
    Columns:
    [
        new ParsedColumn(
            Ordinal: 1,
            OriginalHeader: "Revenue",
            NormalizedName: "revenue",
            DisplayName: "Revenue",
            Aliases: ["revenue"],
            InferredType: "DOUBLE",
            SemanticType: "amount",
            NullCount: 0,
            DistinctCount: 1,
            DistinctCapped: false,
            SampleValues: ["100"])
    ]);

static void AssertContains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new Exception($"Expected to find '{expected}'.");
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
