using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

/// <summary>
/// One sandboxed in-memory DuckDB database. Every query runs on a lightweight duplicated
/// connection, so queries execute in parallel while sharing catalog (views), caches and buffer pool.
/// </summary>
/// <remarks>
/// Once built, file system access is limited to the given data sources (plus the temp folder) and
/// the configuration is locked, so users cannot change settings, read arbitrary files or leak the
/// S3 credentials. Changing the set of sources therefore means building a new instance.
/// </remarks>
public sealed class EngineInstance : IDisposable
{
    private readonly DuckDBConnection _root;
    private readonly Dictionary<string, string> _sourceErrors = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; }

    internal EngineInstance(IReadOnlyList<DataSourceOptions> sources, QueryOptions q, string tempDir, ILogger logger)
    {
        _root = new DuckDBConnection("Data Source=:memory:");
        _root.Open();
        try
        {
            Exec($"SET temp_directory = {SqlText.Literal(Path.Combine(tempDir, "spill"))}");
            if (q.Threads > 0) Exec($"SET threads = {q.Threads}");
            if (!string.IsNullOrWhiteSpace(q.MemoryLimit)) Exec($"SET memory_limit = {SqlText.Literal(q.MemoryLimit)}");
            // Keeps parquet footers (schema, row group statistics) in memory between queries.
            if (q.EnableObjectCache) Exec("SET parquet_metadata_cache = true");
            // Late materialization turns "SELECT * ... LIMIT n" into a second scan joined on row numbers:
            // over thousands of files it reads every file instead of stopping after n rows.
            if (!q.LateMaterialization) Exec("SET disabled_optimizers = 'late_materialization'");
            if (!string.IsNullOrWhiteSpace(q.ExtensionDirectory))
            {
                Directory.CreateDirectory(q.ExtensionDirectory);
                Exec($"SET extension_directory = {SqlText.Literal(Path.GetFullPath(q.ExtensionDirectory))}");
            }

            var s3Sources = sources.Where(s => s.Type == DataSourceType.S3).ToList();
            if (s3Sources.Count > 0)
            {
                var httpfsOk = TryExec("INSTALL httpfs; LOAD httpfs;", out var httpfsError);
                if (!httpfsOk)
                    logger.LogError("Cannot load DuckDB httpfs extension, S3 sources disabled: {Error}", httpfsError);
                if (httpfsOk && s3Sources.Any(s => s.UseCredentialChain) && !TryExec("INSTALL aws; LOAD aws;", out var awsError))
                    logger.LogWarning("Cannot load DuckDB aws extension (credential chain): {Error}", awsError);

                // Caches object metadata (HEAD requests) across queries.
                if (httpfsOk && q.EnableObjectCache) TryExec("SET enable_http_metadata_cache = true", out _);

                foreach (var src in s3Sources)
                {
                    if (!httpfsOk)
                    {
                        _sourceErrors[src.Name] = "httpfs extension not available: " + httpfsError;
                        continue;
                    }
                    if (!TryExec(BuildSecret(src), out var secretError))
                    {
                        _sourceErrors[src.Name] = secretError;
                        logger.LogError("Cannot configure credentials for source {Source}: {Error}", src.Name, secretError);
                    }
                }
            }

            // Sandbox: only the data sources and our temp folder are reachable from SQL.
            var allowed = sources
                .Where(s => !_sourceErrors.ContainsKey(s.Name))
                .Select(s => s.RootUri)
                .Append(tempDir + Path.DirectorySeparatorChar)
                .Distinct()
                .Select(SqlText.Literal);
            Exec($"SET allowed_directories = [{string.Join(", ", allowed)}]");
            Exec("SET enable_external_access = false");
            Exec("SET autoinstall_known_extensions = false");
            Exec("SET lock_configuration = true");

            using var cmd = _root.CreateCommand();
            cmd.CommandText = "SELECT version()";
            Version = cmd.ExecuteScalar()?.ToString() ?? "?";
        }
        catch
        {
            _root.Dispose();
            throw;
        }
    }

    /// <summary>Error that prevented a data source from being configured, if any.</summary>
    public string? GetSourceError(string sourceName) => _sourceErrors.GetValueOrDefault(sourceName);

    /// <summary>Opens a new connection on this database. Dispose it after use.</summary>
    public DuckDBConnection OpenConnection()
    {
        var c = _root.Duplicate();
        if (c.State != System.Data.ConnectionState.Open) c.Open();
        return c;
    }

    private static string BuildSecret(DataSourceOptions s)
    {
        var parts = new List<string> { "TYPE s3" };
        if (s.UseCredentialChain)
        {
            parts.Add("PROVIDER credential_chain");
        }
        else
        {
            if (!string.IsNullOrEmpty(s.AccessKeyId)) parts.Add($"KEY_ID {SqlText.Literal(s.AccessKeyId)}");
            if (!string.IsNullOrEmpty(s.SecretAccessKey)) parts.Add($"SECRET {SqlText.Literal(s.SecretAccessKey)}");
            if (!string.IsNullOrEmpty(s.SessionToken)) parts.Add($"SESSION_TOKEN {SqlText.Literal(s.SessionToken)}");
        }
        if (!string.IsNullOrEmpty(s.Region)) parts.Add($"REGION {SqlText.Literal(s.Region)}");
        if (!string.IsNullOrEmpty(s.Endpoint)) parts.Add($"ENDPOINT {SqlText.Literal(s.Endpoint)}");
        if (!string.IsNullOrEmpty(s.UrlStyle)) parts.Add($"URL_STYLE {SqlText.Literal(s.UrlStyle)}");
        if (!s.UseSsl) parts.Add("USE_SSL false");
        // Scoped to the source prefix, so two sources on the same bucket can use different keys.
        parts.Add($"SCOPE {SqlText.Literal(s.RootUri)}");

        var name = SqlText.Ident("src_" + SqlText.ToIdentifier(s.Name));
        return $"CREATE OR REPLACE SECRET {name} ({string.Join(", ", parts)})";
    }

    private void Exec(string sql)
    {
        using var cmd = _root.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool TryExec(string sql, out string error)
    {
        try
        {
            Exec(sql);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Releases this instance's root connection. Queries still running on duplicated connections
    /// keep the database alive until they finish.
    /// </summary>
    public void Dispose() => _root.Dispose();
}

/// <summary>
/// Holds the current <see cref="EngineInstance"/> and builds new ones when the data sources change.
/// </summary>
public sealed class DuckDbEngine : IDisposable
{
    private readonly ILogger<DuckDbEngine> _logger;
    private readonly QueryOptions _query;
    private readonly string _tempDir;
    private EngineInstance _current;

    public string ExportDirectory { get; }
    public string Version => _current.Version;
    public EngineInstance Current => _current;

    public DuckDbEngine(IOptions<ExplorerOptions> options, ILogger<DuckDbEngine> logger)
    {
        _logger = logger;
        _query = options.Value.Query;
        _tempDir = Path.GetFullPath(string.IsNullOrWhiteSpace(_query.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "parquet-explorer")
            : _query.TempDirectory);
        ExportDirectory = Path.Combine(_tempDir, "exports");
        Directory.CreateDirectory(ExportDirectory);

        // Empty until the catalog builds the real instance with the configured sources.
        _current = new EngineInstance([], _query, _tempDir, logger);
        _logger.LogInformation("DuckDB {Version} ready (temp: {Temp})", _current.Version, _tempDir);
    }

    /// <summary>Builds a new sandboxed database for the given sources (not yet active).</summary>
    public EngineInstance Build(IReadOnlyList<DataSourceOptions> sources) =>
        new(sources, _query, _tempDir, _logger);

    /// <summary>Makes <paramref name="next"/> the active database and releases the previous one.</summary>
    public void Activate(EngineInstance next)
    {
        var old = Interlocked.Exchange(ref _current, next);
        if (!ReferenceEquals(old, next)) old.Dispose();
    }

    /// <summary>Opens a new connection on the active database. Dispose it after use.</summary>
    public DuckDBConnection OpenConnection() => _current.OpenConnection();

    /// <summary>Downloads the extensions needed for S3 (used to pre-bake container images).</summary>
    public static void InstallExtensions(string? extensionDirectory)
    {
        using var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        var dir = string.IsNullOrWhiteSpace(extensionDirectory) ? "" : $"SET extension_directory = {SqlText.Literal(Path.GetFullPath(extensionDirectory))};";
        cmd.CommandText = dir + "INSTALL httpfs; INSTALL aws;";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _current.Dispose();
}
