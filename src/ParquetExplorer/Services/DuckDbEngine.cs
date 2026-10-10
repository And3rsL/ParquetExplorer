using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

/// <summary>
/// Owns the single in-process DuckDB database. Every query runs on a lightweight duplicated
/// connection, so queries execute in parallel while sharing catalog (views), caches and buffer pool.
/// </summary>
/// <remarks>
/// After start-up the engine is sandboxed: file system access is limited to the configured data
/// sources (plus the temp folder) and the configuration is locked, so users cannot change settings,
/// read arbitrary files or leak the S3 credentials.
/// </remarks>
public sealed class DuckDbEngine : IDisposable
{
    private readonly DuckDBConnection _root;
    private readonly ILogger<DuckDbEngine> _logger;
    private readonly HashSet<string> _failedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceErrors = new(StringComparer.OrdinalIgnoreCase);

    public string ExportDirectory { get; }
    public string Version { get; }

    public DuckDbEngine(IOptions<ExplorerOptions> options, ILogger<DuckDbEngine> logger)
    {
        _logger = logger;
        var opts = options.Value;
        var q = opts.Query;

        var tempDir = Path.GetFullPath(string.IsNullOrWhiteSpace(q.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "parquet-explorer")
            : q.TempDirectory);
        ExportDirectory = Path.Combine(tempDir, "exports");
        Directory.CreateDirectory(ExportDirectory);

        _root = new DuckDBConnection("Data Source=:memory:");
        _root.Open();

        Exec($"SET temp_directory = {SqlText.Literal(Path.Combine(tempDir, "spill"))}");
        if (q.Threads > 0) Exec($"SET threads = {q.Threads}");
        if (!string.IsNullOrWhiteSpace(q.MemoryLimit)) Exec($"SET memory_limit = {SqlText.Literal(q.MemoryLimit)}");
        if (q.EnableObjectCache) Exec("SET enable_object_cache = true");
        if (!string.IsNullOrWhiteSpace(q.ExtensionDirectory))
        {
            Directory.CreateDirectory(q.ExtensionDirectory);
            Exec($"SET extension_directory = {SqlText.Literal(Path.GetFullPath(q.ExtensionDirectory))}");
        }

        var s3Sources = opts.DataSources.Where(s => s.Type == DataSourceType.S3).ToList();
        if (s3Sources.Count > 0)
        {
            var httpfsOk = TryExec("INSTALL httpfs; LOAD httpfs;", out var httpfsError);
            if (!httpfsOk)
            {
                _logger.LogError("Cannot load DuckDB httpfs extension, S3 sources disabled: {Error}", httpfsError);
            }
            if (httpfsOk && s3Sources.Any(s => s.UseCredentialChain) && !TryExec("INSTALL aws; LOAD aws;", out var awsError))
            {
                _logger.LogWarning("Cannot load DuckDB aws extension (credential chain): {Error}", awsError);
            }

            foreach (var src in s3Sources)
            {
                if (!httpfsOk)
                {
                    MarkFailed(src.Name, "httpfs extension not available: " + httpfsError);
                    continue;
                }
                if (!TryExec(BuildSecret(src), out var secretError))
                {
                    MarkFailed(src.Name, secretError);
                    _logger.LogError("Cannot configure credentials for source {Source}: {Error}", src.Name, secretError);
                }
            }
        }

        // Sandbox: only the data sources and our temp folder are reachable from SQL.
        var allowed = opts.DataSources
            .Where(s => !_failedSources.Contains(s.Name))
            .Select(s => s.RootUri)
            .Append(tempDir + Path.DirectorySeparatorChar)
            .Select(SqlText.Literal);
        Exec($"SET allowed_directories = [{string.Join(", ", allowed)}]");
        Exec("SET enable_external_access = false");
        Exec("SET autoinstall_known_extensions = false");
        Exec("SET lock_configuration = true");

        using var cmd = _root.CreateCommand();
        cmd.CommandText = "SELECT version()";
        Version = cmd.ExecuteScalar()?.ToString() ?? "?";
        _logger.LogInformation("DuckDB {Version} ready (temp: {Temp})", Version, tempDir);
    }

    /// <summary>Error that prevented a data source from being configured, if any.</summary>
    public string? GetSourceError(string sourceName) => _sourceErrors.GetValueOrDefault(sourceName);

    /// <summary>Opens a new connection on the shared database. Dispose it after use.</summary>
    public DuckDBConnection OpenConnection()
    {
        var c = _root.Duplicate();
        if (c.State != System.Data.ConnectionState.Open) c.Open();
        return c;
    }

    private void MarkFailed(string source, string error)
    {
        _failedSources.Add(source);
        _sourceErrors[source] = error;
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
        parts.Add($"SCOPE {SqlText.Literal($"s3://{s.Bucket}")}");

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

    public void Dispose() => _root.Dispose();
}
