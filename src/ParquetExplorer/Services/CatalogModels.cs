using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

public sealed record ColumnInfo(string Name, string Type, bool IsPartitionKey);

public sealed class TableInfo
{
    public required string Source { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>Folder (or file) path relative to the source root, '/' separated.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Glob(s) passed to read_parquet.</summary>
    public required IReadOnlyList<string> Locations { get; init; }

    public bool IsSingleFile { get; init; }
    public bool HivePartitioned { get; init; }
    public IReadOnlyList<string> PartitionKeys { get; init; } = [];

    /// <summary>DuckDB type of each partition key, inferred from the folder names during the scan.</summary>
    public IReadOnlyDictionary<string, string> PartitionTypes { get; init; } = new Dictionary<string, string>();

    /// <summary>Changes when files are added, removed or rewritten: unchanged tables are not rebuilt on refresh.</summary>
    public string Fingerprint { get; init; } = "";
    public int FileCount { get; init; }
    public long? TotalBytes { get; init; }
    public IReadOnlyList<ColumnInfo> Columns { get; set; } = [];
    public string? Error { get; set; }

    public string QualifiedName => SqlText.Qualified(Schema, Name);

    /// <summary>Name as a user would naturally type it (quotes only when needed).</summary>
    public string DisplayQualifiedName => $"{Schema}.{Name}";
}

public sealed class SourceInfo
{
    public required string Name { get; init; }
    public required string Schema { get; init; }
    public required DataSourceType Type { get; init; }
    public required string RootUri { get; init; }
    public required DataSourceOptions Options { get; init; }
    public IReadOnlyList<TableInfo> Tables { get; set; } = [];
    public string? Error { get; set; }
    public bool Loading { get; set; }
    public DateTimeOffset? LastRefresh { get; set; }
    public TimeSpan? RefreshDuration { get; set; }
}
