using DuckDB.NET.Data;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Converts the parser's intermediate CSV files into Parquet using DuckDB's COPY.
/// Lets DuckDB do the heavy lifting for type detection — the same engine will read the files back at query time.
/// </summary>
public class ParquetWriter(ILogger<ParquetWriter> logger)
{
    public void WriteParquet(string sourceCsvPath, string targetParquetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetParquetPath)!);

        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();

        var csvEscaped = sourceCsvPath.Replace("\\", "/").Replace("'", "''");
        var parquetEscaped = targetParquetPath.Replace("\\", "/").Replace("'", "''");

        // sample_size=-1 means full file scan for type inference. This is what we want for correctness on the first pass.
        var sql = $"""
                   COPY (
                       SELECT * FROM read_csv_auto('{csvEscaped}',
                           header=true,
                           sample_size=-1,
                           all_varchar=false,
                           ignore_errors=true)
                   ) TO '{parquetEscaped}' (FORMAT 'parquet', COMPRESSION 'zstd');
                   """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();

        logger.LogInformation("Wrote parquet {Target} from {Source}", targetParquetPath, sourceCsvPath);
    }
}
