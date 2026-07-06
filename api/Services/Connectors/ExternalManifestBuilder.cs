using System.Text;

namespace ExcelDatasetManager.Api.Services.Connectors;

/// <summary>Everything the manifest needs for one table: the exact name to use in SQL, its columns,
/// and up to a couple of sample rows fetched live from the source database.</summary>
public sealed record ExternalManifestTable(
    string QueryableName,
    string SourceLabel,
    IReadOnlyList<ExternalColumnInfo> Columns,
    IReadOnlyList<object?[]> SampleRows);

/// <summary>
/// Builds the manifest.md content for an external (live-query) dataset. Pure and static so it is
/// trivially unit-testable without a database or network connection.
/// </summary>
public static class ExternalManifestBuilder
{
    private static readonly Dictionary<string, string> DialectNames = new()
    {
        [ExternalDbProviders.PostgreSql] = "PostgreSQL",
        [ExternalDbProviders.MySql] = "MySQL",
        [ExternalDbProviders.MsSql] = "SQL Server (T-SQL)",
        [ExternalDbProviders.BigQuery] = "BigQuery Standard SQL",
    };

    public static string Build(string datasetName, string provider, IReadOnlyList<ExternalManifestTable> tables)
    {
        var dialect = DialectNames.TryGetValue(provider, out var name) ? name : provider;
        var sb = new StringBuilder();

        sb.AppendLine("# External Dataset Manifest");
        sb.AppendLine();
        sb.AppendLine($"Dataset Name: {datasetName}");
        sb.AppendLine($"Provider: {provider}");
        sb.AppendLine($"Dialect: Write {dialect} dialect SQL.");
        sb.AppendLine();

        sb.AppendLine("## Warning");
        sb.AppendLine();
        sb.AppendLine("Queries run LIVE against the source database — always prefer aggregates and LIMIT.");
        sb.AppendLine();

        sb.AppendLine("## Tables");
        sb.AppendLine();

        foreach (var table in tables)
        {
            sb.AppendLine($"### `{table.QueryableName}` (use this exact name in SQL)");
            sb.AppendLine();
            sb.AppendLine($"- Source: `{table.SourceLabel}`");
            sb.AppendLine($"- Columns: {table.Columns.Count}");
            sb.AppendLine();

            sb.AppendLine("#### Columns");
            sb.AppendLine();
            sb.AppendLine("| Column | Type | Nullable |");
            sb.AppendLine("|---|---|---|");
            foreach (var col in table.Columns)
            {
                sb.AppendLine($"| `{EscapeMd(col.Name)}` | {EscapeMd(col.DataType)} | {(col.IsNullable ? "yes" : "no")} |");
            }
            sb.AppendLine();

            sb.AppendLine("#### Sample rows (up to 2)");
            sb.AppendLine();
            if (table.SampleRows.Count == 0)
            {
                sb.AppendLine("_No sample rows available._");
            }
            else
            {
                sb.AppendLine("| " + string.Join(" | ", table.Columns.Select(c => EscapeMd(c.Name))) + " |");
                sb.AppendLine("|" + string.Join("|", table.Columns.Select(_ => "---")) + "|");
                foreach (var row in table.SampleRows.Take(2))
                {
                    var cells = row.Select(v => EscapeMd(v?.ToString() ?? ""));
                    sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeMd(string value) =>
        value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");
}
