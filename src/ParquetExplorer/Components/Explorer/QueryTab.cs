using Microsoft.AspNetCore.Components;
using ParquetExplorer.Services;

namespace ParquetExplorer.Components.Explorer;

/// <summary>State of one query window (editor + results).</summary>
public sealed class QueryTab
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "Query";
    public string InitialSql { get; set; } = "";
    public int? EditorId { get; set; }
    public ElementReference EditorElement;

    public bool Running { get; set; }
    public double? Progress { get; set; }
    public DateTimeOffset? RunningSince { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public QueryExecution? Execution { get; set; }
    public string? LastSql { get; set; }

    /// <summary>Index of the visible result set; -1 shows the messages pane.</summary>
    public int ResultIndex { get; set; }

    /// <summary>Execute the whole editor content once the editor is created.</summary>
    public bool RunOnCreate { get; set; }
}

public sealed record SavedTab(string Title, string Sql);
