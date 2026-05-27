namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Manages on-disk layout for dataset artifacts.
/// /storage/users/{user_id}/datasets/{dataset_id}/
///   original_file.{ext}
///   manifest.md
///   parquet/{table_name}.parquet
/// </summary>
public class FileStorageService(IConfiguration configuration)
{
    public string RootPath => Path.GetFullPath(configuration["Storage:RootPath"] ?? "storage");

    public string GetDatasetDirectory(Guid userId, Guid datasetId)
        => Path.Combine(RootPath, "users", userId.ToString(), "datasets", datasetId.ToString());

    public string GetOriginalPath(Guid userId, Guid datasetId, string storedFileName)
        => Path.Combine(GetDatasetDirectory(userId, datasetId), storedFileName);

    public string GetManifestPath(Guid userId, Guid datasetId, string manifestFileName = "manifest.md")
        => Path.Combine(GetDatasetDirectory(userId, datasetId), manifestFileName);

    public string GetParquetDirectory(Guid userId, Guid datasetId)
        => Path.Combine(GetDatasetDirectory(userId, datasetId), "parquet");

    public string GetParquetPath(Guid userId, Guid datasetId, string parquetFileName)
        => Path.Combine(GetParquetDirectory(userId, datasetId), parquetFileName);

    public string GetTempDirectory(Guid userId, Guid datasetId)
        => Path.Combine(GetDatasetDirectory(userId, datasetId), "_tmp");

    public void EnsureDatasetDirectories(Guid userId, Guid datasetId)
    {
        Directory.CreateDirectory(GetDatasetDirectory(userId, datasetId));
        Directory.CreateDirectory(GetParquetDirectory(userId, datasetId));
    }

    public void EnsureTempDirectory(Guid userId, Guid datasetId)
        => Directory.CreateDirectory(GetTempDirectory(userId, datasetId));

    public void DeleteTempDirectory(Guid userId, Guid datasetId)
    {
        var path = GetTempDirectory(userId, datasetId);
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); }
            catch { /* best effort - swallow */ }
        }
    }

    public void DeleteDatasetDirectory(Guid userId, Guid datasetId)
    {
        var path = GetDatasetDirectory(userId, datasetId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
