namespace ParquetExplorer.Services;

public sealed record ResultColumn(string Name, string Type, bool IsNumeric);

public sealed class ResultSet
{
    public required IReadOnlyList<ResultColumn> Columns { get; init; }

    /// <summary>Formatted cell values; null means SQL NULL.</summary>
    public required List<string?[]> Rows { get; init; }

    /// <summary>True when more rows were available than the row limit.</summary>
    public bool Truncated { get; init; }

    public int? RecordsAffected { get; init; }
    public string Statement { get; init; } = "";
}

public sealed class QueryExecution
{
    public List<ResultSet> ResultSets { get; } = [];

    /// <summary>Informational messages (e.g. partition pruning), shown in the Messages pane.</summary>
    public List<string> Notes { get; } = [];
    public TimeSpan Elapsed { get; set; }
    public string? Error { get; set; }
    public bool Cancelled { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public long TotalRows => ResultSets.Sum(r => (long)r.Rows.Count);
}

public sealed record HistoryEntry(string Sql, DateTimeOffset At, TimeSpan Elapsed, long Rows, string? Error);
