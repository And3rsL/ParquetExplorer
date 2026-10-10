using System.Text;

namespace ParquetExplorer.Services;

/// <summary>Helpers to safely build DuckDB SQL text.</summary>
public static class SqlText
{
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static string Ident(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    public static string Qualified(string schema, string name) => Ident(schema) + "." + Ident(name);

    /// <summary>Turns an arbitrary name (folder path, file name...) into a friendly SQL identifier.</summary>
    public static string ToIdentifier(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var lastUnderscore = false;
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }

        var id = sb.ToString().TrimEnd('_');
        if (id.Length == 0) id = "t";
        if (char.IsDigit(id[0])) id = "t_" + id;
        return id;
    }

    /// <summary>
    /// Splits a script into statements, honouring quotes, quoted identifiers, dollar quotes and comments.
    /// Returns trimmed, non-empty statements without the trailing semicolon.
    /// </summary>
    public static List<string> SplitStatements(string sql)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                current.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                current.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c is '\'' or '"')
            {
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == c) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                j = Math.Min(j + 1, sql.Length);
                current.Append(sql, i, j - i);
                i = j;
                continue;
            }
            if (c == ';')
            {
                Flush();
                i++;
                continue;
            }
            current.Append(c);
            i++;
        }
        Flush();
        return result;

        void Flush()
        {
            var s = current.ToString().Trim();
            if (StripComments(s).Trim().Length > 0) result.Add(s);
            current.Clear();
        }
    }

    /// <summary>Removes -- and /* */ comments (outside of string literals).</summary>
    public static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                sb.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                sb.Append(' ');
                continue;
            }
            if (c is '\'' or '"')
            {
                var j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == c) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                j = Math.Min(j + 1, sql.Length);
                sb.Append(sql, i, j - i);
                i = j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>First keyword of a statement (upper case), ignoring comments and parentheses.</summary>
    public static string FirstKeyword(string statement)
    {
        var s = StripComments(statement).TrimStart(' ', '\t', '\r', '\n', '(');
        var end = 0;
        while (end < s.Length && (char.IsLetter(s[end]) || s[end] == '_')) end++;
        return s[..end].ToUpperInvariant();
    }
}
