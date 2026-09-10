using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReportDesk.Core;

// Reads an explicit local file only. Descriptors are passed intact to ODP.NET;
// no registry/environment changes, global alias cache, or implicit include lookup.
public static class TnsNames
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new InvalidOperationException("请选择 tnsnames.ora 文件的完整路径。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidOperationException("TNS 文件超过 1 MiB，请选用较小的配置文件。");
        // BOM detection supports UTF-8/UTF-16; legacy files use the Windows code page.
        using var reader = new StreamReader(stream, Encoding.Default, true);
        return Parse(reader.ReadToEnd());
    }

    public static string Resolve(string path, string alias)
    {
        var entries = Read(path);
        if (string.IsNullOrWhiteSpace(alias) || !entries.TryGetValue(alias, out var descriptor))
            throw new InvalidOperationException("所选 TNS 别名不在当前文件中，请重新读取并选择别名。");
        return descriptor;
    }

    public static IReadOnlyDictionary<string, string> Parse(string source)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder(); char quote = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (quote == '\0' && c == '#')
            { while (i < source.Length && source[i] != '\n') i++; text.Append('\n'); continue; }
            if (c == '"' || c == '\'')
            { if (quote == '\0') quote = c; else if (quote == c) quote = '\0'; }
            text.Append(c);
        }
        if (quote != '\0') throw new InvalidOperationException("TNS 文件存在未闭合的引号。");
        var input = text.ToString(); var pos = 0;
        void Skip() { while (pos < input.Length && char.IsWhiteSpace(input[pos])) pos++; }
        InvalidOperationException Error(string reason) => new InvalidOperationException("TNS 文件第 " + (1 + CountLines(input, pos)) + " 行：" + reason);
        while (true)
        {
            Skip(); if (pos == input.Length) break;
            var begin = pos;
            while (pos < input.Length && input[pos] != '=') pos++;
            if (pos == input.Length) throw Error("缺少别名定义的等号。");
            var names = input.Substring(begin, pos - begin).Trim().Split(','); pos++;
            foreach (var raw in names)
            {
                var name = raw.Trim();
                if (name.Equals("IFILE", StringComparison.OrdinalIgnoreCase))
                    throw Error("此版不展开 IFILE，请直接选择包含实际别名定义的文件。");
                if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+$")) throw Error("别名须由字母、数字、下划线、点或短横线组成。");
            }
            Skip(); begin = pos;
            if (pos == input.Length || input[pos] != '(') throw Error("需要括号形式的连接描述。");
            var depth = 0; quote = '\0';
            do
            {
                var c = input[pos++];
                if (c == '"' || c == '\'')
                { if (quote == '\0') quote = c; else if (quote == c) quote = '\0'; }
                else if (quote == '\0')
                {
                    if (c == '(' && ++depth > 64) throw Error("连接描述嵌套过深。");
                    if (c == ')') depth--;
                }
            } while (pos < input.Length && (depth > 0 || quote != '\0'));
            if (depth != 0 || quote != '\0') throw Error("连接描述括号或引号未闭合。");
            var descriptor = input.Substring(begin, pos - begin);
            if (!Regex.IsMatch(descriptor, @"^\(\s*DESCRIPTION(?:_LIST)?\s*=", RegexOptions.IgnoreCase))
                throw Error("仅支持 DESCRIPTION 或 DESCRIPTION_LIST 连接描述。");
            foreach (var raw in names)
            {
                var name = raw.Trim();
                if (entries.ContainsKey(name)) throw Error("存在重复别名，请先消除歧义。");
                entries.Add(name, descriptor);
            }
        }
        if (entries.Count == 0) throw new InvalidOperationException("TNS 文件中没有可用别名。");
        return entries;
    }

    private static int CountLines(string text, int end)
    { var count = 0; for (var i = 0; i < end; i++) if (text[i] == '\n') count++; return count; }
}
