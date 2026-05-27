using System.Text;
using System.Text.Json;
using ExcelDatasetManager.Api.Models;

namespace ExcelDatasetManager.Api.Services;

public class ManifestGenerator
{
    public async Task GenerateAsync(
        string path,
        DatasetRecord dataset,
        IReadOnlyList<ParsedTable> tables,
        IReadOnlyList<string> warnings,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Dataset Manifest");
        sb.AppendLine();
        sb.AppendLine($"Dataset ID: {dataset.Id}");
        sb.AppendLine($"Dataset Name: {dataset.Name}");
        sb.AppendLine($"Original File: {dataset.OriginalFileName}");
        sb.AppendLine($"File Type: {dataset.FileType}");
        sb.AppendLine($"Created At: {dataset.CreatedAt:O}");
        sb.AppendLine();

        sb.AppendLine("## Query Endpoint");
        sb.AppendLine();
        sb.AppendLine($"`POST /api/datasets/{dataset.Id}/query`");
        sb.AppendLine();
        sb.AppendLine("Request body:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("""
        {
          "query_type": "sql",
          "sql": "SELECT ... FROM raw_xxx LIMIT 100",
          "options": { "max_rows": 100, "return_format": "compact", "include_sql": true }
        }
        """);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## Query Rules");
        sb.AppendLine();
        sb.AppendLine("- Use SELECT (or WITH) queries only.");
        sb.AppendLine("- Use the normalized table names and normalized column names exactly as listed below.");
        sb.AppendLine("- Do not use original Vietnamese headers directly in SQL — they are for human reference only.");
        sb.AppendLine("- Do not use `SELECT *` unless a `LIMIT` is provided; the server enforces a default limit of 100 rows.");
        sb.AppendLine("- For aggregate analysis prefer `GROUP BY` + `SUM` / `AVG` / `COUNT` over scanning raw rows.");
        sb.AppendLine("- When a user asks in Vietnamese, use the Column Mapping table to translate terms into SQL columns.");
        sb.AppendLine();

        sb.AppendLine("## Tables");
        sb.AppendLine();

        foreach (var table in tables)
        {
            sb.AppendLine($"### `{table.TableName}`");
            sb.AppendLine();
            sb.AppendLine($"- Source: `{table.SourceName}`");
            sb.AppendLine($"- Source type: `{table.SourceType}`");
            sb.AppendLine($"- Query table name: `{table.TableName}`");
            sb.AppendLine($"- Rows: {table.RowCount:N0}");
            sb.AppendLine($"- Columns: {table.Columns.Count}");
            sb.AppendLine();

            sb.AppendLine("#### Column Mapping");
            sb.AppendLine();
            sb.AppendLine("| # | Original Header | Query Column | Type | Semantic | Nulls | Distinct | Example values |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");

            foreach (var col in table.Columns)
            {
                var original = EscapeMd(col.OriginalHeader ?? "");
                var samples = col.SampleValues.Length == 0
                    ? ""
                    : string.Join(", ", col.SampleValues.Take(3).Select(v => EscapeMd(Truncate(v, 40))));
                if (col.SampleValues.Length > 3) samples += ", …";

                var distinctText = col.DistinctCapped ? $"{col.DistinctCount}+" : col.DistinctCount.ToString();

                sb.AppendLine($"| {col.Ordinal} | {original} | `{col.NormalizedName}` | {col.InferredType} | {col.SemanticType ?? ""} | {col.NullCount} | {distinctText} | {samples} |");
            }

            sb.AppendLine();
            sb.AppendLine("#### Aliases (Vietnamese / business terms)");
            sb.AppendLine();
            foreach (var col in table.Columns)
            {
                var aliases = col.Aliases
                    .Where(a => !string.Equals(a, col.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    .Select(EscapeMd);
                var joined = string.Join(", ", aliases);
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    sb.AppendLine($"- `{col.NormalizedName}` ← {joined}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Natural Language Query Guide");
        sb.AppendLine();
        sb.AppendLine("When the user asks in Vietnamese, map common terms as follows:");
        sb.AppendLine();
        foreach (var table in tables)
        {
            foreach (var col in table.Columns)
            {
                var alias = col.Aliases.FirstOrDefault(a =>
                    !string.Equals(a, col.NormalizedName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(a, col.NormalizedName.Replace('_', ' '), StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(alias))
                {
                    sb.AppendLine($"- \"{EscapeMd(alias)}\" → `{table.TableName}.{col.NormalizedName}`");
                }
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Warnings");
        sb.AppendLine();
        if (warnings.Count == 0)
        {
            sb.AppendLine("- No parser warnings.");
        }
        else
        {
            foreach (var w in warnings)
            {
                sb.AppendLine($"- {EscapeMd(w)}");
            }
        }
        sb.AppendLine();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
    }

    private static string EscapeMd(string value) =>
        value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
