using System.Text.Json;
using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DbConnectionConfigTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_postgresql_missing_host_returns_error()
    {
        var raw = Json("""{"database":"db","username":"u","password":"p"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.PostgreSql, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("host", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_mysql_missing_password_returns_error()
    {
        var raw = Json("""{"host":"localhost","database":"db","username":"u"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.MySql, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("password", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_mssql_missing_database_returns_error()
    {
        var raw = Json("""{"host":"localhost","username":"u","password":"p"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.MsSql, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("database", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_relational_missing_username_returns_error()
    {
        var raw = Json("""{"host":"localhost","database":"db","password":"p"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.PostgreSql, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("username", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_missing_project_id_returns_error()
    {
        var validSa = """{"client_email":"svc@proj.iam.gserviceaccount.com"}""";
        var raw = Json($$"""{"dataset":"ds","service_account_json":{{JsonSerializer.Serialize(validSa)}}}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("project_id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_missing_dataset_returns_error()
    {
        var validSa = """{"client_email":"svc@proj.iam.gserviceaccount.com"}""";
        var raw = Json($$"""{"project_id":"proj","service_account_json":{{JsonSerializer.Serialize(validSa)}}}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("dataset", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_missing_service_account_json_returns_error()
    {
        var raw = Json("""{"project_id":"proj","dataset":"ds"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("service_account_json", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_service_account_json_invalid_json_returns_error()
    {
        var raw = Json("""{"project_id":"proj","dataset":"ds","service_account_json":"not-valid-json"}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("service_account_json", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_service_account_json_missing_client_email_returns_error()
    {
        var sa = """{"project_id":"proj-x"}""";
        var raw = Json($$"""{"project_id":"proj","dataset":"ds","service_account_json":{{JsonSerializer.Serialize(sa)}}}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("client_email", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_bigquery_defaults_max_bytes_billed_to_1gb()
    {
        var sa = """{"client_email":"svc@proj.iam.gserviceaccount.com"}""";
        var raw = Json($$"""{"project_id":"proj","dataset":"ds","service_account_json":{{JsonSerializer.Serialize(sa)}}}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal(1_073_741_824L, config!.MaxBytesBilled);
    }

    [Fact]
    public void Parse_unknown_provider_returns_error()
    {
        var raw = Json("""{"host":"localhost"}""");

        var (config, error) = DbConnectionConfig.Parse("oracle", raw);

        Assert.Null(config);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(ExternalDbProviders.PostgreSql, 5432)]
    [InlineData(ExternalDbProviders.MySql, 3306)]
    [InlineData(ExternalDbProviders.MsSql, 1433)]
    public void Parse_defaults_port_per_provider(string provider, int expectedPort)
    {
        var raw = Json("""{"host":"localhost","database":"db","username":"u","password":"p"}""");

        var (config, error) = DbConnectionConfig.Parse(provider, raw);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal(expectedPort, config!.Port);
        Assert.True(config.Ssl); // default ssl = true
    }

    [Fact]
    public void Parse_relational_honors_explicit_port_and_ssl()
    {
        var raw = Json("""{"host":"localhost","port":6543,"database":"db","username":"u","password":"p","ssl":false}""");

        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.PostgreSql, raw);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal(6543, config!.Port);
        Assert.False(config.Ssl);
    }

    [Fact]
    public void ToJson_then_FromJson_round_trips_relational_config()
    {
        var raw = Json("""{"host":"localhost","port":5432,"database":"db","username":"u","password":"p","ssl":true}""");
        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.PostgreSql, raw);
        Assert.Null(error);

        var json = config!.ToJson();
        var roundTripped = DbConnectionConfig.FromJson(json);

        Assert.Equal(config, roundTripped);
    }

    [Fact]
    public void ToJson_then_FromJson_round_trips_bigquery_config()
    {
        var sa = """{"client_email":"svc@proj.iam.gserviceaccount.com"}""";
        var raw = Json($$"""{"project_id":"proj","dataset":"ds","service_account_json":{{JsonSerializer.Serialize(sa)}},"max_bytes_billed":5000000}""");
        var (config, error) = DbConnectionConfig.Parse(ExternalDbProviders.BigQuery, raw);
        Assert.Null(error);

        var json = config!.ToJson();
        var roundTripped = DbConnectionConfig.FromJson(json);

        Assert.Equal(config, roundTripped);
    }
}
