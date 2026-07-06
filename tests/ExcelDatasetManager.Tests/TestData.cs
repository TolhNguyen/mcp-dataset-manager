using ExcelDatasetManager.Api.Models;
using ExcelDatasetManager.Api.Services;
using Microsoft.Extensions.Configuration;

namespace ExcelDatasetManager.Tests;

internal static class TestData
{
    public static AiTokenBudgetService NewAiTokenBudgetService(int safeMaxTokens, int hardMaxTokens)
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

    public static DatasetRecord NewDatasetRecord(string businessKnowledge) => new(
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
        BusinessKnowledgeUpdatedAt: DateTime.UtcNow,
        SourceKind: "file",
        ConnectionId: null);

    public static ParsedTable NewParsedTable() => new(
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
}
