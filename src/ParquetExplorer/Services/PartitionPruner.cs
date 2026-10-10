using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;

namespace ParquetExplorer.Services;

public sealed record RewriteResult(string Sql, IReadOnlyList<string> Notes);

/// <summary>
/// Rewrites a statement so that each partitioned table reads only the files it needs.
/// </summary>
/// <remarks>
/// <para>
/// DuckDB lists every file of a table before planning a query, even when the WHERE clause keeps a
/// single partition. On a network share that is one round trip per folder, one at a time: minutes
/// for a table with tens of thousands of partition folders. Using DuckDB's own parser
/// (<c>json_serialize_sql</c>), this class finds the references to catalog tables and the
/// conditions on their partition columns, picks the matching files from the catalog scan and
/// replaces the table with <c>read_parquet([those files])</c>.
/// </para>
/// <para>
/// Pruning is conservative: only top-level AND conditions of the form
/// <c>column op constant</c> (=, &lt;&gt;, &lt;, &lt;=, &gt;, &gt;=, IN, BETWEEN) on a partition
/// column are used, and a file is dropped only when its folder value makes the condition false.
/// These comparisons reject NULL, so dropping rows they would filter out never changes the
/// result, also with outer joins. Anything not understood keeps all files.
/// </para>
/// </remarks>
public sealed partial class PartitionPruner(CatalogService catalog, ILogger<PartitionPruner> logger)
{
    private const int MaxFastPathRows = 1_000_000;

    /// <param name="rowLimit">
    /// Rows the caller will read (result grid). A plain <c>SELECT … FROM t [WHERE partitions]</c> then
    /// reads only enough files to fill it. Null for exports, which need the whole result.
    /// </param>
    public RewriteResult Rewrite(string statement, DuckDBConnection conn, int? rowLimit)
    {
        var candidates = catalog.AllTables
            .Where(t => t.Error is null && t.Files.Count > 0 && (t.HivePartitioned || !t.HasView || t.FileCount > 1))
            .Where(t => statement.Contains(t.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return new RewriteResult(statement, []);

        // EXPLAIN [ANALYZE] is not serializable: rewrite the statement it wraps.
        var explain = ExplainPrefix().Match(statement);
        var prefix = explain.Success ? explain.Value : "";
        var body = statement[prefix.Length..];

        JsonDocument doc;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT json_serialize_sql({SqlText.Literal(body)})::VARCHAR";
            doc = JsonDocument.Parse(cmd.ExecuteScalar()?.ToString() ?? "{}");
        }
        catch (Exception ex)
        {
            logger.LogDebug("Cannot parse statement for pruning: {Error}", ex.Message);
            return new RewriteResult(statement, []);
        }

        using (doc)
        {
            var rootEl = doc.RootElement;
            if (!rootEl.TryGetProperty("error", out var err) || err.GetBoolean()
                || !rootEl.TryGetProperty("statements", out var stmts) || stmts.GetArrayLength() != 1)
                return new RewriteResult(statement, []);

            var context = new RewriteContext(body, candidates, conn, rowLimit);
            var top = stmts[0].GetProperty("node");
            Visit(top, context, topLevel: true, describeOnly: false, inShowRef: false);

            if (context.Replacements.Count == 0) return new RewriteResult(statement, context.Notes);

            var sb = new StringBuilder(body);
            foreach (var r in context.Replacements.OrderByDescending(r => r.Start))
                sb.Remove(r.Start, r.Length).Insert(r.Start, r.Text);
            return new RewriteResult(prefix + sb, context.Notes);
        }
    }

    [GeneratedRegex(@"^\s*EXPLAIN(\s+ANALYZE)?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ExplainPrefix();

    private sealed class RewriteContext(string sql, List<TableInfo> tables, DuckDBConnection conn, int? rowLimit)
    {
        public string Sql { get; } = sql;
        public byte[] Utf8 { get; } = Encoding.UTF8.GetBytes(sql);
        public List<TableInfo> Tables { get; } = tables;
        public DuckDBConnection Connection { get; } = conn;
        public int? RowLimit { get; } = rowLimit;
        public List<(int Start, int Length, string Text)> Replacements { get; } = [];
        public List<string> Notes { get; } = [];
    }

    // ---------------- AST walk ----------------

    /// <summary>Visits every SELECT node (CTEs, subqueries, set operations included).</summary>
    private void Visit(JsonElement el, RewriteContext ctx, bool topLevel, bool describeOnly, bool inShowRef)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var type = Str(el, "type");
                if (type == "SHOW_REF")
                {
                    // DESCRIBE / SUMMARIZE / SHOW: "DESCRIBE (subquery)" takes no alias.
                    inShowRef = true;
                    if (Str(el, "show_type") == "DESCRIBE") describeOnly = true;
                }
                if (type == "SELECT_NODE") ProcessSelect(el, ctx, topLevel, describeOnly, inShowRef);
                // A nested SELECT is a subquery again: its table references take aliases.
                var nestedShowRef = inShowRef && type != "SELECT_NODE";
                foreach (var p in el.EnumerateObject()) Visit(p.Value, ctx, topLevel: false, describeOnly, nestedShowRef);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Visit(item, ctx, topLevel: false, describeOnly, inShowRef);
                break;
        }
    }

    private sealed record TableRef(JsonElement Node, string Schema, string Name, string Alias, int Location);

    private void ProcessSelect(JsonElement node, RewriteContext ctx, bool topLevel, bool describeOnly, bool inShowRef)
    {
        var refs = new List<TableRef>();
        var opaque = 0;
        if (node.TryGetProperty("from_table", out var from)) CollectFrom(from, refs, ref opaque);
        var single = refs.Count == 1 && opaque == 0;
        var conjuncts = node.TryGetProperty("where_clause", out var where) ? Conjuncts(where).ToList() : [];

        foreach (var r in refs)
        {
            var table = ctx.Tables.FirstOrDefault(t => t.Schema.Equals(r.Schema, StringComparison.OrdinalIgnoreCase)
                                                    && t.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
            if (table is null || r.Location < 0) continue;
            var binding = r.Alias.Length > 0 ? r.Alias : r.Name;

            // Files kept by the partition conditions.
            IEnumerable<TableFile> kept = table.Files;
            var allConditionsUsed = true;
            foreach (var c in conjuncts)
            {
                var test = PartitionTest(c, table, binding, r, single);
                if (test is null) { allConditionsUsed = false; continue; }
                kept = kept.Where(test);
            }
            var files = kept.ToList();
            var pruned = files.Count < table.Files.Count;

            string? replacement = null;
            if (describeOnly)
            {
                replacement = Read(table, [table.Files[0]]);
            }
            else if (files.Count == 0)
            {
                // No partition matches: an empty relation with the table's columns.
                replacement = $"(SELECT * FROM {CatalogService.ReadParquetExpr(SourceOf(table), table, [table.Files[0].Uri])} LIMIT 0)";
                ctx.Notes.Add($"{table.DisplayQualifiedName}: no partition matches the filter, 0 of {table.Files.Count:N0} files read.");
            }
            else if (topLevel && single && allConditionsUsed && FastPathTarget(node, ctx.RowLimit) is { } target
                     && FirstFilesWithRows(files, target, ctx.Connection) is { } firstFiles)
            {
                replacement = Read(table, firstFiles);
                ctx.Notes.Add($"{table.DisplayQualifiedName}: {firstFiles.Count:N0} of {table.Files.Count:N0} files read (enough for {target:N0} rows, no ORDER BY).");
            }
            else if (pruned)
            {
                replacement = Read(table, files);
                ctx.Notes.Add($"{table.DisplayQualifiedName}: partition filter, {files.Count:N0} of {table.Files.Count:N0} files read.");
            }
            else if (!table.HasView)
            {
                // Network table without a view: read it through its globs.
                replacement = $"(SELECT * FROM {CatalogService.ReadParquetExpr(SourceOf(table), table)})";
                if (table.Files.Count > 1000)
                    ctx.Notes.Add($"{table.DisplayQualifiedName}: no filter on the partition columns ({string.Join(", ", table.PartitionKeys)}), all {table.Files.Count:N0} files are listed and read. Filter on them to make the query faster.");
            }

            if (replacement is null) continue;
            if (Span(ctx, r) is not { } span) continue;
            // "DESCRIBE t" / "SUMMARIZE t" become "DESCRIBE (subquery)", which takes no alias.
            var bareShow = inShowRef && PrecedingWord(ctx.Sql, span.Start) is "DESCRIBE" or "DESC" or "SUMMARIZE" or "SHOW";
            var aliasText = r.Alias.Length > 0 || bareShow ? "" : " AS " + SqlText.Ident(r.Name);
            ctx.Replacements.Add((span.Start, span.Length, replacement + aliasText));
        }
    }

    private string Read(TableInfo table, IReadOnlyList<TableFile> files) =>
        $"(SELECT * FROM {CatalogService.ReadParquetExpr(SourceOf(table), table, files.Select(f => f.Uri).ToList())})";

    private Options.DataSourceOptions SourceOf(TableInfo t) =>
        catalog.Sources.First(s => s.Name == t.Source).Options;

    private static void CollectFrom(JsonElement from, List<TableRef> refs, ref int opaque)
    {
        switch (Str(from, "type"))
        {
            case "BASE_TABLE":
                var catalogName = Str(from, "catalog_name");
                if (catalogName.Length > 0 && !catalogName.Equals("memory", StringComparison.OrdinalIgnoreCase))
                {
                    opaque++;
                    break;
                }
                var location = from.TryGetProperty("query_location", out var loc) && loc.TryGetUInt64(out var l) && l < int.MaxValue ? (int)l : -1;
                refs.Add(new TableRef(from, Str(from, "schema_name"), Str(from, "table_name"), Str(from, "alias"), location));
                break;
            case "JOIN":
                if (from.TryGetProperty("left", out var left)) CollectFrom(left, refs, ref opaque);
                if (from.TryGetProperty("right", out var right)) CollectFrom(right, refs, ref opaque);
                break;
            case "EMPTY_FROM":
            case "":
                break;
            default:
                opaque++;
                break;
        }
    }

    private static IEnumerable<JsonElement> Conjuncts(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) yield break;
        if (Str(e, "class") == "CONJUNCTION" && Str(e, "type") == "CONJUNCTION_AND" && e.TryGetProperty("children", out var children))
        {
            foreach (var c in children.EnumerateArray())
                foreach (var x in Conjuncts(c)) yield return x;
        }
        else
        {
            yield return e;
        }
    }

    // ---------------- Partition conditions ----------------

    /// <summary>Returns a test keeping the files whose partition value may satisfy the condition, or null.</summary>
    private static Func<TableFile, bool>? PartitionTest(JsonElement c, TableInfo table, string binding, TableRef r, bool single)
    {
        var cls = Str(c, "class");
        var type = Str(c, "type");
        int? Key(JsonElement e) => PartitionColumn(e, table, binding, r, single);

        if (cls == "COMPARISON")
        {
            var left = c.GetProperty("left");
            var right = c.GetProperty("right");
            var op = type;
            var key = Key(left);
            var constant = right;
            if (key is null)
            {
                key = Key(right);
                constant = left;
                op = Flip(op);
            }
            if (key is not { } k || op is null) return null;
            var kind = table.PartitionTypes[table.PartitionKeys[k]];
            if (Constant(constant, kind) is not { } value) return null;
            if (kind == "VARCHAR" && op is not ("COMPARE_EQUAL" or "COMPARE_NOTEQUAL") && !IsAscii((string)value)) return null;
            return f => Matches(f, k, kind, cmp => op switch
            {
                "COMPARE_EQUAL" => cmp == 0,
                "COMPARE_NOTEQUAL" => cmp != 0,
                "COMPARE_LESSTHAN" => cmp < 0,
                "COMPARE_LESSTHANOREQUALTO" => cmp <= 0,
                "COMPARE_GREATERTHAN" => cmp > 0,
                "COMPARE_GREATERTHANOREQUALTO" => cmp >= 0,
                _ => true,
            }, value);
        }

        if (cls == "OPERATOR" && type == "COMPARE_IN" && c.TryGetProperty("children", out var children))
        {
            var items = children.EnumerateArray().ToList();
            if (items.Count < 2 || Key(items[0]) is not { } k) return null;
            var kind = table.PartitionTypes[table.PartitionKeys[k]];
            var values = new List<object>();
            foreach (var item in items.Skip(1))
            {
                if (Constant(item, kind) is not { } v) return null;
                values.Add(v);
            }
            return f => values.Any(v => Matches(f, k, kind, cmp => cmp == 0, v));
        }

        if (cls == "BETWEEN" && c.TryGetProperty("input", out var input) && Key(input) is { } bk)
        {
            var kind = table.PartitionTypes[table.PartitionKeys[bk]];
            if (Constant(c.GetProperty("lower"), kind) is not { } lo || Constant(c.GetProperty("upper"), kind) is not { } hi) return null;
            if (kind == "VARCHAR" && (!IsAscii((string)lo) || !IsAscii((string)hi))) return null;
            return f => Matches(f, bk, kind, cmp => cmp >= 0, lo) && Matches(f, bk, kind, cmp => cmp <= 0, hi);
        }

        return null;
    }

    private static string? Flip(string op) => op switch
    {
        "COMPARE_EQUAL" or "COMPARE_NOTEQUAL" => op,
        "COMPARE_LESSTHAN" => "COMPARE_GREATERTHAN",
        "COMPARE_GREATERTHAN" => "COMPARE_LESSTHAN",
        "COMPARE_LESSTHANOREQUALTO" => "COMPARE_GREATERTHANOREQUALTO",
        "COMPARE_GREATERTHANOREQUALTO" => "COMPARE_LESSTHANOREQUALTO",
        _ => null,
    };

    /// <summary>Index of the partition key referenced by a column expression, if it belongs to this table.</summary>
    private static int? PartitionColumn(JsonElement e, TableInfo table, string binding, TableRef r, bool single)
    {
        if (Str(e, "class") != "COLUMN_REF" || !e.TryGetProperty("column_names", out var namesEl)) return null;
        var names = namesEl.EnumerateArray().Select(n => n.GetString() ?? "").ToArray();
        var column = names[^1];
        var ok = names.Length switch
        {
            1 => single,
            2 => names[0].Equals(binding, StringComparison.OrdinalIgnoreCase),
            3 => r.Alias.Length == 0 && names[0].Equals(r.Schema, StringComparison.OrdinalIgnoreCase)
                                      && names[1].Equals(r.Name, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!ok) return null;
        for (var i = 0; i < table.PartitionKeys.Count; i++)
            if (table.PartitionKeys[i].Equals(column, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>A constant converted to the partition column type (decimal, DateOnly or string), or null.</summary>
    private static object? Constant(JsonElement e, string kind)
    {
        string? castTo = null;
        if (Str(e, "class") == "CAST")
        {
            if (e.TryGetProperty("try_cast", out var tc) && tc.ValueKind == JsonValueKind.True) return null;
            castTo = e.GetProperty("cast_type").TryGetProperty("id", out var id) ? id.GetString() : null;
            e = e.GetProperty("child");
        }
        if (Str(e, "class") != "CONSTANT" || !e.TryGetProperty("value", out var v)
            || (v.TryGetProperty("is_null", out var isNull) && isNull.ValueKind == JsonValueKind.True)
            || !v.TryGetProperty("value", out var raw)) return null;
        var constType = v.GetProperty("type").TryGetProperty("id", out var ct) ? ct.GetString() ?? "" : "";

        switch (kind)
        {
            case "BIGINT":
                if (castTo is not (null or "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "HUGEINT" or "DECIMAL" or "DOUBLE" or "FLOAT")) return null;
                if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out var d)) return d;
                if (raw.ValueKind == JsonValueKind.String && decimal.TryParse(raw.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) return ds;
                return null;
            case "DATE":
                // DATE '2026-05-01' or '2026-05-01' compared to a DATE column. Timestamps compare differently: skip.
                if (castTo is not (null or "DATE") || constType != "VARCHAR" || raw.ValueKind != JsonValueKind.String) return null;
                return DateOnly.TryParseExact(raw.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
            case "VARCHAR":
                if (castTo is not (null or "VARCHAR") || constType != "VARCHAR" || raw.ValueKind != JsonValueKind.String) return null;
                return raw.GetString();
            default:
                return null;
        }
    }

    /// <summary>
    /// True when the file may hold rows satisfying <paramref name="test"/>(compare(fileValue, constant)).
    /// NULL partitions never satisfy a comparison; unparsable values are kept.
    /// </summary>
    private static bool Matches(TableFile f, int key, string kind, Func<int, bool> test, object constant)
    {
        var raw = key < f.PartitionValues.Length ? f.PartitionValues[key] : null;
        if (raw is null || raw.Contains('%')) return true;
        if (raw is "__HIVE_DEFAULT_PARTITION__" or "NULL") return false;
        switch (kind)
        {
            case "BIGINT":
                return !decimal.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || test(n.CompareTo((decimal)constant));
            case "DATE":
                return !DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || test(d.CompareTo((DateOnly)constant));
            case "VARCHAR":
                return test(Math.Sign(string.CompareOrdinal(raw, (string)constant)));
            default:
                return true;
        }
    }

    private static bool IsAscii(string s) => s.All(ch => ch < 128);

    // ---------------- "Top N rows" fast path ----------------

    /// <summary>
    /// Rows needed when the SELECT is a plain projection with no ORDER BY, aggregate or extra filter:
    /// any rows will do, so reading the first files is enough.
    /// </summary>
    private static long? FastPathTarget(JsonElement node, int? rowLimit)
    {
        if (node.TryGetProperty("group_expressions", out var g) && g.GetArrayLength() > 0) return null;
        if (node.TryGetProperty("group_sets", out var gs) && gs.GetArrayLength() > 0) return null;
        foreach (var p in new[] { "having", "qualify", "sample" })
            if (node.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null) return null;
        if (Str(node, "aggregate_handling") is { Length: > 0 } ah && ah != "STANDARD_HANDLING") return null;
        if (!node.TryGetProperty("select_list", out var list)
            || list.EnumerateArray().Any(e => Str(e, "class") is not ("STAR" or "COLUMN_REF"))) return null;

        var modifiers = node.TryGetProperty("modifiers", out var m) ? m.EnumerateArray().ToList() : [];
        if (modifiers.Count == 0) return rowLimit is { } rl ? rl + 1L : null;
        if (modifiers.Count != 1 || Str(modifiers[0], "type") != "LIMIT_MODIFIER") return null;
        if (IntConstant(modifiers[0], "limit") is not { } limit) return null;
        var offset = 0L;
        if (modifiers[0].TryGetProperty("offset", out var off) && off.ValueKind != JsonValueKind.Null)
        {
            if (IntConstant(modifiers[0], "offset") is not { } o) return null;
            offset = o;
        }
        var target = limit + offset;
        return target is > 0 and <= MaxFastPathRows ? target : null;
    }

    private static long? IntConstant(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var e) && Str(e, "class") == "CONSTANT"
        && e.GetProperty("value").TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : null;

    /// <summary>First files (in path order) whose footers add up to <paramref name="target"/> rows, or null.</summary>
    private static List<TableFile>? FirstFilesWithRows(List<TableFile> files, long target, DuckDBConnection conn)
    {
        long rows = 0;
        var taken = 0;
        var batch = 4;
        while (taken < files.Count && taken < 4096)
        {
            var next = files.Skip(taken).Take(batch).ToList();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT file_name, num_rows FROM parquet_file_metadata([{string.Join(", ", next.Select(f => SqlText.Literal(f.Uri)))}])";
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) counts[NormalizePath(r.GetString(0))] = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
            foreach (var f in next)
            {
                taken++;
                rows += counts.GetValueOrDefault(NormalizePath(f.Uri));
                if (rows >= target) return files.Take(taken).ToList();
            }
            batch = Math.Min(batch * 2, 64);
        }
        // Not enough rows in the first files (or too many tiny files): read them all.
        return null;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    // ---------------- Text splicing ----------------

    /// <summary>Character span of the table name (schema.table, quoted or not) starting at the node's location.</summary>
    private static (int Start, int Length)? Span(RewriteContext ctx, TableRef r)
    {
        if (r.Location > ctx.Utf8.Length) return null;
        var start = Encoding.UTF8.GetCharCount(ctx.Utf8, 0, r.Location);
        var s = ctx.Sql;
        var i = start;
        var parts = new List<string>();
        while (true)
        {
            if (i >= s.Length) return null;
            string part;
            if (s[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        break;
                    }
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) return null;
                i++;
                part = sb.ToString();
            }
            else
            {
                var b = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                if (i == b) return null;
                part = s[b..i];
            }
            parts.Add(part);
            var j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && s[j] == '.' && parts.Count < 3)
            {
                j++;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                i = j;
                continue;
            }
            break;
        }
        // Sanity check: the text must be the reference DuckDB reported.
        if (!parts[^1].Equals(r.Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (parts.Count >= 2 && !parts[^2].Equals(r.Schema, StringComparison.OrdinalIgnoreCase)) return null;
        return (start, i - start);
    }

    private static string PrecedingWord(string sql, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(sql[i])) i--;
        var end = i + 1;
        while (i >= 0 && char.IsLetter(sql[i])) i--;
        return sql[(i + 1)..end].ToUpperInvariant();
    }

    private static string Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
