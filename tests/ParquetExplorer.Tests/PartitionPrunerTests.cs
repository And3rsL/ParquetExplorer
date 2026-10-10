using System.Security.Claims;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ParquetExplorer.Options;
using ParquetExplorer.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ParquetExplorer.Tests;

/// <summary>
/// The same partitioned folder is exposed twice: "lake" as a normal source (DuckDB views) and
/// "net" as network storage (no views, every query rewritten). Rewritten queries must return
/// exactly what DuckDB returns when it reads every file.
/// </summary>
public sealed class PartitionPrunerTests : IAsyncLifetime
{
    private const int TotalFiles = 4 * 3 * 4;

    private readonly string _root = TempDir.Create();
    private readonly string _state = TempDir.Create();
    private DuckDbEngine _engine = default!;
    private CatalogService _catalog = default!;
    private QueryService _queries = default!;

    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));

    public async Task InitializeAsync()
    {
        var data = Path.Combine(_root, "events");
        Directory.CreateDirectory(data);
        using (var c = new DuckDBConnection("Data Source=:memory:"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // 4 days x 3 regions x 4 hours = 48 partition folders, one file each.
            cmd.CommandText = $"""
                COPY (SELECT i AS id, i * 10 AS v,
                             DATE '2026-05-01' + (i % 4)::INT AS day,
                             ['eu', 'us', 'asia'][1 + (i // 4) % 3] AS region,
                             (i // 12) % 4 AS hour
                      FROM range(2400) t(i))
                  TO '{data.Replace('\\', '/')}' (FORMAT parquet, PARTITION_BY (day, region, hour));
                """;
            cmd.ExecuteNonQuery();
        }

        var options = new ExplorerOptions
        {
            StateDirectory = _state,
            DataSources =
            [
                new DataSourceOptions { Name = "lake", Type = DataSourceType.Local, Path = _root },
                new DataSourceOptions { Name = "net", Type = DataSourceType.Local, Path = _root, NetworkStorage = true },
            ],
            Query = new QueryOptions { TempDirectory = Path.Combine(_state, "tmp"), TimeoutSeconds = 60 },
            Auth = new AuthOptions { Users = [new UserOptions { Username = "admin", PasswordHash = UserStore.HashPassword("x"), Roles = [Roles.Admin] }] },
        };
        var o = MsOptions.Create(options);
        var users = new UserStore(new StaticMonitor(options), NullLogger<UserStore>.Instance);
        _engine = new DuckDbEngine(o, NullLogger<DuckDbEngine>.Instance);
        var registry = new SourceRegistry(o, new EphemeralDataProtectionProvider(), NullLogger<SourceRegistry>.Instance);
        _catalog = new CatalogService(_engine, registry, NullLogger<CatalogService>.Instance);
        _queries = new QueryService(_engine, new QueryHistoryService(o), users,
            new PartitionPruner(_catalog, NullLogger<PartitionPruner>.Instance), o, NullLogger<QueryService>.Instance);
        await _catalog.ReloadAsync();
    }

    public Task DisposeAsync()
    {
        _engine.Dispose();
        TempDir.Delete(_root);
        TempDir.Delete(_state);
        return Task.CompletedTask;
    }

    /// <summary>Runs SQL on the "lake" views, bypassing the rewriter: DuckDB reads every file.</summary>
    private List<string> Reference(string sql)
    {
        using var conn = _engine.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.Replace("{s}", "lake");
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : QueryService.Format(r.GetValue(i)))));
        return rows;
    }

    private async Task<QueryExecution> Run(string sql, string schema, int rowLimit = 100_000)
    {
        var exec = await _queries.ExecuteAsync(sql.Replace("{s}", schema), rowLimit, Admin);
        Assert.True(exec.Error is null, exec.Error);
        return exec;
    }

    private static List<string> Rows(QueryExecution e) =>
        e.ResultSets.Last().Rows.Select(r => string.Join("|", r.Select(v => v ?? "NULL"))).ToList();

    public static TheoryData<string> Queries =>
    [
        "SELECT count(*), sum(v) FROM {s}.events WHERE day = DATE '2026-05-02'",
        "SELECT count(*), sum(v) FROM {s}.events WHERE region IN ('eu', 'asia') AND hour >= 2",
        "SELECT count(*) FROM {s}.events WHERE day BETWEEN '2026-05-02' AND '2026-05-03' AND 2 > hour",
        "SELECT count(*) FROM {s}.events WHERE region = 'eu' OR hour = 1",
        "SELECT count(*) FROM {s}.events WHERE hour <> 3 AND v > 1000",
        "SELECT count(*) FROM {s}.events e JOIN {s}.events f ON e.id = f.id WHERE e.day = DATE '2026-05-01' AND f.region = 'us'",
        "SELECT count(*) FROM {s}.events e LEFT JOIN {s}.events f ON e.id = f.id + 1 WHERE f.hour = 2",
        "WITH x AS (SELECT * FROM {s}.events WHERE hour <> 3) SELECT count(*), max(v) FROM x",
        "SELECT count(*) FROM {s}.events WHERE day = DATE '2030-01-01'",
        "SELECT region, count(*) FROM {s}.events WHERE day > '2026-05-02' GROUP BY ALL ORDER BY ALL",
        "SELECT count(*) FROM {s}.events WHERE hour = 0 UNION ALL SELECT count(*) FROM {s}.events WHERE hour = 1",
        "SELECT count(*) FROM {s}.events WHERE id IN (SELECT id FROM {s}.events WHERE region = 'eu')",
        "SELECT count(*) FROM \"{s}\".\"events\" AS t WHERE t.region = 'eu' AND t.day < DATE '2026-05-03'",
        "SELECT day, region, hour, id, v FROM {s}.events WHERE region = 'asia' AND hour = 3 ORDER BY id LIMIT 5",
        "SELECT count(*) FROM (SELECT * FROM {s}.events WHERE day = '2026-05-04') s WHERE s.hour IN (1, 2)",
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task RewrittenQueries_ReturnTheSameRowsAsAFullScan(string sql)
    {
        var expected = Reference(sql);
        Assert.Equal(expected, Rows(await Run(sql, "lake")));
        Assert.Equal(expected, Rows(await Run(sql, "net")));
    }

    [Fact]
    public async Task PartitionFilter_ReadsOnlyMatchingFiles()
    {
        var exec = await Run("SELECT count(*) FROM {s}.events WHERE day = DATE '2026-05-02' AND region = 'eu'", "net");
        Assert.Contains(exec.Notes, n => n.Contains($"4 of {TotalFiles} files"));
        Assert.Equal(Reference("SELECT count(*) FROM {s}.events WHERE day = DATE '2026-05-02' AND region = 'eu'"), Rows(exec));
    }

    [Fact]
    public async Task UnusableConditions_KeepAllFiles()
    {
        var exec = await Run("SELECT count(*) FROM {s}.events WHERE region = 'eu' OR hour = 1", "lake");
        Assert.Empty(exec.Notes);
    }

    [Fact]
    public async Task TopRows_ReadOnlyTheFirstFiles()
    {
        var exec = await Run("SELECT * FROM {s}.events LIMIT 10", "net");
        Assert.Equal(10, exec.ResultSets[0].Rows.Count);
        Assert.Contains(exec.Notes, n => n.Contains("enough for 10 rows"));
    }

    [Fact]
    public async Task GridRowLimit_StillReportsTruncation()
    {
        var exec = await Run("SELECT * FROM {s}.events WHERE region = 'us'", "net", rowLimit: 30);
        var rs = exec.ResultSets[0];
        Assert.Equal(30, rs.Rows.Count);
        Assert.True(rs.Truncated);
    }

    [Fact]
    public async Task NetworkTables_HaveNoView_ButKnowTheirColumns()
    {
        var table = _catalog.FindTable("net", "events")!;
        Assert.False(table.HasView);
        Assert.Equal(TotalFiles, table.Files.Count);
        Assert.Contains(table.Columns, c => c.Name == "day" && c.Type == "DATE" && c.IsPartitionKey);

        var describe = await Run("DESCRIBE {s}.events", "net");
        Assert.Contains(describe.ResultSets[0].Rows, r => r[0] == "region");

        var full = await Run("SELECT count(*) FROM {s}.events", "net");
        Assert.Equal("2400", full.ResultSets[0].Rows[0][0]);

        var explain = await Run("EXPLAIN SELECT count(*) FROM {s}.events WHERE hour = 1", "net");
        Assert.NotEmpty(explain.ResultSets[0].Rows);
    }

    [Theory]
    [InlineData("SUMMARIZE {s}.events")]
    [InlineData("SUMMARIZE SELECT * FROM {s}.events WHERE hour = 1")]
    [InlineData("DESCRIBE SELECT e.id FROM {s}.events e WHERE e.day = DATE '2026-05-01'")]
    public async Task ShowStatements_Work(string sql)
    {
        var exec = await Run(sql, "net");
        Assert.NotEmpty(exec.ResultSets[0].Rows);
    }

    [Fact]
    public async Task EmptyResults_AreNotErrors()
    {
        var exec = await Run("SELECT * FROM {s}.events WHERE v < 0", "lake");
        Assert.Empty(exec.ResultSets[0].Rows);
        Assert.Equal(5, exec.ResultSets[0].Columns.Count);

        var script = await Run("SELECT 1 AS x WHERE false; SELECT 2 AS y", "lake");
        Assert.Equal(2, script.ResultSets.Count);
        Assert.Empty(script.ResultSets[0].Rows);
        Assert.Equal("2", script.ResultSets[1].Rows[0][0]);
    }

    [Fact]
    public async Task NoMatchingPartition_ReturnsNoRowsWithTheRightColumns()
    {
        var exec = await Run("SELECT * FROM {s}.events WHERE day = DATE '2031-01-01'", "net");
        Assert.Empty(exec.ResultSets[0].Rows);
        Assert.Equal(["day", "hour", "id", "region", "v"], exec.ResultSets[0].Columns.Select(c => c.Name).Order());
    }
}
