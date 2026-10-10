using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

public sealed record ColumnInfo(string Name, string Type, bool IsPartitionKey);

/// <summary>A data file of a table and its partition values (aligned with <see cref="TableInfo.PartitionKeys"/>).</summary>
public sealed record TableFile(string Uri, string?[] PartitionValues);

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

    /// <summary>Every data file found by the scan: lets queries read only the partitions they filter on.</summary>
    public IReadOnlyList<TableFile> Files { get; init; } = [];

    /// <summary>
    /// False on network folders: there a DuckDB view over the whole table would list every folder
    /// at each query (minutes on SMB with tens of thousands of partitions), so queries are rewritten
    /// to read the files directly instead (see <see cref="PartitionPruner"/>).
    /// </summary>
    public bool HasView { get; set; } = true;

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

    /// <summary>UNC path or mapped network drive: listing and opening files costs a network round trip each.</summary>
    public bool IsNetwork { get; init; }
    public IReadOnlyList<TableInfo> Tables { get; set; } = [];
    public string? Error { get; set; }
    public bool Loading { get; set; }
    public DateTimeOffset? LastRefresh { get; set; }
    public TimeSpan? RefreshDuration { get; set; }
}
