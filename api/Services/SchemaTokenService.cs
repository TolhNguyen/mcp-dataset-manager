using System.Security.Cryptography;
using System.Text;

namespace ExcelDatasetManager.Api.Services;

/// <summary>
/// Computes a stable schema fingerprint. Callers pass tables sorted by logical dataset scope;
/// this service canonicalizes table order and preserves column ordinal order within each table.
/// </summary>
public static class SchemaTokenService
{
    public const string Prefix = "st_";

    public static string Compute(IEnumerable<(string TableName, IReadOnlyList<(string Name, string Type)> Columns)> tables)
    {
        var sb = new StringBuilder();
        foreach (var (tableName, columns) in tables.OrderBy(t => t.TableName, StringComparer.Ordinal))
        {
            sb.Append(tableName).Append('{');
            foreach (var (name, type) in columns)
            {
                sb.Append(name).Append(':').Append(type ?? "UNKNOWN").Append(',');
            }
            sb.Append("}|");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Prefix + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static bool Matches(string? provided, string expected)
        => !string.IsNullOrWhiteSpace(provided)
           && string.Equals(provided.Trim(), expected, StringComparison.Ordinal);
}
