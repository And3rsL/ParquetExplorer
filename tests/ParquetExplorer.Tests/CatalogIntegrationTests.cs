using System.Security.Claims;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using ParquetExplorer.Options;
using ParquetExplorer.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ParquetExplorer.Tests;

/// <summary>End-to-end: writes a small lake to disk, discovers it and queries it through the sandboxed engine.</summary>
public sealed class CatalogIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "px-tests-" + Guid.NewGuid().ToString("N"));
    private DuckDbEngine _engine = default!;
    private CatalogService _catalog = default!;
    private QueryService _queries = default!;

    private static readonly ClaimsPrincipal Reader = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader")], "test"));
    private static readonly ClaimsPrincipal Admin = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin")], "test"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sales", "orders"));
        Directory.CreateDirectory(Path.Combine(_root, "crm", "customers"));
        Directory.CreateDirectory(Path.Combine(_root, "crm", "_temporary"));
        using (var c = new DuckDBConnection("Data Source=:memory:"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                COPY (SELECT i AS id, i % 3 AS region, i * 1.5 AS amount FROM range(300) t(i))
                  TO '{_root}/sales/orders' (FORMAT parquet, PARTITION_BY (region));
                COPY (SELECT i AS id, 'c' || i AS name FROM range(10) t(i)) TO '{_root}/crm/customers/part-0.parquet' (FORMAT parquet);
                COPY (SELECT i AS id, 'c' || i AS name FROM range(10, 15) t(i)) TO '{_root}/crm/customers/part-1.parquet' (FORMAT parquet);
                COPY (SELECT 1 AS junk) TO '{_root}/crm/_temporary/x.parquet' (FORMAT parquet);
                COPY (SELECT 42 AS answer) TO '{_root}/lookup.parquet' (FORMAT parquet);
                """;
            cmd.ExecuteNonQuery();
        }

        var options = MsOptions.Create(new ExplorerOptions
        {
            DataSources = [new DataSourceOptions { Name = "lake", Type = DataSourceType.Local, Path = _root }],
            Query = new QueryOptions { TempDirectory = Path.Combine(_root, ".tmp"), TimeoutSeconds = 30 },
        });
        _engine = new DuckDbEngine(options, NullLogger<DuckDbEngine>.Instance);
        _catalog = new CatalogService(_engine, options, NullLogger<CatalogService>.Instance);
        _queries = new QueryService(_engine, new QueryHistoryService(options), options, NullLogger<QueryService>.Instance);
        await _catalog.RefreshAllAsync();
    }

    public Task DisposeAsync()
    {
        _engine.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void Folders_BecomeTables_AndHivePartitionsAreFolded()
    {
        var src = Assert.Single(_catalog.Sources);
        Assert.Null(src.Error);
        Assert.Equal(["crm_customers", "lookup", "sales_orders"], src.Tables.Select(t => t.Name).ToArray());

        var orders = src.Tables.Single(t => t.Name == "sales_orders");
        Assert.True(orders.HivePartitioned);
        Assert.Equal(3, orders.FileCount);
        Assert.Contains(orders.Columns, c => c.Name == "region" && c.IsPartitionKey);

        var customers = src.Tables.Single(t => t.Name == "crm_customers");
        Assert.Equal(2, customers.FileCount); // _temporary is ignored
    }

    [Fact]
    public async Task Views_CanBeQueried()
    {
        var exec = await _queries.ExecuteAsync(
            "SELECT count(*) AS n, sum(amount) AS total FROM lake.sales_orders; SELECT count(*) FROM lake.crm_customers; SELECT answer FROM lake.lookup",
            100, Reader);
        Assert.Null(exec.Error);
        Assert.Equal(3, exec.ResultSets.Count);
        Assert.Equal("300", exec.ResultSets[0].Rows[0][0]);
        Assert.Equal("15", exec.ResultSets[1].Rows[0][0]);
        Assert.Equal("42", exec.ResultSets[2].Rows[0][0]);
    }

    [Fact]
    public async Task RowLimit_TruncatesResult()
    {
        var exec = await _queries.ExecuteAsync("SELECT * FROM lake.sales_orders", 50, Reader);
        var rs = Assert.Single(exec.ResultSets);
        Assert.Equal(50, rs.Rows.Count);
        Assert.True(rs.Truncated);
    }

    [Fact]
    public async Task Sandbox_BlocksFilesOutsideDataSources()
    {
        var exec = await _queries.ExecuteAsync("SELECT * FROM read_csv('/etc/hostname')", 10, Admin);
        Assert.NotNull(exec.Error);
        var locked = await _queries.ExecuteAsync("SET enable_external_access = true", 10, Admin);
        Assert.NotNull(locked.Error);
    }

    [Fact]
    public async Task ReadOnlyUsers_CannotWrite_AdminsCan()
    {
        var denied = await _queries.ExecuteAsync("CREATE TABLE t AS SELECT 1", 10, Reader);
        Assert.Contains("read-only", denied.Error);
        var allowed = await _queries.ExecuteAsync("CREATE TEMP TABLE t AS SELECT 1 AS x; SELECT x FROM t", 10, Admin);
        Assert.Null(allowed.Error);
    }

    [Fact]
    public async Task Cancellation_StopsLongQueries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exec = await _queries.ExecuteAsync("SELECT count(*) FROM range(1000000000000)", 10, Reader, cancellationToken: cts.Token);
        Assert.True(exec.Cancelled);
    }
}
