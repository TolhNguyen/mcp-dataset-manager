using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace ExcelDatasetManager.Api.Services;

public sealed record MigrationScript(int Version, string Name, string Sql);

public static class MigrationScriptLoader
{
    private const string ResourcePrefix = "ExcelDatasetManager.Api.Migrations.";
    private static readonly Regex NamePattern = new(@"^(\d{4})_[A-Za-z0-9_]+\.sql$", RegexOptions.Compiled);

    public static IReadOnlyList<MigrationScript> LoadAll(Assembly assembly)
    {
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            var fileName = resource[ResourcePrefix.Length..];
            var match = NamePattern.Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Migration resource '{fileName}' must be named NNNN_description.sql (e.g. 0002_add_connections.sql).");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Cannot open embedded resource '{resource}'.");
            using var reader = new StreamReader(stream);

            scripts.Add(new MigrationScript(
                Version: int.Parse(match.Groups[1].Value),
                Name: fileName,
                Sql: reader.ReadToEnd()));
        }

        var ordered = scripts.OrderBy(s => s.Version).ToList();
        var duplicate = ordered.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate migration version {duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.Name))}.");
        }

        return ordered;
    }
}

public class MigrationRunner(NpgsqlDataSource dataSource, ILogger<MigrationRunner> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INT PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            )
            """);

        var applied = (await conn.QueryAsync<int>("SELECT version FROM schema_migrations")).ToHashSet();

        foreach (var script in MigrationScriptLoader.LoadAll(typeof(MigrationRunner).Assembly))
        {
            if (applied.Contains(script.Version)) continue;

            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(script.Sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_migrations (version, name) VALUES (@Version, @Name)",
                new { script.Version, script.Name }, tx);
            await tx.CommitAsync(ct);

            logger.LogInformation("Applied migration {Name}", script.Name);
        }
    }
}
