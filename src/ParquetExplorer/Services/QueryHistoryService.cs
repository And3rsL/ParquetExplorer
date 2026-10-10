using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

/// <summary>Per-user in-memory query history.</summary>
public sealed class QueryHistoryService(IOptions<ExplorerOptions> options)
{
    private readonly ConcurrentDictionary<string, LinkedList<HistoryEntry>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _size = Math.Max(1, options.Value.Query.HistorySize);

    public event Action<string>? Changed;

    public void Add(string user, HistoryEntry entry)
    {
        var list = _history.GetOrAdd(user, _ => new LinkedList<HistoryEntry>());
        lock (list)
        {
            list.AddFirst(entry);
            while (list.Count > _size) list.RemoveLast();
        }
        Changed?.Invoke(user);
    }

    public IReadOnlyList<HistoryEntry> Get(string user)
    {
        if (!_history.TryGetValue(user, out var list)) return [];
        lock (list) return list.ToList();
    }

    public void Clear(string user)
    {
        if (_history.TryGetValue(user, out var list))
            lock (list) list.Clear();
        Changed?.Invoke(user);
    }
}
