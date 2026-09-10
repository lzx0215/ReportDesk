using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportDesk.Core;

public sealed class BoundQuery
{
    public string Sql { get; set; } = "";
    public Dictionary<string, string> Values { get; } = new();
    public List<string> RequiredNames { get; } = new();
}

// Deliberately conservative lexer for legacy &value templates, not an Oracle SQL parser.
// User values are NEVER copied into command text. Unsupported syntax fails closed.
public static class SqlTemplate
{
    private static readonly Regex Placeholder = new(@"&([A-Za-z_][A-Za-z_0-9]*)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    { "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE",
      "COMMIT", "ROLLBACK", "BEGIN", "DECLARE", "EXECUTE", "CALL", "INTO", "FUNCTION", "PROCEDURE",
      "NEXTVAL", "DBMS_SQL", "UTL_HTTP", "UTL_FILE", "DBMS_SCHEDULER", "DBMS_LOCK" };

    public static BoundQuery Compile(string input, IReadOnlyDictionary<string, string>? values = null)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("查询 SQL 为空。");
        var result = new BoundQuery();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var words = new List<string>();
        string Bind(string name)
        {
            if (!names.TryGetValue(name, out var bind))
            {
                bind = "p" + names.Count.ToString(CultureInfo.InvariantCulture);
                names.Add(name, bind);
                result.RequiredNames.Add(name);
                if (values != null)
                {
                    if (!values.TryGetValue(name, out var value)) throw new InvalidOperationException("缺少参数：" + name);
                    if (value.Length > 4000) throw new InvalidOperationException("参数过长：" + name);
                    result.Values.Add(bind, value);
                }
            }
            return ":" + bind;
        }
        string Literal(string text) => "'" + text.Replace("'", "''") + "'";
        var sql = input.Trim();
        if (sql.EndsWith(";", StringComparison.Ordinal)) sql = sql.Substring(0, sql.Length - 1);
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                output.Append('\n'); continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidOperationException("SQL 注释未结束。");
                // Preserve hints; never substitute placeholders inside comments.
                output.Append(sql.Substring(i, end + 2 - i)); i = end + 2; continue;
            }
            if (c == '\'')
            {
                i++; var literal = new StringBuilder(); var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { literal.Append('\''); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    literal.Append(sql[i++]);
                }
                if (!closed) throw new InvalidOperationException("SQL 字符串未结束。");
                var content = literal.ToString(); var matches = Placeholder.Matches(content);
                if (matches.Count == 0) { output.Append(Literal(content)); continue; }
                var parts = new List<string>(); var offset = 0;
                foreach (Match match in matches)
                {
                    if (match.Index > offset) parts.Add(Literal(content.Substring(offset, match.Index - offset)));
                    parts.Add(Bind(match.Groups[1].Value)); offset = match.Index + match.Length;
                }
                if (offset < content.Length) parts.Add(Literal(content.Substring(offset)));
                output.Append(parts.Count == 1 ? parts[0] : "(" + string.Join(" || ", parts) + ")");
                continue;
            }
            if (c == '"')
            {
                var start = i++; var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != '"') continue;
                    if (i < sql.Length && sql[i] == '"') { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new InvalidOperationException("SQL 标识符引号未结束。");
                var identifier = sql.Substring(start, i - start);
                if (Placeholder.IsMatch(identifier)) throw new InvalidOperationException("不支持动态列名或标识符。");
                output.Append(identifier); continue;
            }
            if (c == '&')
            {
                var match = Placeholder.Match(sql, i);
                if (!match.Success || match.Index != i) throw new InvalidOperationException("不支持此 & 模板语法。");
                if (words.Count > 0 && new[] { "FROM", "JOIN", "BY", "SELECT", "ASC", "DESC" }.Contains(words.Last(), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("疑似动态 SQL 片段，需要人工适配。");
                output.Append(Bind(match.Groups[1].Value)); i += match.Length; continue;
            }
            if (c == ';' || c == ':' || c == '@' || c == '（' || c == '）')
                throw new InvalidOperationException("不支持多语句、原生绑定变量、数据库链接或全角括号，需要适配。");
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '#')) i++;
                var word = sql.Substring(start, i - start);
                if (i < sql.Length && sql[i] == '\'' && (word.Equals("q", StringComparison.OrdinalIgnoreCase) || word.Equals("n", StringComparison.OrdinalIgnoreCase) || word.Equals("nq", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("此版暂不转换 Q/N 引用字符串。");
                if (Forbidden.Contains(word)) throw new InvalidOperationException("只支持查询；发现需拒绝或适配的关键字：" + word);
                words.Add(word); output.Append(word); continue;
            }
            output.Append(c); i++;
        }
        if (words.Count == 0 || (!words[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !words[0].Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("只允许 SELECT 或 WITH 查询。");
        result.Sql = output.ToString();
        return result;
    }

    public static string Transform(ParameterDefinition parameter, string value)
    {
        if (parameter.PadLeft)
        {
            if (parameter.PadCharacter.Length != 1 || parameter.PadLength < 0 || parameter.PadLength > 1000)
                throw new InvalidOperationException("不支持的补齐设置：" + parameter.Name);
            value = value.PadLeft(parameter.PadLength, parameter.PadCharacter[0]);
        }
        if (parameter.IsLike) value = string.Format(CultureInfo.InvariantCulture, parameter.LikeFormat, value);
        return value;
    }
}
