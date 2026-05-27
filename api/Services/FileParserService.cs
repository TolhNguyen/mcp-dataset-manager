using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Streaming parser for .xlsx/.xls/.xlsm/.csv/.tsv.
/// Emits one intermediate CSV per table (with normalized headers and UTF-8 BOM)
/// plus column statistics. The CSV is then handed to <see cref="ParquetWriter"/>.
/// </summary>
public class FileParserService(HeaderNormalizer normalizer, FileStorageService storage, ILogger<FileParserService> logger)
{
    private static readonly string[] ExcelExtensions = [".xlsx", ".xls", ".xlsm"];
    private const int MaxSampleRowsForTypeInference = 5_000; // beyond this we stop running type checks per row to save CPU

    public async Task<ParsedDataset> ParseAsync(Guid userId, Guid datasetId, string originalPath, string fileType, CancellationToken cancellationToken)
    {
        // Required by ExcelDataReader for legacy code pages.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var ext = "." + fileType.TrimStart('.').ToLowerInvariant();
        storage.EnsureTempDirectory(userId, datasetId);

        if (ext is ".csv" or ".tsv")
        {
            var table = await ParseDelimitedAsync(userId, datasetId, originalPath, ext, cancellationToken);
            return new ParsedDataset(new List<ParsedTable> { table }, new List<string>());
        }

        if (ExcelExtensions.Contains(ext))
        {
            return await ParseExcelAsync(userId, datasetId, originalPath, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported file type: {fileType}");
    }

    // ============================================================
    // CSV / TSV
    // ============================================================

    private async Task<ParsedTable> ParseDelimitedAsync(Guid userId, Guid datasetId, string path, string ext, CancellationToken ct)
    {
        var sourceName = Path.GetFileNameWithoutExtension(path);
        var tableName = normalizer.NormalizeTableName(sourceName, "file");
        var tempCsv = Path.Combine(storage.GetTempDirectory(userId, datasetId), tableName + ".csv");

        var delimiter = ext == ".tsv" ? '\t' : DetectDelimiter(path);
        var encoding = DetectEncoding(path);

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: true);

        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw new InvalidOperationException("Header row is empty.");
        }

        var headers = SplitDelimitedLine(headerLine, delimiter);
        var normalizedColumns = normalizer.NormalizeColumns(headers);
        var stats = normalizedColumns.Select(c => new ColumnStats(c.Original, c.Normalized)).ToList();

        await using var output = new FileStream(tempCsv, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(',', normalizedColumns.Select(c => EscapeCsv(c.Normalized))));

        long rowCount = 0;
        var collectStats = true;
        var pendingBuffer = new StringBuilder();
        var multilineFields = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            // Handle multiline quoted fields by accumulating until quote count is balanced.
            pendingBuffer.AppendLine(line);
            var aggregated = pendingBuffer.ToString().TrimEnd('\r', '\n');
            if (HasUnbalancedQuotes(aggregated))
            {
                multilineFields = true;
                continue;
            }

            pendingBuffer.Clear();
            var fields = SplitDelimitedLine(aggregated, delimiter);
            await WriteRowAsync(writer, fields, normalizedColumns.Count, stats, collectStats);
            rowCount++;

            if (collectStats && rowCount >= MaxSampleRowsForTypeInference)
            {
                collectStats = false;
            }
        }

        if (pendingBuffer.Length > 0)
        {
            // Trailing partial line — try to flush it as a row.
            var aggregated = pendingBuffer.ToString().TrimEnd('\r', '\n');
            var fields = SplitDelimitedLine(aggregated, delimiter);
            await WriteRowAsync(writer, fields, normalizedColumns.Count, stats, collectStats);
            rowCount++;
        }

        if (multilineFields)
        {
            logger.LogInformation("Multiline quoted fields detected in {File}", path);
        }

        return new ParsedTable(
            SourceName: sourceName,
            SourceType: ext == ".tsv" ? "tsv_file" : "csv_file",
            TableName: tableName,
            TempCsvPath: tempCsv,
            ParquetFileName: tableName + ".parquet",
            RowCount: rowCount,
            Columns: BuildParsedColumns(stats));
    }

    // ============================================================
    // Excel (xlsx / xls / xlsm) — streaming row-by-row
    // ============================================================

    private async Task<ParsedDataset> ParseExcelAsync(Guid userId, Guid datasetId, string path, CancellationToken ct)
    {
        var tables = new List<ParsedTable>();
        var warnings = new List<string>();
        var usedTableNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tempDir = storage.GetTempDirectory(userId, datasetId);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var sheetIndex = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            sheetIndex++;

            var sheetName = reader.Name ?? $"sheet_{sheetIndex}";
            if (reader.RowCount == 0)
            {
                warnings.Add($"Sheet '{sheetName}' is empty and was skipped.");
                continue;
            }

            // Read header row.
            if (!reader.Read())
            {
                warnings.Add($"Sheet '{sheetName}' has no header row and was skipped.");
                continue;
            }

            var headers = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                headers.Add(reader.GetValue(i)?.ToString() ?? string.Empty);
            }

            // Trim trailing empty headers (Excel sometimes pads columns).
            while (headers.Count > 0 && string.IsNullOrWhiteSpace(headers[^1]))
            {
                headers.RemoveAt(headers.Count - 1);
            }

            if (headers.Count == 0)
            {
                warnings.Add($"Sheet '{sheetName}' has no usable header columns and was skipped.");
                continue;
            }

            var normalizedColumns = normalizer.NormalizeColumns(headers);
            var stats = normalizedColumns.Select(c => new ColumnStats(c.Original, c.Normalized)).ToList();

            var tableBase = normalizer.NormalizeTableName(sheetName, $"sheet_{sheetIndex:000}");
            var tableName = MakeUniqueTableName(tableBase, usedTableNames);
            var tempCsv = Path.Combine(tempDir, tableName + ".csv");

            long rowCount = 0;
            await using (var output = new FileStream(tempCsv, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await writer.WriteLineAsync(string.Join(',', normalizedColumns.Select(c => EscapeCsv(c.Normalized))));

                var collectStats = true;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();

                    var values = new string[normalizedColumns.Count];
                    var allEmpty = true;
                    for (var c = 0; c < normalizedColumns.Count; c++)
                    {
                        var cell = c < reader.FieldCount ? FormatCell(reader.GetValue(c)) : string.Empty;
                        if (!string.IsNullOrWhiteSpace(cell)) allEmpty = false;
                        if (collectStats) stats[c].Add(cell);
                        values[c] = EscapeCsv(cell);
                    }

                    if (allEmpty) continue;
                    await writer.WriteLineAsync(string.Join(',', values));
                    rowCount++;

                    if (collectStats && rowCount >= MaxSampleRowsForTypeInference) collectStats = false;
                }
            }

            tables.Add(new ParsedTable(
                SourceName: sheetName,
                SourceType: "sheet",
                TableName: tableName,
                TempCsvPath: tempCsv,
                ParquetFileName: tableName + ".parquet",
                RowCount: rowCount,
                Columns: BuildParsedColumns(stats)));
        } while (reader.NextResult());

        if (tables.Count == 0)
        {
            throw new InvalidOperationException("No usable table found in workbook.");
        }

        return new ParsedDataset(tables, warnings);
    }

    // ============================================================
    // Shared helpers
    // ============================================================

    private static List<ParsedColumn> BuildParsedColumns(List<ColumnStats> stats)
    {
        return stats.Select((s, idx) => new ParsedColumn(
            Ordinal: idx + 1,
            OriginalHeader: s.OriginalHeader,
            NormalizedName: s.NormalizedName,
            DisplayName: s.OriginalHeader ?? s.NormalizedName,
            Aliases: s.BuildAliases(),
            InferredType: s.ReduceType(),
            SemanticType: s.GuessSemanticType(),
            NullCount: s.Nulls,
            DistinctCount: s.DistinctCount,
            DistinctCapped: s.DistinctCapped,
            SampleValues: s.Samples.ToArray()
        )).ToList();
    }

    private static async Task WriteRowAsync(StreamWriter writer, List<string> fields, int expectedCount, List<ColumnStats> stats, bool collectStats)
    {
        var output = new List<string>(expectedCount);
        for (var i = 0; i < expectedCount; i++)
        {
            var value = i < fields.Count ? fields[i] : string.Empty;
            if (collectStats) stats[i].Add(value);
            output.Add(EscapeCsv(value));
        }
        await writer.WriteLineAsync(string.Join(',', output));
    }

    private static string MakeUniqueTableName(string baseName, Dictionary<string, int> used)
    {
        if (!used.TryGetValue(baseName, out var count))
        {
            used[baseName] = 1;
            return baseName;
        }
        count++;
        used[baseName] = count;
        return $"{baseName}_{count}";
    }

    private static string FormatCell(object? value)
    {
        return value switch
        {
            null or DBNull => string.Empty,
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static char DetectDelimiter(string path)
    {
        using var reader = new StreamReader(path, DetectEncoding(path), detectEncodingFromByteOrderMarks: true);
        var firstLine = reader.ReadLine() ?? string.Empty;
        var candidates = new[] { ',', ';', '\t', '|' };
        return candidates
            .Select(c => (Delim: c, Count: firstLine.Count(x => x == c)))
            .OrderByDescending(t => t.Count)
            .First().Delim;
    }

    private static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        var bom = new byte[4];
        var read = fs.Read(bom, 0, 4);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return Encoding.UTF8;
    }

    private static List<string> SplitDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static bool HasUnbalancedQuotes(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                count++;
            }
        }
        return count % 2 != 0;
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
        return value;
    }
}

public record ParsedDataset(List<ParsedTable> Tables, List<string> Warnings);

public record ParsedTable(
    string SourceName,
    string SourceType,
    string TableName,
    string TempCsvPath,
    string ParquetFileName,
    long RowCount,
    List<ParsedColumn> Columns);

public record ParsedColumn(
    int Ordinal,
    string? OriginalHeader,
    string NormalizedName,
    string DisplayName,
    string[] Aliases,
    string InferredType,
    string? SemanticType,
    long NullCount,
    long DistinctCount,
    bool DistinctCapped,
    string[] SampleValues);
