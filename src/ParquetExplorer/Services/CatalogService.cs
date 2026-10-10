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
    /// Sources whose settings did not change reuse their previous scan instead of listing files again.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var previous = _sources;
            var options = _registry.AllOptions;
            var infos = CreateInfos(options);
            var reuse = new Dictionary<SourceInfo, IReadOnlyList<TableInfo>?>();
            foreach (var info in infos)
            {
                var old = previous.FirstOrDefault(o => o.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase));
                // Keep showing the previous tables while loading.
                info.Tables = old?.Tables ?? [];
                reuse[info] = old is { Error: null, Loading: false, LastRefresh: not null } && old.Schema == info.Schema && SameSettings(old.Options, info.Options)
                    ? old.Tables
                    : null;
            }
            _sources = infos;
            Changed?.Invoke();

            var instance = await Task.Run(() => _engine.Build(options), ct);

            // At start-up nothing is queryable yet: activate right away so each source becomes
            // available as soon as it is loaded. Otherwise keep serving the old catalog until the
            // new one is complete.
            var progressive = !_loadedOnce;
            if (progressive) _engine.Activate(instance);
            try
            {
                await Task.WhenAll(infos.Select(i => LoadSourceAsync(i, instance, reuse[i], viewsExist: false, ct)));
            }
            catch when (!progressive)
            {
                instance.Dispose();
                throw;
            }
            if (!progressive) _engine.Activate(instance);
            _loadedOnce = true;
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    private bool _loadedOnce;

    private static bool SameSettings(DataSourceOptions a, DataSourceOptions b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    /// <summary>Re-scans every source for new, changed or removed folders (same database).</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var instance = _engine.Current;
            foreach (var i in _sources) i.Loading = true;
            Changed?.Invoke();
            await Task.WhenAll(_sources.Select(i => LoadSourceAsync(i, instance, reuseTables: null, viewsExist: true, ct)));
        }
        finally
        {
            _lock.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-scans one source for new, changed or removed folders (same database).</summary>
    public async Task RefreshAsync(string sourceName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var info = _sources.FirstOrDefault(s => s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
            if (info is null) return;
            info.Loading = true;
            Changed?.Invoke();
            await LoadSourceAsync(info, _engine.Current, reuseTables: null, viewsExist: true, ct);
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

    /// <param name="reuseTables">Tables from a previous scan to use instead of listing files again.</param>
    /// <param name="viewsExist">
    /// True when refreshing the active database: views of tables whose files did not change are kept as they are.
    /// </param>
    private async Task LoadSourceAsync(SourceInfo info, EngineInstance instance, IReadOnlyList<TableInfo>? reuseTables,
        bool viewsExist, CancellationToken ct)
    {
        var opts = info.Options;
        info.Loading = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var engineError = instance.GetSourceError(opts.Name);
            if (engineError is not null) throw new InvalidOperationException(engineError);

            List<TableInfo> tables;
            var fileCount = 0;
            if (reuseTables is not null)
            {
                tables = reuseTables.ToList();
            }
            else
            {
                var files = await Task.Run(() => ListFiles(opts, instance), ct);
                fileCount = files.Count;
                tables = BuildTables(opts, info.Schema, files);
            }

            // Views whose files did not change are kept (with their columns) when refreshing.
            var unchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (viewsExist)
            {
                var before = info.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var t in tables)
                {
                    if (before.TryGetValue(t.Name, out var old) && old.Error is null && old.Fingerprint == t.Fingerprint
                        && old.Locations.SequenceEqual(t.Locations))
                    {
                        t.Columns = old.Columns;
                        unchanged.Add(t.Name);
                    }
                }
            }

            var created = await Task.Run(() => CreateViews(info, tables, unchanged, instance, ct), ct);

            info.Tables = tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            info.Error = null;
            _logger.LogInformation("Source {Source}: {Tables} tables ({Created} (re)created), {Files} files in {Elapsed} ms",
                opts.Name, tables.Count, created, reuseTables is null ? fileCount : tables.Sum(t => t.FileCount), sw.ElapsedMilliseconds);
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

    private sealed record FileEntry(string Uri, string Relative, long? Size, DateTime? LastWriteUtc);

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
                .Select(f => new FileEntry(f.FullName, Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
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
            if (!IsIgnored(rel)) list.Add(new FileEntry(uri, rel, null, null));
        }
        return list;
    }

    /// <summary>Skips Spark/Hadoop markers and hidden folders (_temporary, .crc, _delta_log, ...).</summary>
    private static bool IsIgnored(string relative) =>
        relative.Split('/').Any(seg => seg.StartsWith('_') || seg.StartsWith('.'));

    [GeneratedRegex(@"^[^=/]+=[^/]*$")]
    private static partial Regex PartitionSegment();

    private sealed class TableGroup
    {
        public List<FileEntry> Files { get; } = [];
        public List<string> Keys { get; } = [];
        public Dictionary<string, HashSet<string>> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Exts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folder layout below the table folder, e.g. "year=*/month=*". Null when files are at mixed depths.</summary>
        public string? Layout { get; set; }
        public bool LayoutMixed { get; set; }
    }

    private static List<TableInfo> BuildTables(DataSourceOptions src, string schema, List<FileEntry> files)
    {
        var tables = new List<TableInfo>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sep = src.Type == DataSourceType.Local ? Path.DirectorySeparatorChar.ToString() : "/";

        // Group files by "table folder" = parent folder without trailing partition segments.
        var groups = new Dictionary<string, TableGroup>(StringComparer.Ordinal);
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
                g = new TableGroup();
                groups[key] = g;
            }
            g.Files.Add(f);
            g.Exts.Add(Path.GetExtension(f.Relative));

            var below = firstPartition >= 0 ? dirs[firstPartition..] : [];
            var layout = string.Join('/', below.Select(d => PartitionSegment().IsMatch(d) ? d[..d.IndexOf('=')] + "=*" : "*"));
            if (g.Layout is null && !g.LayoutMixed) g.Layout = layout;
            else if (g.Layout != layout) { g.LayoutMixed = true; g.Layout = null; }

            foreach (var d in below)
            {
                if (!PartitionSegment().IsMatch(d)) continue;
                var eq = d.IndexOf('=');
                var k = d[..eq];
                if (!g.Values.TryGetValue(k, out var values))
                {
                    values = new HashSet<string>(StringComparer.Ordinal);
                    g.Values[k] = values;
                    g.Keys.Add(k);
                }
                if (values.Count < 100_000) values.Add(d[(eq + 1)..]);
            }
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
                // A glob naming each level ("year=*/month=*/*.parquet") lists and matches less than "**".
                var pattern = !hive ? "*"
                    : g.Layout is { Length: > 0 } layout ? layout.Replace("/", sep) + sep + "*"
                    : "**" + sep + "*";
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
                PartitionKeys = g.Keys,
                PartitionTypes = g.Keys.ToDictionary(k => k, k => InferPartitionType(g.Values[k])),
                FileCount = g.Files.Count,
                TotalBytes = g.Files.All(f => f.Size.HasValue) ? g.Files.Sum(f => f.Size!.Value) : null,
                Fingerprint = Fingerprint(g.Files),
            });
        }
        return tables;
    }

    [GeneratedRegex(@"^-?\d{1,18}$")]
    private static partial Regex IntegerValue();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateValue();

    /// <summary>
    /// Type of a partition column from its folder values. Passing it to DuckDB avoids type
    /// auto-detection over every path on each bind.
    /// </summary>
    private static string InferPartitionType(HashSet<string> values)
    {
        if (values.Count == 0) return "VARCHAR";
        if (values.All(v => IntegerValue().IsMatch(v))) return "BIGINT";
        if (values.All(v => DateValue().IsMatch(v) && DateOnly.TryParseExact(v, "yyyy-MM-dd", out _))) return "DATE";
        return "VARCHAR";
    }

    /// <summary>Order independent fingerprint of a file set (names, sizes and modification times when known).</summary>
    private static string Fingerprint(List<FileEntry> files)
    {
        ulong sum = 0, xor = 0;
        foreach (var f in files)
        {
            var h = Fnv1a(f.Relative) ^ (ulong)(f.Size ?? 0) * 0x9E3779B97F4A7C15UL ^ (ulong)(f.LastWriteUtc?.Ticks ?? 0);
            sum += h;
            xor ^= h * 0xBF58476D1CE4E5B9UL;
        }
        return $"{files.Count}:{sum:x16}:{xor:x16}";
    }

    private static ulong Fnv1a(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    private static string ReadParquetExpr(DataSourceOptions src, TableInfo t)
    {
        var files = t.Locations.Count == 1
            ? SqlText.Literal(t.Locations[0])
            : "[" + string.Join(", ", t.Locations.Select(SqlText.Literal)) + "]";
        var args = new List<string> { files };
        if (t.HivePartitioned)
        {
            args.Add("hive_partitioning = true");
            args.Add("hive_types = {" + string.Join(", ", t.PartitionTypes.Select(kv => $"{SqlText.Literal(kv.Key)}: {kv.Value}")) + "}");
        }
        if (src.UnionByName || t.Locations.Count > 1) args.Add("union_by_name = true");
        return $"read_parquet({string.Join(", ", args)})";
    }

    /// <summary>
    /// Creates (or replaces) the views of a source in parallel and reads their columns from the catalog.
    /// Only a view's binding touches the files: DuckDB lists them and reads one footer for the schema.
    /// </summary>
    /// <returns>Number of views (re)created.</returns>
    private static int CreateViews(SourceInfo info, List<TableInfo> tables, HashSet<string> unchanged, EngineInstance instance, CancellationToken ct)
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
        var keep = tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existing.Where(v => !keep.Contains(v)))
            Exec($"DROP VIEW IF EXISTS {SqlText.Qualified(info.Schema, v)}");

        var todo = tables.Where(t => !unchanged.Contains(t.Name)).ToList();
        var parallelism = src.Type == DataSourceType.S3 ? 16 : Math.Clamp(Environment.ProcessorCount, 4, 16);
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, t =>
        {
            t.Error = null;
            try
            {
                using var c = instance.OpenConnection();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"CREATE OR REPLACE VIEW {t.QualifiedName} AS SELECT * FROM {ReadParquetExpr(src, t)}";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
                t.Columns = [];
            }
        });

        // Columns of the new views, straight from DuckDB's catalog (no file access).
        var columns = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT table_name, column_name, data_type FROM duckdb_columns() WHERE schema_name = {SqlText.Literal(info.Schema)} ORDER BY table_name, column_index";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var table = r.GetString(0);
                if (!columns.TryGetValue(table, out var list)) columns[table] = list = [];
                list.Add(new ColumnInfo(r.GetString(1), r.GetString(2), false));
            }
        }
        foreach (var t in todo.Where(t => t.Error is null))
        {
            t.Columns = columns.TryGetValue(t.Name, out var cols)
                ? cols.Select(c => c with { IsPartitionKey = t.PartitionKeys.Contains(c.Name) }).ToList()
                : [];
        }
        return todo.Count;
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
