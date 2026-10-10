using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

/// <summary>
/// Discovers parquet datasets in every data source and exposes each folder as a SQL view:
/// <c>&lt;source&gt;.&lt;folder_path&gt;</c>. Hive style partitions (<c>year=2024/month=01</c>) are folded
/// into their parent table and surface as columns.
/// </summary>
public sealed partial class CatalogService
{
    private readonly DuckDbEngine _engine;
    private readonly SourceRegistry _registry;
    private readonly ILogger<CatalogService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyList<SourceInfo> _sources = [];

    public event Action? Changed;

    public CatalogService(DuckDbEngine engine, SourceRegistry registry, ILogger<CatalogService> logger)
    {
        _engine = engine;
        _registry = registry;
        _logger = logger;
        _sources = CreateInfos(registry.AllOptions);
    }

    public IReadOnlyList<SourceInfo> Sources => _sources;

    public IEnumerable<TableInfo> AllTables => Sources.SelectMany(s => s.Tables);

    public TableInfo? FindTable(string schema, string name) =>
        AllTables.FirstOrDefault(t => t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)
                                   && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>schema → table → columns, used by the editor for auto-completion.</summary>
    public Dictionary<string, Dictionary<string, string[]>> CompletionSchema() =>
        Sources.ToDictionary(
            s => s.Schema,
            s => s.Tables.ToDictionary(t => t.Name, t => t.Columns.Select(c => c.Name).ToArray()));

    private static List<SourceInfo> CreateInfos(IReadOnlyList<DataSourceOptions> sources)
    {
        var usedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "information_schema", "pg_catalog", "temp", "system" };
        return sources.Select(src => new SourceInfo
        {
            Name = src.Name,
            Schema = Unique(SqlText.ToIdentifier(src.Name), usedSchemas),
            Type = src.Type,
            RootUri = src.RootUri,
            Options = src,
            Loading = true,
        }).ToList();
    }

    /// <summary>
    /// Rebuilds the whole catalog on a fresh sandboxed database (needed when sources are added,
    /// changed or removed) and switches to it atomically. Running queries finish on the old one.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var options = _registry.AllOptions;
            var infos = CreateInfos(options);
            // Keep showing the previous tables while loading, mark everything as loading.
            foreach (var info in infos)
                info.Tables = _sources.FirstOrDefault(o => o.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase))?.Tables ?? [];
            _sources = infos;
            Changed?.Invoke();

            var instance = await Task.Run(() => _engine.Build(options), ct);
            try
            {
                await Task.WhenAll(infos.Select(i => LoadSourceAsync(i, instance, ct)));
            }
            catch
            {
                instance.Dispose();
                throw;
            }
            _engine.Activate(instance);
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-scans every source for new or removed folders (same database).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var instance = _engine.Current;
            foreach (var i in _sources) i.Loading = true;
            Changed?.Invoke();
            await Task.WhenAll(_sources.Select(i => LoadSourceAsync(i, instance, ct)));
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-scans one source for new or removed folders (same database).</summary>
    public async Task RefreshAsync(string sourceName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var info = _sources.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
            if (info is null) return;
            info.Loading = true;
            Changed?.Invoke();
            await LoadSourceAsync(info, _engine.Current, ct);
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    public sealed record TestResult(bool Success, string Message, int Tables = 0, int Files = 0, IReadOnlyList<string>? SampleTables = null);

    /// <summary>Checks that a (not yet saved) source is reachable and lists the tables it would expose.</summary>
    public async Task<TestResult> TestAsync(DataSourceOptions src, CancellationToken ct = default)
    {
        try
        {
            using var instance = await Task.Run(() => _engine.Build([src]), ct);
            if (instance.GetSourceError(src.Name) is { } err) return new TestResult(false, err);
            var files = await Task.Run(() => ListFiles(src, instance), ct);
            var tables = BuildTables(src, "test", files);
            return new TestResult(true,
                files.Count == 0 ? "Connected, but no parquet files were found." : $"Connected: {tables.Count} table(s) from {files.Count} file(s).",
                tables.Count, files.Count, tables.Select(t => t.Name).Take(12).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TestResult(false, ex.Message);
        }
    }

    private async Task LoadSourceAsync(SourceInfo info, EngineInstance instance, CancellationToken ct)
    {
        var opts = info.Options;
        info.Loading = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var engineError = instance.GetSourceError(opts.Name);
            if (engineError is not null) throw new InvalidOperationException(engineError);

            var files = await Task.Run(() => ListFiles(opts, instance), ct);
            var tables = BuildTables(opts, info.Schema, files);
            await DescribeTablesAsync(opts, tables, instance, ct);
            await Task.Run(() => CreateViews(info, tables, instance), ct);

            info.Tables = tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            info.Error = null;
            _logger.LogInformation("Source {Source}: {Tables} tables, {Files} files in {Elapsed} ms",
                opts.Name, tables.Count, files.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Cannot load source {Source}: {Error}", opts.Name, ex.Message);
            info.Error = ex.Message;
            info.Tables = [];
        }
        finally
        {
            info.Loading = false;
            info.LastRefresh = DateTimeOffset.Now;
            info.RefreshDuration = sw.Elapsed;
            Changed?.Invoke();
        }
    }

    private sealed record FileEntry(string Uri, string Relative, long? Size);

    private static List<FileEntry> ListFiles(DataSourceOptions src, EngineInstance instance)
    {
        var exts = src.Extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
        bool IsParquet(string path) => exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        if (src.Type == DataSourceType.Local)
        {
            var root = src.RootUri;
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Folder not found: {root}");
            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            return new DirectoryInfo(root).EnumerateFiles("*", enumOpts)
                .Where(f => IsParquet(f.Name))
                .Select(f => new FileEntry(f.FullName, Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length))
                .Where(f => !IsIgnored(f.Relative))
                .ToList();
        }

        // S3: let DuckDB (httpfs) list the bucket, so it shares credentials and endpoint settings.
        var rootUri = src.RootUri;
        using var conn = instance.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Join(" UNION ALL ",
            exts.Select(e => $"SELECT file FROM glob({SqlText.Literal(rootUri + "**/*" + e)})"));
        using var reader = cmd.ExecuteReader();
        var list = new List<FileEntry>();
        while (reader.Read())
        {
            var uri = reader.GetString(0);
            if (!uri.StartsWith(rootUri, StringComparison.Ordinal)) continue;
            var rel = uri[rootUri.Length..];
            if (!IsIgnored(rel)) list.Add(new FileEntry(uri, rel, null));
        }
        return list;
    }

    /// <summary>Skips Spark/Hadoop markers and hidden folders (_temporary, .crc, _delta_log, ...).</summary>
    private static bool IsIgnored(string relative) =>
        relative.Split('/').Any(seg => seg.StartsWith('_') || seg.StartsWith('.'));

    [GeneratedRegex(@"^[^=/]+=[^/]*$")]
    private static partial Regex PartitionSegment();

    private static List<TableInfo> BuildTables(DataSourceOptions src, string schema, List<FileEntry> files)
    {
        var tables = new List<TableInfo>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sep = src.Type == DataSourceType.Local ? Path.DirectorySeparatorChar.ToString() : "/";

        // Group files by "table folder" = parent folder without trailing partition segments.
        var groups = new Dictionary<string, (List<FileEntry> Files, HashSet<string> Keys, HashSet<string> Exts)>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var segs = f.Relative.Split('/');
            var dirs = segs[..^1];
            var firstPartition = Array.FindIndex(dirs, d => PartitionSegment().IsMatch(d));
            var tableDirs = firstPartition >= 0 ? dirs[..firstPartition] : dirs;

            string key;
            if (tableDirs.Length == 0 && dirs.Length == 0 && src.RootFilesAsTables)
                key = "file:" + f.Relative; // loose file in the root → its own table
            else
                key = "dir:" + string.Join('/', tableDirs);

            if (!groups.TryGetValue(key, out var g))
            {
                g = ([], new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                groups[key] = g;
            }
            g.Files.Add(f);
            g.Exts.Add(Path.GetExtension(f.Relative));
            if (firstPartition >= 0)
                foreach (var d in dirs[firstPartition..])
                    if (PartitionSegment().IsMatch(d)) g.Keys.Add(d[..d.IndexOf('=')]);
        }

        foreach (var (key, g) in groups.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var isFile = key.StartsWith("file:", StringComparison.Ordinal);
            var rel = key[(key.IndexOf(':') + 1)..];
            var hive = g.Keys.Count > 0;

            string rawName;
            IReadOnlyList<string> locations;
            if (isFile)
            {
                rawName = Path.GetFileNameWithoutExtension(rel);
                locations = [g.Files[0].Uri];
            }
            else
            {
                rawName = rel.Length == 0 ? src.Name : rel;
                var dirUri = src.RootUri + (rel.Length == 0 ? "" : rel.Replace("/", sep) + sep);
                var pattern = hive ? "**" + sep + "*" : "*";
                locations = g.Exts.Select(e => dirUri + pattern + e).ToList();
            }

            tables.Add(new TableInfo
            {
                Source = src.Name,
                Schema = schema,
                Name = Unique(SqlText.ToIdentifier(rawName), usedNames),
                RelativePath = rel.Length == 0 ? "/" : rel,
                Locations = locations,
                IsSingleFile = isFile,
                HivePartitioned = hive,
                PartitionKeys = g.Keys.ToList(),
                FileCount = g.Files.Count,
                TotalBytes = g.Files.All(f => f.Size.HasValue) ? g.Files.Sum(f => f.Size!.Value) : null,
            });
        }
        return tables;
    }

    private static string ReadParquetExpr(DataSourceOptions src, TableInfo t)
    {
        var files = t.Locations.Count == 1
            ? SqlText.Literal(t.Locations[0])
            : "[" + string.Join(", ", t.Locations.Select(SqlText.Literal)) + "]";
        var args = new List<string> { files };
        if (t.HivePartitioned) args.Add("hive_partitioning = true");
        if (src.UnionByName || t.Locations.Count > 1) args.Add("union_by_name = true");
        return $"read_parquet({string.Join(", ", args)})";
    }

    /// <summary>Reads table schemas in parallel (only parquet footers are touched).</summary>
    private static async Task DescribeTablesAsync(DataSourceOptions src, List<TableInfo> tables, EngineInstance instance, CancellationToken ct)
    {
        var parallelism = src.Type == DataSourceType.S3 ? 16 : Math.Max(4, Environment.ProcessorCount);
        await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            (t, _) =>
            {
                try
                {
                    using var conn = instance.OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DESCRIBE SELECT * FROM {ReadParquetExpr(src, t)}";
                    using var r = cmd.ExecuteReader();
                    var cols = new List<ColumnInfo>();
                    while (r.Read())
                    {
                        var name = r.GetString(0);
                        cols.Add(new ColumnInfo(name, r.GetString(1), t.PartitionKeys.Contains(name)));
                    }
                    t.Columns = cols;
                }
                catch (Exception ex)
                {
                    t.Error = ex.Message;
                }
                return ValueTask.CompletedTask;
            });
    }

    private static void CreateViews(SourceInfo info, List<TableInfo> tables, EngineInstance instance)
    {
        var src = info.Options;
        using var conn = instance.OpenConnection();
        void Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"CREATE SCHEMA IF NOT EXISTS {SqlText.Ident(info.Schema)}");

        // Drop views of tables that disappeared.
        var existing = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT view_name FROM duckdb_views() WHERE schema_name = {SqlText.Literal(info.Schema)} AND NOT internal";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }
        var keep = tables.Where(t => t.Error is null).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existing.Where(v => !keep.Contains(v)))
            Exec($"DROP VIEW IF EXISTS {SqlText.Qualified(info.Schema, v)}");

        foreach (var t in tables.Where(t => t.Error is null))
        {
            try
            {
                Exec($"CREATE OR REPLACE VIEW {t.QualifiedName} AS SELECT * FROM {ReadParquetExpr(src, t)}");
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
            }
        }
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }
}

/// <summary>Loads the catalog at start-up and refreshes it periodically.</summary>
public sealed class CatalogRefreshService(CatalogService catalog, IOptions<ExplorerOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await catalog.ReloadAsync(stoppingToken);
        var minutes = options.Value.CatalogRefreshMinutes;
        if (minutes <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await catalog.RefreshAllAsync(stoppingToken);
    }
}
