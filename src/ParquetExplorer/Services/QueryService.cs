using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Claims;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using ParquetExplorer.Options;

namespace ParquetExplorer.Services;

public sealed class QueryService(DuckDbEngine engine, QueryHistoryService history, UserStore users, PartitionPruner pruner,
    IOptions<ExplorerOptions> options, ILogger<QueryService> logger)
{
    private readonly QueryOptions _opts = options.Value.Query;

    public int DefaultRowLimit => _opts.DefaultRowLimit;
    public int MaxRowLimit => _opts.MaxRowLimit;

    /// <summary>Checked against the user store, so role changes and disabled accounts apply immediately.</summary>
    public bool IsAdmin(ClaimsPrincipal user) => users.IsAdmin(user.Identity?.Name);

    /// <summary>Runs a script. Each statement producing rows yields a result set (capped at <paramref name="rowLimit"/> rows).</summary>
    public Task<QueryExecution> ExecuteAsync(string sql, int rowLimit, ClaimsPrincipal user,
        Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        rowLimit = Math.Clamp(rowLimit, 1, _opts.MaxRowLimit);
        return Task.Run(() => Execute(sql, rowLimit, user, onProgress, cancellationToken), CancellationToken.None);
    }

    private QueryExecution Execute(string sql, int rowLimit, ClaimsPrincipal user, Action<double>? onProgress, CancellationToken ct)
    {
        var exec = new QueryExecution();
        var sw = Stopwatch.StartNew();
        var userName = user.Identity?.Name ?? "?";

        try
        {
            if (!users.IsActive(userName))
                throw new UnauthorizedAccessException("Your account is disabled or no longer exists. Sign in again.");
            if (!IsAdmin(user) && SqlGuard.CheckReadOnly(sql) is { } reason)
                throw new UnauthorizedAccessException(reason);

            var statements = SqlText.SplitStatements(sql);
            if (statements.Count == 0) throw new InvalidOperationException("Nothing to execute.");

            using var timeoutCts = _opts.TimeoutSeconds > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(_opts.TimeoutSeconds)) : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var conn = engine.OpenConnection();

            // Progress polling runs on a timer while the native query blocks this thread.
            using var progressTimer = onProgress is null ? null : new Timer(_ =>
            {
                try
                {
                    var p = conn.GetQueryProgress().Percentage;
                    if (p >= 0) onProgress(p);
                }
                catch { /* connection may be closing */ }
            }, null, 300, 300);

            foreach (var statement in statements)
            {
                linked.Token.ThrowIfCancellationRequested();
                var rewritten = RewriteSafely(statement, conn, rowLimit);
                exec.Notes.AddRange(rewritten.Notes);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = rewritten.Sql;
                cmd.UseStreamingMode = true;
                using var reg = linked.Token.Register(() => { try { cmd.Cancel(); } catch { } });

                try
                {
                    using var reader = cmd.ExecuteReader();
                    do
                    {
                        exec.ResultSets.Add(ReadResult(reader, statement, rowLimit, linked.Token));
                    } while (reader.NextResult());
                }
                catch (Exception) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Query exceeded the timeout of {_opts.TimeoutSeconds} s.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            exec.Cancelled = true;
            exec.Error = "Query cancelled.";
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            exec.Cancelled = true;
            exec.Error = "Query cancelled.";
        }
        catch (Exception ex)
        {
            exec.Error = ex.Message;
        }

        exec.Elapsed = sw.Elapsed;
        history.Add(userName, new HistoryEntry(sql, exec.StartedAt, exec.Elapsed, exec.TotalRows, exec.Error));
        logger.LogInformation("Query by {User} in {Elapsed} ms, {Rows} rows{Error}", userName,
            (long)exec.Elapsed.TotalMilliseconds, exec.TotalRows, exec.Error is null ? "" : " - error: " + exec.Error);
        return exec;
    }

    /// <summary>Reads only the partitions a statement needs; on any problem the statement runs unchanged.</summary>
    public RewriteResult RewriteSafely(string statement, DuckDBConnection conn, int? rowLimit)
    {
        try
        {
            return pruner.Rewrite(statement, conn, rowLimit);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Partition pruning skipped: {Error}", ex.Message);
            return new RewriteResult(statement, []);
        }
    }

    private static ResultSet ReadResult(DuckDBDataReader reader, string statement, int rowLimit, CancellationToken ct)
    {
        var fieldCount = reader.FieldCount;
        if (fieldCount == 0)
        {
            return new ResultSet { Columns = [], Rows = [], RecordsAffected = reader.RecordsAffected, Statement = statement };
        }

        var cols = new ResultColumn[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var type = reader.GetDataTypeName(i);
            cols[i] = new ResultColumn(reader.GetName(i), type, IsNumericType(reader.GetFieldType(i)));
        }

        var rows = new List<string?[]>(Math.Min(rowLimit, 4096));
        var truncated = false;
        while (ReadNext(reader, rows.Count == 0, ct))
        {
            if (rows.Count >= rowLimit)
            {
                truncated = true;
                break;
            }
            if ((rows.Count & 1023) == 0) ct.ThrowIfCancellationRequested();

            var row = new string?[fieldCount];
            for (var i = 0; i < fieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : Format(reader.GetValue(i));
            rows.Add(row);
        }

        return new ResultSet { Columns = cols, Rows = rows, Truncated = truncated, Statement = statement };
    }

    /// <summary>
    /// DuckDB.NET 1.5 in streaming mode throws on the first Read() of an empty result
    /// ("unsuccessful or closed pending query result") although the query succeeded: read it as empty.
    /// </summary>
    private static bool ReadNext(DuckDBDataReader reader, bool first, CancellationToken ct)
    {
        try
        {
            return reader.Read();
        }
        catch (DuckDBException ex) when (first && !ct.IsCancellationRequested && ex.Message.Contains("unsuccessful or closed pending query result", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static bool IsNumericType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double) || t == typeof(decimal)
            || t == typeof(BigInteger) || t == typeof(Int128) || t == typeof(UInt128) || t == typeof(Half);
    }

    public static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] bytes => "\\x" + Convert.ToHexString(bytes.Length > 512 ? bytes[..512] : bytes) + (bytes.Length > 512 ? "…" : ""),
        Stream s => $"<blob {s.Length} bytes>",
        IDictionary dict => FormatDictionary(dict),
        IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(FormatNested)) + "]",
        _ => value.ToString() ?? "",
    };

    private static string FormatNested(object? v) => v is string s ? "'" + s + "'" : Format(v);

    private static string FormatDictionary(IDictionary dict)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (DictionaryEntry e in dict)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(FormatNested(e.Key)).Append(": ").Append(FormatNested(e.Value));
        }
        return sb.Append('}').ToString();
    }
}
