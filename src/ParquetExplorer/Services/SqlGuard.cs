namespace ParquetExplorer.Services;

/// <summary>
/// Decides whether a script is read-only. Non-admin users may only run read-only statements.
/// The DuckDB sandbox (locked configuration + allowed directories) is the second line of defence.
/// </summary>
public static class SqlGuard
{
    private static readonly HashSet<string> ReadOnlyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "FROM", "VALUES", "TABLE", "DESCRIBE", "DESC", "SHOW", "SUMMARIZE",
        "PIVOT", "UNPIVOT", "PIVOT_WIDER", "PIVOT_LONGER",
    };

    /// <summary>Returns null when the script is read-only, otherwise the reason it is rejected.</summary>
    public static string? CheckReadOnly(string script)
    {
        foreach (var stmt in SqlText.SplitStatements(script))
        {
            var reason = CheckStatement(stmt);
            if (reason is not null) return reason;
        }
        return null;
    }

    private static string? CheckStatement(string stmt)
    {
        var clean = SqlText.StripComments(stmt).Trim();
        var kw = SqlText.FirstKeyword(clean);

        if (kw == "EXPLAIN")
        {
            // EXPLAIN [ANALYZE] <statement>: ANALYZE executes the statement, so check what follows.
            var rest = clean.TrimStart('(').TrimStart()[kw.Length..].TrimStart();
            if (SqlText.FirstKeyword(rest) == "ANALYZE") rest = rest[7..].TrimStart();
            return CheckStatement(rest);
        }

        if (!ReadOnlyKeywords.Contains(kw))
            return $"'{(kw.Length == 0 ? clean.Split(' ')[0] : kw)}' statements are not allowed: your account is read-only.";

        // Data modifying CTEs are not supported by DuckDB, but COPY/ATTACH cannot hide inside a SELECT either.
        return null;
    }
}
