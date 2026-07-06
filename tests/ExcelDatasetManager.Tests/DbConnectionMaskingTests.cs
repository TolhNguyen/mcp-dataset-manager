using System.Text.Json;
using ExcelDatasetManager.Api.Services;
using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DbConnectionMaskingTests
{
    [Fact]
    public void Build_masks_host_and_never_includes_password_for_relational_providers()
    {
        var config = new DbConnectionConfig(
            ExternalDbProviders.PostgreSql,
            Host: "prodhost.internal", Port: 5432, Database: "salesdb",
            Username: "readonly_user", Password: "SuperSecretPassword", Ssl: true,
            ProjectId: null, BigQueryDataset: null, ServiceAccountJson: null, MaxBytesBilled: null);

        var result = DbConnectionMasking.Build(
            Guid.NewGuid(), "Prod DB", ExternalDbProviders.PostgreSql, config,
            lastTestStatus: "success", lastTestAt: DateTime.UtcNow, lastTestError: null, createdAt: DateTime.UtcNow);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"host_masked\":\"pro***\"", json);
        Assert.Contains("\"database\":\"salesdb\"", json);
        Assert.Contains("\"username\":\"readonly_user\"", json);
        Assert.DoesNotContain("SuperSecretPassword", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_masks_project_id_and_never_includes_service_account_json_for_bigquery()
    {
        var config = new DbConnectionConfig(
            ExternalDbProviders.BigQuery,
            Host: null, Port: null, Database: null, Username: null, Password: null, Ssl: true,
            ProjectId: "my-project-123", BigQueryDataset: "analytics",
            ServiceAccountJson: """{"client_email":"svc@x.iam.gserviceaccount.com","private_key":"SECRET_KEY_DO_NOT_LEAK"}""",
            MaxBytesBilled: 1_000_000);

        var result = DbConnectionMasking.Build(
            Guid.NewGuid(), "BQ Analytics", ExternalDbProviders.BigQuery, config,
            lastTestStatus: null, lastTestAt: null, lastTestError: null, createdAt: DateTime.UtcNow);

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"host_masked\":\"my-***\"", json);
        Assert.Contains("\"database\":\"analytics\"", json);
        Assert.DoesNotContain("SECRET_KEY_DO_NOT_LEAK", json);
        Assert.DoesNotContain("service_account", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaskHost_uses_first_three_chars_plus_stars()
    {
        Assert.Equal("abc***", DbConnectionMasking.MaskHost("abcdef.example.com"));
        Assert.Equal("ab***", DbConnectionMasking.MaskHost("ab"));
        Assert.Equal("***", DbConnectionMasking.MaskHost(null));
        Assert.Equal("***", DbConnectionMasking.MaskHost(""));
    }
}
