using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Tests;

internal sealed class StaticMonitor(ExplorerOptions value) : IOptionsMonitor<ExplorerOptions>
{
    public ExplorerOptions CurrentValue => value;
    public ExplorerOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<ExplorerOptions, string?> listener) => null;
}

internal static class TempDir
{
    public static string Create() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "px-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    public static void Delete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
