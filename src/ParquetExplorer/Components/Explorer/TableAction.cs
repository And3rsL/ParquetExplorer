using ParquetExplorer.Services;

namespace ParquetExplorer.Components.Explorer;

public enum TableActionKind
{
    SelectTop,
    Count,
    Describe,
    Summarize,
    Files,
    ScriptSelect,
    InsertName,
    CopyName,
}

public sealed record TableAction(TableInfo Table, TableActionKind Kind);

public static class Format
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes.Value;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMilliseconds < 1000 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.00} s"
        : $"{(int)t.TotalMinutes}m {t.Seconds:00}s";

    public static string TypeIcon(string type)
    {
        var t = type.ToUpperInvariant();
        if (t.StartsWith("STRUCT") || t.StartsWith("MAP") || t.EndsWith("[]") || t.StartsWith("LIST") || t.StartsWith("UNION") || t == "JSON") return "braces";
        if (t.Contains("INT") || t.StartsWith("DECIMAL") || t is "DOUBLE" or "FLOAT" or "REAL" or "HUGEINT" or "UHUGEINT") return "hash";
        if (t.StartsWith("DATE") || t.StartsWith("TIME") || t.StartsWith("INTERVAL")) return "calendar";
        if (t == "BOOLEAN") return "toggle";
        if (t is "BLOB" or "BIT") return "code";
        return "type";
    }
}
