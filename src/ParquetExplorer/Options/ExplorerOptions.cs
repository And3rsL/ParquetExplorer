namespace ParquetExplorer.Options;

public enum DataSourceType
{
    Local,
    S3
}

/// <summary>
/// A root location that contains parquet files. Each folder below it becomes a table.
/// </summary>
public sealed class DataSourceOptions
{
    /// <summary>Logical name, used as SQL schema (e.g. <c>SELECT * FROM lake.sales</c>).</summary>
    public string Name { get; set; } = "";

    public DataSourceType Type { get; set; } = DataSourceType.Local;

    /// <summary>Local folder (Type=Local).</summary>
    public string? Path { get; set; }

    /// <summary>Bucket name (Type=S3).</summary>
    public string? Bucket { get; set; }

    /// <summary>Optional key prefix inside the bucket (Type=S3).</summary>
    public string? Prefix { get; set; }

    public string? Region { get; set; }

    /// <summary>Custom endpoint for S3-compatible storage (MinIO, R2, Ceph...). Host[:port], without scheme.</summary>
    public string? Endpoint { get; set; }

    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }

    /// <summary>Use the AWS credential chain (env vars, profile, instance role) instead of static keys.</summary>
    public bool UseCredentialChain { get; set; }

    /// <summary>"vhost" (AWS default) or "path" (typical for MinIO).</summary>
    public string? UrlStyle { get; set; }

    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Merge schemas of all files by column name. Safer with evolving schemas, but has to read every
    /// file footer when the table is created (slower on remote storage).
    /// </summary>
    public bool UnionByName { get; set; }

    /// <summary>Parquet files directly in the root become one table each (default true).</summary>
    public bool RootFilesAsTables { get; set; } = true;

    /// <summary>File extensions considered parquet.</summary>
    public string[] Extensions { get; set; } = [".parquet", ".parq", ".pq"];

    public DataSourceOptions Clone() => (DataSourceOptions)MemberwiseClone();

    public string RootUri => Type switch
    {
        DataSourceType.S3 => $"s3://{Bucket}/{(string.IsNullOrWhiteSpace(Prefix) ? "" : Prefix.Trim('/') + "/")}",
        _ => System.IO.Path.GetFullPath(Path ?? ".").TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar
    };
}

public sealed class QueryOptions
{
    /// <summary>Rows shown by default in the result grid.</summary>
    public int DefaultRowLimit { get; set; } = 1000;

    /// <summary>Upper bound for rows loaded in the result grid (exports are unlimited).</summary>
    public int MaxRowLimit { get; set; } = 100_000;

    /// <summary>DuckDB worker threads (0 = number of cores).</summary>
    public int Threads { get; set; }

    /// <summary>DuckDB memory limit, e.g. "4GB" (empty = DuckDB default, 80% of RAM).</summary>
    public string? MemoryLimit { get; set; }

    /// <summary>Folder used by DuckDB to spill large operators and for exports.</summary>
    public string? TempDirectory { get; set; }

    /// <summary>Folder where DuckDB extensions (httpfs) are installed/cached.</summary>
    public string? ExtensionDirectory { get; set; }

    /// <summary>
    /// Cache parquet metadata (footers, row group statistics) and, on S3, object metadata between
    /// queries: repeated queries on the same files skip re-reading them. Uses a little memory.
    /// </summary>
    public bool EnableObjectCache { get; set; } = true;

    /// <summary>
    /// DuckDB's late materialization optimizer. Off by default: with many files it makes
    /// "SELECT * ... LIMIT n" scan every file twice (26 s instead of 4 s on 190k files).
    /// </summary>
    public bool LateMaterialization { get; set; }

    /// <summary>Query timeout in seconds (0 = none).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>History entries kept per user.</summary>
    public int HistorySize { get; set; } = 200;
}

public sealed class UserOptions
{
    public string Username { get; set; } = "";

    /// <summary>ASP.NET Core Identity v3 hash (PBKDF2). Generate with <c>ParquetExplorer hash-password</c>.</summary>
    public string PasswordHash { get; set; } = "";

    public string? DisplayName { get; set; }

    /// <summary>"Admin" allows non read-only statements (CREATE VIEW, COPY, ...) and the admin pages.</summary>
    public string[] Roles { get; set; } = [];
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
}

public sealed class AuthOptions
{
    public List<UserOptions> Users { get; set; } = [];

    /// <summary>Session cookie lifetime in hours.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>Failed attempts before the account is temporarily locked.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 5;
}

public sealed class SourceManagementOptions
{
    /// <summary>
    /// When not empty, sources added from the UI must point inside one of these local folders
    /// (e.g. ["/data"]). Empty = any folder readable by the server process.
    /// </summary>
    public string[] AllowedLocalRoots { get; set; } = [];
}

public sealed class ExplorerOptions
{
    public const string Section = "ParquetExplorer";

    /// <summary>
    /// Folder for data managed from the UI (users.json, sources.json) and, unless
    /// DataProtectionKeysPath is set, the keys protecting cookies and stored S3 secrets.
    /// Relative paths are resolved against the content root.
    /// </summary>
    public string StateDirectory { get; set; } = "App_Data";

    public SourceManagementOptions SourceManagement { get; set; } = new();

    public List<DataSourceOptions> DataSources { get; set; } = [];

    /// <summary>Re-scan data sources for new/removed folders every N minutes (0 = only on demand).</summary>
    public int CatalogRefreshMinutes { get; set; } = 10;

    public QueryOptions Query { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
}
