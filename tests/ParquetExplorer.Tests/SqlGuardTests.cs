using ParquetExplorer.Services;

namespace ParquetExplorer.Tests;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("with a as (select 1) select * from a")]
    [InlineData("FROM samples.orders LIMIT 10")]
    [InlineData("DESCRIBE samples.orders")]
    [InlineData("SUMMARIZE samples.orders")]
    [InlineData("SHOW TABLES")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    [InlineData("PIVOT t ON y USING sum(x)")]
    [InlineData("SELECT 1; SELECT 2;")]
    [InlineData("SELECT 'DROP TABLE x'")]
    public void ReadOnlyStatements_AreAllowed(string sql) => Assert.Null(SqlGuard.CheckReadOnly(sql));

    [Theory]
    [InlineData("CREATE TABLE x AS SELECT 1")]
    [InlineData("COPY (SELECT 1) TO '/tmp/x.csv'")]
    [InlineData("ATTACH 'x.db'")]
    [InlineData("SET threads = 1")]
    [InlineData("INSTALL httpfs")]
    [InlineData("SELECT 1; DROP VIEW samples.orders")]
    [InlineData("/* sneaky */ DELETE FROM x")]
    [InlineData("EXPLAIN ANALYZE COPY (SELECT 1) TO 'x.csv'")]
    [InlineData("PRAGMA enable_profiling")]
    [InlineData("CALL dbgen(sf=1)")]
    public void WriteStatements_AreRejected(string sql) => Assert.NotNull(SqlGuard.CheckReadOnly(sql));
}
