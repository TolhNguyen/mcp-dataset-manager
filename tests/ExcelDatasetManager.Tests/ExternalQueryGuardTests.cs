using ExcelDatasetManager.Api.Services.Connectors;
using Xunit;

namespace ExcelDatasetManager.Tests;

public class ExternalQueryGuardTests
{
    // ---------- Cho phép (all providers) ----------

    public static IEnumerable<object[]> AllProviders()
    {
        yield return new object[] { ExternalDbProviders.PostgreSql };
        yield return new object[] { ExternalDbProviders.MySql };
        yield return new object[] { ExternalDbProviders.MsSql };
        yield return new object[] { ExternalDbProviders.BigQuery };
    }

    public static IEnumerable<object[]> AllProvidersWithSql()
    {
        var sqls = new[]
        {
            "SELECT * FROM orders",
            "select id, name from customers where city = 'Ha Noi'",
            "WITH t AS (SELECT 1 AS x) SELECT * FROM t",
            "SELECT * FROM orders;",
            "SELECT * FROM notes WHERE body = 'please insert and delete this'",
        };
        foreach (var provider in new[] { ExternalDbProviders.PostgreSql, ExternalDbProviders.MySql, ExternalDbProviders.MsSql, ExternalDbProviders.BigQuery })
        {
            foreach (var sql in sqls)
            {
                yield return new object[] { provider, sql };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllProvidersWithSql))]
    public void Accepts_readonly_queries_for_all_providers(string provider, string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, provider);
        Assert.True(result.Success, $"{provider}: {result.Message}");
    }

    // ---------- Chặn: unknown provider ----------

    [Fact]
    public void Rejects_unknown_provider()
    {
        var result = ExternalQueryGuard.Validate("SELECT 1", "oracle");
        Assert.False(result.Success);
        Assert.Equal("INVALID_SQL", result.Code);
    }

    // ---------- Chặn: common forbidden tokens + multi-statement (all providers) ----------

    public static IEnumerable<object[]> CommonRejectedSqlPerProvider()
    {
        var sqls = new[]
        {
            "INSERT INTO orders VALUES (1)",
            "UPDATE orders SET x = 1",
            "DELETE FROM orders",
            "DROP TABLE orders",
            "CREATE TABLE t (id INT)",
            "MERGE INTO orders USING x ON 1=1 WHEN MATCHED THEN DELETE",
            "SELECT 1; SELECT 2",
        };
        foreach (var provider in new[] { ExternalDbProviders.PostgreSql, ExternalDbProviders.MySql, ExternalDbProviders.MsSql, ExternalDbProviders.BigQuery })
        {
            foreach (var sql in sqls)
            {
                yield return new object[] { provider, sql };
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommonRejectedSqlPerProvider))]
    public void Rejects_common_forbidden_statements_for_all_providers(string provider, string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, provider);
        Assert.False(result.Success);
    }

    // ---------- Chặn riêng postgresql ----------

    [Theory]
    [InlineData("COPY t TO '/tmp/x'")]
    [InlineData("SELECT pg_sleep(10)")]
    [InlineData("DO $$ ... $$")]
    [InlineData("SET work_mem='1GB'")]
    [InlineData("SELECT pg_sleep_for('5 minutes')")]
    [InlineData("SELECT pg_sleep_until('tomorrow')")]
    [InlineData("SELECT pg_read_binary_file('/etc/passwd')")]
    [InlineData("SELECT * FROM dblink_connect('host=evil')")]
    [InlineData("SELECT dblink_exec('...', 'DELETE FROM t')")]
    public void Rejects_postgresql_specific(string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, ExternalDbProviders.PostgreSql);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    // ---------- Chặn riêng mysql ----------

    [Theory]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'")]
    [InlineData("LOAD DATA INFILE 'x' INTO TABLE t")]
    [InlineData("SELECT load_file('/etc/passwd')")]
    [InlineData("SELECT benchmark(1000000, sha1('x'))")]
    public void Rejects_mysql_specific(string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, ExternalDbProviders.MySql);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    // ---------- Chặn riêng mssql ----------

    [Theory]
    [InlineData("SELECT * INTO newtable FROM t")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("SELECT * FROM OPENROWSET(...)")]
    [InlineData("WAITFOR DELAY '0:0:10'")]
    [InlineData("EXEC sp_executesql N'...'")]
    public void Rejects_mssql_specific(string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, ExternalDbProviders.MsSql);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    // ---------- Chặn riêng bigquery ----------

    [Theory]
    [InlineData("EXPORT DATA OPTIONS(...) AS SELECT 1")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("DECLARE x INT64")]
    public void Rejects_bigquery_specific(string sql)
    {
        var result = ExternalQueryGuard.Validate(sql, ExternalDbProviders.BigQuery);
        Assert.False(result.Success);
        Assert.Equal("NON_READONLY_SQL", result.Code);
    }

    // ---------- Trailing semicolon stripped ----------

    [Fact]
    public void Trailing_semicolon_is_stripped_from_returned_sql()
    {
        var result = ExternalQueryGuard.Validate("SELECT 1;", ExternalDbProviders.PostgreSql);
        Assert.True(result.Success);
        Assert.Equal("SELECT 1", result.Sql);
    }

    // ---------- ApplyRowCap: postgresql / mysql / bigquery ----------

    [Theory]
    [InlineData(ExternalDbProviders.PostgreSql)]
    [InlineData(ExternalDbProviders.MySql)]
    [InlineData(ExternalDbProviders.BigQuery)]
    public void ApplyRowCap_wraps_query_without_limit(string provider)
    {
        var result = ExternalQueryGuard.ApplyRowCap("SELECT * FROM t", provider, 100);
        Assert.Equal("SELECT * FROM (  SELECT * FROM t  ) AS _edm_q LIMIT 100", result);
    }

    [Theory]
    [InlineData(ExternalDbProviders.PostgreSql)]
    [InlineData(ExternalDbProviders.MySql)]
    [InlineData(ExternalDbProviders.BigQuery)]
    public void ApplyRowCap_keeps_existing_top_level_limit(string provider)
    {
        var sql = "SELECT * FROM t LIMIT 5";
        Assert.Equal(sql, ExternalQueryGuard.ApplyRowCap(sql, provider, 100));
    }

    [Theory]
    [InlineData(ExternalDbProviders.PostgreSql)]
    [InlineData(ExternalDbProviders.MySql)]
    [InlineData(ExternalDbProviders.BigQuery)]
    public void ApplyRowCap_keeps_existing_limit_with_offset(string provider)
    {
        var sql = "SELECT * FROM t LIMIT 5 OFFSET 2";
        Assert.Equal(sql, ExternalQueryGuard.ApplyRowCap(sql, provider, 100));
    }

    [Theory]
    [InlineData(ExternalDbProviders.PostgreSql)]
    [InlineData(ExternalDbProviders.MySql)]
    [InlineData(ExternalDbProviders.BigQuery)]
    public void ApplyRowCap_wraps_when_limit_is_only_in_subquery(string provider)
    {
        var sql = "SELECT * FROM (SELECT * FROM t LIMIT 5) q";
        var result = ExternalQueryGuard.ApplyRowCap(sql, provider, 100);
        Assert.EndsWith("LIMIT 100", result);
        Assert.StartsWith("SELECT * FROM (", result);
    }

    // ---------- ApplyRowCap: mssql ----------

    [Fact]
    public void ApplyRowCap_mssql_wraps_plain_select()
    {
        var result = ExternalQueryGuard.ApplyRowCap("SELECT * FROM t", ExternalDbProviders.MsSql, 100);
        Assert.Equal("SELECT TOP (100) * FROM (  SELECT * FROM t  ) AS _edm_q", result);
    }

    [Fact]
    public void ApplyRowCap_mssql_appends_fetch_next_after_order_by()
    {
        var result = ExternalQueryGuard.ApplyRowCap("SELECT * FROM t ORDER BY x", ExternalDbProviders.MsSql, 100);
        Assert.EndsWith("OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", result);
    }

    [Fact]
    public void ApplyRowCap_mssql_cap_survives_trailing_line_comment()
    {
        // Regression: the cap must not land on the same line as a trailing "--" comment,
        // otherwise the comment swallows the cap and the query runs unbounded.
        var result = ExternalQueryGuard.ApplyRowCap("SELECT * FROM big_table ORDER BY id --", ExternalDbProviders.MsSql, 100);
        Assert.Contains("\nOFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY", result);
    }

    [Fact]
    public void ApplyRowCap_mssql_keeps_existing_top()
    {
        var sql = "SELECT TOP 5 * FROM t";
        Assert.Equal(sql, ExternalQueryGuard.ApplyRowCap(sql, ExternalDbProviders.MsSql, 100));
    }

    [Fact]
    public void ApplyRowCap_mssql_keeps_existing_fetch_next()
    {
        var sql = "SELECT * FROM t ORDER BY x OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY";
        Assert.Equal(sql, ExternalQueryGuard.ApplyRowCap(sql, ExternalDbProviders.MsSql, 100));
    }
}
