using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class DialectNotesTests
{
    [Theory]
    [InlineData("external_db", "mssql", "tsql")]
    [InlineData("external_db", "bigquery", "bigquery")]
    [InlineData("external_db", "postgresql", "postgresql")]
    [InlineData("external_db", "mysql", "mysql")]
    [InlineData("file", null, "duckdb")]
    public void MapDialect_maps_provider(string kind, string? provider, string expected)
        => Assert.Equal(expected, DialectNotes.MapDialect(kind, provider));

    [Theory]
    [InlineData("tsql")]
    [InlineData("bigquery")]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("duckdb")]
    public void For_known_dialect_returns_notes(string dialect)
        => Assert.NotEmpty(DialectNotes.For(dialect));

    [Fact]
    public void For_unknown_dialect_is_empty()
        => Assert.Empty(DialectNotes.For("oracle"));

    [Fact]
    public void Tsql_notes_mention_bracket_rule()
        => Assert.Contains(DialectNotes.For("tsql"), n => n.Contains("[dbo.table"));
}
