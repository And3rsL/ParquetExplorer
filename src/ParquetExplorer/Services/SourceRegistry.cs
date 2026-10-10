using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

public sealed record SourceDefinition(DataSourceOptions Options, bool IsManaged, DateTimeOffset? CreatedAt = null, string? CreatedBy = null);

/// <summary>
/// The data sources: the ones from configuration (read-only) plus the ones managed from the
/// admin UI, persisted to <c>sources.json</c> with S3 secrets encrypted by ASP.NET Data Protection.
/// </summary>
public sealed partial class SourceRegistry
{
    private sealed class StoredSource
    {
        public DataSourceOptions Source { get; set; } = new();
        public string? ProtectedSecretAccessKey { get; set; }
        public string? ProtectedSessionToken { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    private sealed class Document
    {
        public List<StoredSource> Sources { get; set; } = [];
    }

    private readonly ExplorerOptions _options;
    private readonly IDataProtector _protector;
    private readonly ILogger<SourceRegistry> _logger;
    private readonly JsonFileStore<Document> _store;
    private readonly Lock _lock = new();
    private List<SourceDefinition> _managed;

    public SourceRegistry(IOptions<ExplorerOptions> options, IDataProtectionProvider dataProtection, ILogger<SourceRegistry> logger)
    {
        _options = options.Value;
        _protector = dataProtection.CreateProtector("ParquetExplorer.SourceSecrets");
        _logger = logger;
        _store = new JsonFileStore<Document>(Path.Combine(_options.StateDirectory, "sources.json"));
        _managed = LoadManaged();
    }

    public IReadOnlyList<SourceDefinition> All
    {
        get
        {
            lock (_lock)
                return _options.DataSources.Select(s => new SourceDefinition(s, false)).Concat(_managed).ToList();
        }
    }

    public IReadOnlyList<DataSourceOptions> AllOptions => All.Select(d => d.Options).ToList();

    public SourceDefinition? Find(string name) =>
        All.FirstOrDefault(s => s.Options.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,62}$")]
    private static partial Regex NamePattern();

    /// <summary>Validates and normalizes a source. Returns the error message, or null when valid.</summary>
    public string? Validate(DataSourceOptions s, string? originalName)
    {
        s.Name = s.Name.Trim();
        if (!NamePattern().IsMatch(s.Name))
            return "Name must start with a letter and contain only letters, digits and '_' (it becomes the SQL schema).";
        if (s.Name.ToLowerInvariant() is "main" or "temp" or "information_schema" or "pg_catalog" or "system")
            return $"'{s.Name}' is a reserved name.";
        var clash = Find(s.Name);
        if (clash is not null && !s.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase))
            return $"A source named '{clash.Options.Name}' already exists.";

        if (s.Type == DataSourceType.Local)
        {
            if (string.IsNullOrWhiteSpace(s.Path)) return "Folder path is required.";
            if (!System.IO.Path.IsPathFullyQualified(s.Path.Trim())) return "Use an absolute folder path.";
            s.Path = System.IO.Path.GetFullPath(s.Path.Trim());
            if (!Directory.Exists(s.Path)) return $"Folder not found on the server: {s.Path}";

            var roots = _options.SourceManagement.AllowedLocalRoots;
            if (roots.Length > 0 && !roots.Any(r => IsUnder(s.Path, r)))
                return $"Folder must be inside one of the allowed roots: {string.Join(", ", roots)}.";
            ClearS3(s);
        }
        else
        {
            s.Bucket = s.Bucket?.Trim();
            s.Prefix = string.IsNullOrWhiteSpace(s.Prefix) ? null : s.Prefix.Trim().Trim('/');
            s.Endpoint = string.IsNullOrWhiteSpace(s.Endpoint) ? null : Regex.Replace(s.Endpoint.Trim(), "^https?://", "").TrimEnd('/');
            s.Region = string.IsNullOrWhiteSpace(s.Region) ? null : s.Region.Trim();
            s.UrlStyle = string.IsNullOrWhiteSpace(s.UrlStyle) ? null : s.UrlStyle;
            if (string.IsNullOrWhiteSpace(s.Bucket) || !Regex.IsMatch(s.Bucket, "^[a-z0-9][a-z0-9.\\-_]{1,254}$"))
                return "A valid bucket name is required.";
            if (!s.UseCredentialChain && string.IsNullOrWhiteSpace(s.AccessKeyId) != string.IsNullOrWhiteSpace(s.SecretAccessKey))
                return "Provide both access key ID and secret access key (or neither for public buckets).";
            s.Path = null;
        }
        return null;
    }

    /// <summary>Adds or replaces a managed source. <paramref name="originalName"/> is null for a new source.</summary>
    public void Save(DataSourceOptions source, string? originalName, string user)
    {
        lock (_lock)
        {
            var existing = originalName is null ? null
                : _managed.FirstOrDefault(m => m.Options.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));
            if (originalName is not null && existing is null)
                throw new InvalidOperationException("Only sources created from the UI can be edited.");

            var def = new SourceDefinition(source.Clone(), true, existing?.CreatedAt ?? DateTimeOffset.Now, existing?.CreatedBy ?? user);
            var list = _managed.Where(m => m != existing).Append(def).ToList();
            Persist(list);
            _managed = list;
        }
        _logger.LogInformation("Source {Source} {Action} by {User}", source.Name, originalName is null ? "added" : "updated", user);
    }

    public void Delete(string name, string user)
    {
        lock (_lock)
        {
            var existing = _managed.FirstOrDefault(m => m.Options.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Only sources created from the UI can be deleted.");
            var list = _managed.Where(m => m != existing).ToList();
            Persist(list);
            _managed = list;
        }
        _logger.LogInformation("Source {Source} deleted by {User}", name, user);
    }

    private void Persist(List<SourceDefinition> list)
    {
        var doc = new Document
        {
            Sources = list.Select(d =>
            {
                var copy = d.Options.Clone();
                var stored = new StoredSource
                {
                    Source = copy,
                    ProtectedSecretAccessKey = string.IsNullOrEmpty(copy.SecretAccessKey) ? null : _protector.Protect(copy.SecretAccessKey),
                    ProtectedSessionToken = string.IsNullOrEmpty(copy.SessionToken) ? null : _protector.Protect(copy.SessionToken),
                    CreatedAt = d.CreatedAt ?? DateTimeOffset.Now,
                    CreatedBy = d.CreatedBy,
                };
                copy.SecretAccessKey = null;
                copy.SessionToken = null;
                return stored;
            }).ToList(),
        };
        _store.Save(doc);
    }

    private List<SourceDefinition> LoadManaged()
    {
        try
        {
            return _store.Load().Sources.Select(s =>
            {
                s.Source.SecretAccessKey = Unprotect(s.ProtectedSecretAccessKey, s.Source.Name);
                s.Source.SessionToken = Unprotect(s.ProtectedSessionToken, s.Source.Name);
                return new SourceDefinition(s.Source, true, s.CreatedAt, s.CreatedBy);
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot read {File}", _store.Path);
            return [];
        }
    }

    private string? Unprotect(string? value, string source)
    {
        if (value is null) return null;
        try
        {
            return _protector.Unprotect(value);
        }
        catch (Exception)
        {
            _logger.LogError("Cannot decrypt the stored secret of source {Source} (data protection keys changed?). Re-enter it from the admin page.", source);
            return null;
        }
    }

    private static void ClearS3(DataSourceOptions s)
    {
        s.Bucket = s.Prefix = s.Region = s.Endpoint = s.AccessKeyId = s.SecretAccessKey = s.SessionToken = s.UrlStyle = null;
        s.UseCredentialChain = false;
        s.UseSsl = true;
    }

    private static bool IsUnder(string path, string root)
    {
        var full = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (path.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar).StartsWith(full, cmp);
    }
}
