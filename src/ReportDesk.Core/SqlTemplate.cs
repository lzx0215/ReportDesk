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
    // HIS detail parameters can use column names such as 处方号/唯一号.
    // Input names remain metadata only: Oracle receives generated ASCII :pN binds.
    private static readonly Regex Placeholder = new(@"&([\p{L}_][\p{L}\p{Nd}_]*(?:\.(?:Value|Text|Values|Texts)\b)?)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    { "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE",
      "COMMIT", "ROLLBACK", "BEGIN", "DECLARE", "EXECUTE", "CALL", "INTO", "FUNCTION", "PROCEDURE",
      "NEXTVAL", "DBMS_SQL", "UTL_HTTP", "UTL_FILE", "DBMS_SCHEDULER", "DBMS_LOCK" };

    public static BoundQuery Compile(string input, IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyDictionary<string, string[]>? multiple = null)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("查询 SQL 为空。");
        var result = new BoundQuery();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder();
        var words = new List<string>();
        var parentheses = new Stack<bool>();
        string Bind(string name, bool listPosition = false)
        {
            if (multiple != null && multiple.TryGetValue(name, out var selections))
            {
                if (!listPosition) throw new InvalidOperationException("多选参数 " + name + " 必须是 IN/NOT IN 括号中的完整参数，不能作为普通文本或 SQL 片段。");
                if (!names.TryGetValue(name, out var prefix))
                {
                    prefix = "p" + names.Count.ToString(CultureInfo.InvariantCulture); names.Add(name,prefix); result.RequiredNames.Add(name);
                    if (values != null) for (var n=0;n<selections.Length;n++)
                    {
                        if(selections[n].Length>4000) throw new InvalidOperationException("参数过长：" + name);
                        result.Values.Add(prefix+"_"+n,selections[n]);
                    }
                }
                if (selections.Length == 0) return "NULL";
                var binds=Enumerable.Range(0,selections.Length).Select(n=>":"+prefix+"_"+n).ToArray();
                // Oracle's 1000-expression IN limit must not truncate the selection.
                return selections.Length <= 1000 ? string.Join(",",binds) : string.Join(" UNION ALL ",binds.Select(b=>"SELECT "+b+" FROM DUAL"));
            }
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
        bool ListPosition(int after) => Regex.IsMatch(output.ToString(), @"\bIN\s*\(\s*$", RegexOptions.IgnoreCase) && Regex.IsMatch(sql.Substring(after), @"^\s*\)");
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
                    if (match.Index + match.Length < content.Length && (content[match.Index + match.Length] == '.' || content[match.Index + match.Length] == '['))
                        throw new InvalidOperationException("数据源/控件属性映射尚未适配。");
                    if (match.Index > offset) parts.Add(Literal(content.Substring(offset, match.Index - offset)));
                    parts.Add(Bind(match.Groups[1].Value, matches.Count == 1 && match.Length == content.Length && ListPosition(i))); offset = match.Index + match.Length;
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
                if (i + match.Length < sql.Length && (sql[i + match.Length] == '.' || sql[i + match.Length] == '['))
                    throw new InvalidOperationException("数据源/控件属性映射尚未适配。");
                if (words.Count > 0 && new[] { "FROM", "JOIN", "BY", "SELECT", "ASC", "DESC" }.Contains(words.Last(), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("疑似动态 SQL 片段，需要人工适配。");
                output.Append(Bind(match.Groups[1].Value, ListPosition(i+match.Length))); i += match.Length; continue;
            }
            if (c == ';' || c == ':' || c == '@' || c == '（' || c == '）')
            {
                var line = 1 + sql.Substring(0, i).Count(x => x == '\n');
                var column = i - sql.LastIndexOf('\n', i);
                var reason = c == ';' ? "发现语句中间的分号；不支持一次执行多条 SQL。" :
                    c == ':' ? "发现原生绑定变量（冒号）；需要适配此参数，不能直接拼接输入值。" :
                    c == '@' ? "发现数据库链接标记 @；需要核对跨库依赖。" :
                    "发现全角括号“" + c + "”；请将此处改为英文括号“" + (c == '（' ? "(" : ")") + "”。";
                throw new InvalidOperationException("SQL 第 " + line + " 行，第 " + column + " 列：" + reason);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '#')) i++;
                var word = sql.Substring(start, i - start);
                if (i < sql.Length && sql[i] == '\'' && (word.Equals("q", StringComparison.OrdinalIgnoreCase) || word.Equals("n", StringComparison.OrdinalIgnoreCase) || word.Equals("nq", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("此版暂不转换 Q/N 引用字符串。");
                var listaggTruncate = word.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
                    parentheses.Count > 0 && parentheses.Peek() &&
                    Regex.IsMatch(output.ToString(), @"\bON\s+OVERFLOW\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (Forbidden.Contains(word) && !listaggTruncate) throw new InvalidOperationException("只支持查询；发现需拒绝或适配的关键字：" + word);
                words.Add(word); output.Append(word); continue;
            }
            if (c == '(') parentheses.Push(Regex.IsMatch(output.ToString(), @"\bLISTAGG\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            else if (c == ')' && parentheses.Count > 0) parentheses.Pop();
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
