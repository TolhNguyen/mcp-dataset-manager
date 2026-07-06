using ExcelDatasetManager.Api.Services;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class QueryValidatorTests
{
    private readonly QueryValidator _validator = new();

    // ---------- Cho phép ----------

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("select id, name from customers where city = 'Hà Nội'")]
    [InlineData("WITH t AS (SELECT 1 AS x) SELECT * FROM t")]
    [InlineData("SELECT * FROM orders;")]
    [InlineData("  \n SELECT 1")]
    public void Accepts_readonly_queries(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Forbidden_keyword_inside_string_literal_is_allowed()
    {
        var result = _validator.ValidateReadOnlySelect(
            "SELECT * FROM notes WHERE body = 'please update and delete this'");
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Forbidden_keyword_inside_comment_is_ignored()
    {
        var result = _validator.ValidateReadOnlySelect(
            "SELECT 1 -- drop table orders\n");
        Assert.True(result.Success, result.Message);
    }

    // ---------- Chặn ----------

    [Theory]
    [InlineData("INSERT INTO orders VALUES (1)")]
    [InlineData("UPDATE orders SET x = 1")]
    [InlineData("DELETE FROM orders")]
    [InlineData("DROP TABLE orders")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("ATTACH 'other.db'")]
    [InlineData("PRAGMA database_list")]
    [InlineData("COPY orders TO 'out.csv'")]
    [InlineData("CALL something()")]
    [InlineData("INSTALL httpfs")]
    public void Rejects_non_select_statements(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    [Theory]
    [InlineData("SELECT * FROM read_parquet('/etc/passwd')")]
    [InlineData("SELECT * FROM read_csv_auto('secret.csv')")]
    [InlineData("SELECT 1 UNION SELECT * FROM read_json('x.json')")]
    [InlineData("WITH t AS (SELECT 1) SELECT * FROM t; SET memory_limit='100GB'")]
    public void Rejects_dangerous_functions_even_inside_select(string sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
    }

    [Fact]
    public void Rejects_multiple_statements()
    {
        var result = _validator.ValidateReadOnlySelect("SELECT 1; SELECT 2");
        Assert.False(result.Success);
        Assert.Equal("INVALID_SQL", result.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void Rejects_empty_sql(string? sql)
    {
        var result = _validator.ValidateReadOnlySelect(sql);
        Assert.False(result.Success);
        Assert.Equal("INVALID_SQL", result.Code);
    }

    [Fact]
    public void Trailing_semicolon_is_stripped_from_returned_sql()
    {
        var result = _validator.ValidateReadOnlySelect("SELECT 1;");
        Assert.True(result.Success);
        Assert.Equal("SELECT 1", result.Sql);
    }

    // ---------- ApplyLimit ----------

    [Fact]
    public void ApplyLimit_wraps_query_without_limit()
    {
        var result = _validator.ApplyLimit("SELECT * FROM t", 100);
        Assert.Equal("SELECT * FROM (SELECT * FROM t) AS _user_query LIMIT 100", result);
    }

    [Fact]
    public void ApplyLimit_keeps_existing_top_level_limit()
    {
        var sql = "SELECT * FROM t LIMIT 5";
        Assert.Equal(sql, _validator.ApplyLimit(sql, 100));
    }

    [Fact]
    public void ApplyLimit_keeps_existing_limit_with_offset()
    {
        var sql = "SELECT * FROM t LIMIT 5 OFFSET 10";
        Assert.Equal(sql, _validator.ApplyLimit(sql, 100));
    }

    [Fact]
    public void ApplyLimit_wraps_when_limit_is_only_in_subquery()
    {
        var sql = "SELECT * FROM (SELECT * FROM t LIMIT 5) q";
        var result = _validator.ApplyLimit(sql, 100);
        Assert.EndsWith("LIMIT 100", result);
        Assert.StartsWith("SELECT * FROM (", result);
    }
}
