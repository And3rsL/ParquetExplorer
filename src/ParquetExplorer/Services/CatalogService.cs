using System.Collections.Concurrent;
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
    private readonly ExplorerOptions _options;
    private readonly ILogger<CatalogService> _logger;
    private readonly ConcurrentDictionary<string, SourceInfo> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _ddlLock = new(1, 1);

    public event Action? Changed;

    public CatalogService(DuckDbEngine engine, IOptions<ExplorerOptions> options, ILogger<CatalogService> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;

        var usedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "information_schema", "pg_catalog", "temp" };
        foreach (var src in _options.DataSources)
        {
            var schema = Unique(SqlText.ToIdentifier(src.Name), usedSchemas);
            _sources[src.Name] = new SourceInfo
            {
                Name = src.Name,
                Schema = schema,
                Type = src.Type,
                RootUri = src.RootUri,
                Loading = true,
            };
        }
    }

    public IReadOnlyList<SourceInfo> Sources =>
        _options.DataSources.Select(s => _sources[s.Name]).ToList();

    public IEnumerable<TableInfo> AllTables => Sources.SelectMany(s => s.Tables);

    public TableInfo? FindTable(string schema, string name) =>
        AllTables.FirstOrDefault(t => t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)
                                   && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>schema → table → columns, used by the editor for auto-completion.</summary>
    public Dictionary<string, Dictionary<string, string[]>> CompletionSchema() =>
        Sources.ToDictionary(
            s => s.Schema,
            s => s.Tables.ToDictionary(t => t.Name, t => t.Columns.Select(c => c.Name).ToArray()));

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(_options.DataSources.Select(s => RefreshAsync(s.Name, ct)));
    }

    public async Task RefreshAsync(string sourceName, CancellationToken ct = default)
    {
        var opts = _options.DataSources.First(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
        var info = _sources[opts.Name];
        info.Loading = true;
        Changed?.Invoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var engineError = _engine.GetSourceError(opts.Name);
            if (engineError is not null) throw new InvalidOperationException(engineError);

            var files = await Task.Run(() => ListFiles(opts), ct);
            var tables = BuildTables(opts, info.Schema, files);
            await DescribeTablesAsync(opts, tables, ct);
            await CreateViewsAsync(info, tables, ct);

            info.Tables = tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            info.Error = null;
            _logger.LogInformation("Source {Source}: {Tables} tables, {Files} files in {Elapsed} ms",
                opts.Name, tables.Count, files.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Cannot load source {Source}", opts.Name);
            info.Error = ex.Message;
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

    private List<FileEntry> ListFiles(DataSourceOptions src)
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
        using var conn = _engine.OpenConnection();
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

    private List<TableInfo> BuildTables(DataSourceOptions src, string schema, List<FileEntry> files)
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
    private async Task DescribeTablesAsync(DataSourceOptions src, List<TableInfo> tables, CancellationToken ct)
    {
        var parallelism = src.Type == DataSourceType.S3 ? 16 : Math.Max(4, Environment.ProcessorCount);
        await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            (t, _) =>
            {
                try
                {
                    using var conn = _engine.OpenConnection();
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

    private async Task CreateViewsAsync(SourceInfo info, List<TableInfo> tables, CancellationToken ct)
    {
        var src = _options.DataSources.First(s => s.Name == info.Name);
        await _ddlLock.WaitAsync(ct);
        try
        {
            using var conn = _engine.OpenConnection();
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
                ct.ThrowIfCancellationRequested();
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
        finally
        {
            _ddlLock.Release();
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
        await catalog.RefreshAllAsync(stoppingToken);
        var minutes = options.Value.CatalogRefreshMinutes;
        if (minutes <= 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await catalog.RefreshAllAsync(stoppingToken);
    }
}
