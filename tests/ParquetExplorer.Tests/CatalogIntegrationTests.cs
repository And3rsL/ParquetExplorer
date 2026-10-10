using System.Security.Claims;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ParquetExplorer.Options;
using ParquetExplorer.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ParquetExplorer.Tests;

/// <summary>End-to-end: writes a small lake to disk, discovers it and queries it through the sandboxed engine.</summary>
public sealed class CatalogIntegrationTests : IAsyncLifetime
{
    private readonly string _root = TempDir.Create();
    private readonly string _other = TempDir.Create();
    private readonly string _state = TempDir.Create();
    private ExplorerOptions _options = default!;
    private DuckDbEngine _engine = default!;
    private SourceRegistry _registry = default!;
    private CatalogService _catalog = default!;
    private QueryService _queries = default!;
    private UserStore _users = default!;

    private static readonly ClaimsPrincipal Reader = Principal("reader");
    private static readonly ClaimsPrincipal Admin = Principal("admin");

    private static ClaimsPrincipal Principal(string name) => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sales", "orders"));
        Directory.CreateDirectory(Path.Combine(_root, "crm", "customers"));
        Directory.CreateDirectory(Path.Combine(_root, "crm", "_temporary"));
        Directory.CreateDirectory(Path.Combine(_other, "events"));
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
                COPY (SELECT i AS event_id FROM range(7) t(i)) TO '{_other}/events/e.parquet' (FORMAT parquet);
                """;
            cmd.ExecuteNonQuery();
        }

        _options = new ExplorerOptions
        {
            StateDirectory = _state,
            DataSources = [new DataSourceOptions { Name = "lake", Type = DataSourceType.Local, Path = _root }],
            Query = new QueryOptions { TempDirectory = Path.Combine(_state, "tmp"), TimeoutSeconds = 30 },
            Auth = new AuthOptions { Users = [new UserOptions { Username = "admin", PasswordHash = UserStore.HashPassword("x"), Roles = [Roles.Admin] }] },
        };
        var options = MsOptions.Create(_options);
        _users = new UserStore(new StaticMonitor(_options), NullLogger<UserStore>.Instance);
        _users.Create("reader", null, "password1", isAdmin: false, by: "admin");
        _engine = new DuckDbEngine(options, NullLogger<DuckDbEngine>.Instance);
        _registry = new SourceRegistry(options, new EphemeralDataProtectionProvider(), NullLogger<SourceRegistry>.Instance);
        _catalog = new CatalogService(_engine, _registry, NullLogger<CatalogService>.Instance);
        _queries = new QueryService(_engine, new QueryHistoryService(options), _users, options, NullLogger<QueryService>.Instance);
        await _catalog.ReloadAsync();
    }

    public Task DisposeAsync()
    {
        _engine.Dispose();
        TempDir.Delete(_root);
        TempDir.Delete(_other);
        TempDir.Delete(_state);
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
        var exec = await _queries.ExecuteAsync($"SELECT * FROM read_parquet('{_other}/events/e.parquet')", 10, Admin);
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
    public async Task PromotedUser_GetsWriteAccess_DisabledUser_IsBlocked()
    {
        _users.SetRole("reader", true, "admin");
        var promoted = await _queries.ExecuteAsync("CREATE TEMP TABLE p AS SELECT 1", 10, Reader);
        Assert.Null(promoted.Error);

        _users.SetDisabled("reader", true, "admin");
        var blocked = await _queries.ExecuteAsync("SELECT 1", 10, Reader);
        Assert.Contains("disabled", blocked.Error);
    }

    [Fact]
    public async Task Cancellation_StopsLongQueries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exec = await _queries.ExecuteAsync("SELECT count(*) FROM range(1000000000000)", 10, Reader, cancellationToken: cts.Token);
        Assert.True(exec.Cancelled);
    }

    [Fact]
    public async Task AddingASource_ReloadsTheSandboxWithoutRestart()
    {
        var newSource = new DataSourceOptions { Name = "events", Type = DataSourceType.Local, Path = _other };
        Assert.Null(_registry.Validate(newSource, null));

        var test = await _catalog.TestAsync(newSource);
        Assert.True(test.Success, test.Message);
        Assert.Equal(1, test.Tables);

        _registry.Save(newSource, null, "admin");
        await _catalog.ReloadAsync();

        var exec = await _queries.ExecuteAsync("SELECT count(*) FROM events.events", 10, Reader);
        Assert.Null(exec.Error);
        Assert.Equal("7", exec.ResultSets[0].Rows[0][0]);

        // Removing it closes access to the folder again.
        _registry.Delete("events", "admin");
        await _catalog.ReloadAsync();
        var after = await _queries.ExecuteAsync($"SELECT count(*) FROM read_parquet('{_other}/events/e.parquet')", 10, Admin);
        Assert.NotNull(after.Error);
        Assert.DoesNotContain(_catalog.Sources, s => s.Name == "events");
    }

    [Fact]
    public void SourceValidation_RejectsBadInput()
    {
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "lake", Type = DataSourceType.Local, Path = _other }, null)); // duplicate
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "1bad", Type = DataSourceType.Local, Path = _other }, null));
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "main", Type = DataSourceType.Local, Path = _other }, null));
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.Local, Path = "relative/path" }, null));
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.Local, Path = "/does/not/exist" }, null));
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.S3 }, null)); // no bucket
        Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.S3, Bucket = "b", AccessKeyId = "k" }, null)); // key without secret
    }

    [Fact]
    public void AllowedLocalRoots_AreEnforced()
    {
        _options.SourceManagement.AllowedLocalRoots = [_root];
        try
        {
            Assert.NotNull(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.Local, Path = _other }, null));
            Assert.Null(_registry.Validate(new DataSourceOptions { Name = "x", Type = DataSourceType.Local, Path = Path.Combine(_root, "crm") }, null));
        }
        finally
        {
            _options.SourceManagement.AllowedLocalRoots = [];
        }
    }

    [Fact]
    public void S3Secrets_AreEncryptedOnDisk()
    {
        var s3 = new DataSourceOptions { Name = "bucket", Type = DataSourceType.S3, Bucket = "my-bucket", AccessKeyId = "AKIAEXAMPLE", SecretAccessKey = "super-secret-value" };
        Assert.Null(_registry.Validate(s3, null));
        _registry.Save(s3, null, "admin");
        try
        {
            var json = File.ReadAllText(Path.Combine(_state, "sources.json"));
            Assert.DoesNotContain("super-secret-value", json);
            Assert.Equal("super-secret-value", _registry.Find("bucket")!.Options.SecretAccessKey);
        }
        finally
        {
            _registry.Delete("bucket", "admin");
        }
    }
}
