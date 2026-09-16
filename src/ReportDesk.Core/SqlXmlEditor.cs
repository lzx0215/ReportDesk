using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

public sealed class SqlXmlSource
{
    // XML ordinal, NOT the imported query index (the importer skips empty SQL).
    public int Index { get; internal set; }
    public int QueryIndex { get; internal set; }
    public string Name { get; internal set; } = "";
    public string Kind { get; internal set; } = "";
    public string Sql { get; internal set; } = "";
    public bool Editable { get; internal set; }
}

public sealed class SqlXmlSnapshot
{
    public string Path { get; internal set; } = "";
    public string Hash { get; internal set; } = "";
    public IReadOnlyList<SqlXmlSource> Sources { get; internal set; } = Array.Empty<SqlXmlSource>();
    internal byte[] Bytes = Array.Empty<byte>();
    internal string Text = "";
    internal Encoding Encoding = Encoding.UTF8;
    internal int PreambleLength;
    internal XDocument Document = new();
}

public sealed class SqlXmlSaveResult
{
    public SqlXmlSnapshot Snapshot { get; internal set; } = new();
    public string BackupPath { get; internal set; } = "";
    public bool Changed { get; internal set; }
}

/// <summary>Edits a single existing SQL element. Does not execute SQL or serialize the report model.</summary>
public static class SqlXmlEditor
{
    public const int MaxSqlCharacters = 1024 * 1024;
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private static readonly string[] SourcePath = { "ReportQueryInfo", "QueryDataSource", "QueryDataSource" };

    public static SqlXmlSnapshot Read(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        CheckPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxFileBytes) throw new InvalidOperationException("查询 XML 过大，不能安全编辑。");
        var bytes = new byte[(int)input.Length];
        int offset = 0, count;
        while (offset < bytes.Length && (count = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
        if (offset != bytes.Length) throw new IOException("读取 XML 未完成。");
        return Parse(path, bytes);
    }

    private static SqlXmlSnapshot Parse(string path, byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidOperationException("保存后 XML 将超过编辑大小限制。");
        using var input = new MemoryStream(bytes);
        // Capture the parser-detected encoding before EOF; normalize XML values just as the importer does.
        using var raw = new XmlTextReader(input) { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Normalization = true };
        using var reader = XmlReader.Create(raw, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024, IgnoreWhitespace = false
        });
        reader.MoveToContent();
        var encoding = (Encoding)raw.Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != "ReportQueryInfo" || document.Root.Elements("QueryDataSource").Count() != 1)
            throw new InvalidOperationException("不是可编辑的单一 ReportQueryInfo 查询定义。");
        var preamble = encoding.GetPreamble();
        int skip = preamble.Length > 0 && bytes.Take(preamble.Length).SequenceEqual(preamble) ? preamble.Length : 0;
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Length > 16 * 1024 * 1024) throw new InvalidOperationException("XML 超过编辑大小限制。");
        if (!encoding.GetBytes(text).SequenceEqual(bytes.Skip(skip)))
            throw new InvalidOperationException("不能无损识别 XML 编码，未修改原文件。");
        int queryIndex = 0;
        var sources = document.Root.Element("QueryDataSource")!.Elements("QueryDataSource").Select((node, index) => {
            var sql = node.Element("Sql");
            var value = sql?.Value ?? "";
            var kind = node.Element("SqlType")?.Value ?? "";
            return new SqlXmlSource {
                Index = index, QueryIndex = string.IsNullOrWhiteSpace(value) ? -1 : queryIndex++,
                Name = node.Element("Name")?.Value ?? "", Kind = kind, Sql = value,
                Editable = (kind == "MainReportUsing" || kind == "DetailReportUsing") &&
                    !string.IsNullOrWhiteSpace(value) && node.Elements("Sql").Count() == 1 && !sql!.HasElements
            };
        }).ToArray();
        return new SqlXmlSnapshot { Path = path, Hash = ReportImporter.Hash(bytes), Sources = sources,
            Bytes = bytes, Text = text, Encoding = encoding, PreambleLength = skip, Document = document };
    }

    public static SqlXmlSaveResult Save(SqlXmlSnapshot opened, int sourceIndex, string sql, CancellationToken token = default)
    {
        if (opened == null) throw new ArgumentNullException(nameof(opened));
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("SQL 不能为空；本版不删除数据源。");
        if (sql.Length > MaxSqlCharacters) throw new InvalidOperationException("SQL 超过编辑大小限制。");
        XmlConvert.VerifyXmlChars(sql);
        token.ThrowIfCancellationRequested();
        var current = Read(opened.Path);
        if (current.Hash != opened.Hash) throw new InvalidOperationException("原 XML 已被修改。请先复制保留草稿，再重新读取文件后编辑；没有覆盖外部修改。");
        var source = current.Sources.SingleOrDefault(s => s.Index == sourceIndex);
        if (source == null || !source.Editable) throw new InvalidOperationException("该数据源不支持编辑；本版仅修改已有主表或明细 SQL。");
        if (source.Sql == sql) return new SqlXmlSaveResult { Snapshot = current };
        var element = current.Document.Root!.Element("QueryDataSource")!.Elements("QueryDataSource").ElementAt(sourceIndex).Element("Sql")!;
        var span = FindSqlContent(current.Text, sourceIndex);
        string content = element.Nodes().Any(n => n is XCData) && !sql.Contains("\r")
            ? "<![CDATA[" + sql.Replace("]]>", "]]]]><![CDATA[>") + "]]>"
            : sql.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xD;");
        string updated = current.Text.Substring(0, span.Start) + content + current.Text.Substring(span.End);
        var body = current.Encoding.GetBytes(updated); // Strict fallback: never replace unencodable characters with '?'.
        var bytes = current.Bytes.Take(current.PreambleLength).Concat(body).ToArray();
        var next = Parse(current.Path, bytes);
        if (next.Sources[sourceIndex].Sql != sql) throw new InvalidOperationException("SQL 写出校验失败，未修改原文件。");
        var directory = System.IO.Path.GetDirectoryName(current.Path)!;
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
        var temporary = System.IO.Path.Combine(directory, ".reportdesk-" + suffix + ".tmp");
        var backup = current.Path + "." + suffix + ".bak"; // Does not end in .xml: discovery must not import backups.
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            // Optimistic conflict check immediately before replacement, not a distributed file lock.
            if (Read(current.Path).Hash != current.Hash)
                throw new InvalidOperationException("保存前原 XML 已发生变化；未覆盖，请保留草稿并重新读取。");
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, current.Path, backup, false);
            // The commit point has passed. Do not report a later cancel as 'not saved'.
            return new SqlXmlSaveResult { Snapshot = next, BackupPath = backup, Changed = true };
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { ErrorLog.Write("SqlEditTemporaryCleanup", ex, includeMessage: false); }
        }
    }

    private static void CheckPath(string path)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许编辑已导入的查询 XML 文件。");
        for (string? entry = path; entry != null; entry = System.IO.Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不通过文件或目录链接写入 XML，请选择实际来源目录。");
    }

    private sealed class Frame
    {
        public string Name = "";
        public int ContentStart;
        public int SourceIndex = -1;
    }

    // The parser above validates XML; this tokenizer finds the original SQL text span.
    // Bytes outside that content (including unknown settings) are not serialized again.
    private static (int Start, int End) FindSqlContent(string text, int wanted)
    {
        var stack = new List<Frame>();
        int ordinal = -1, i = 0;
        while ((i = text.IndexOf('<', i)) >= 0)
        {
            if (At(text, i, "<!--")) { i = EndOf(text, i + 4, "-->"); continue; }
            if (At(text, i, "<![CDATA[")) { i = EndOf(text, i + 9, "]]>"); continue; }
            if (At(text, i, "<?")) { i = EndOf(text, i + 2, "?>"); continue; }
            int end = TagEnd(text, i + 1);
            if (At(text, i, "</"))
            {
                var frame = stack[stack.Count - 1];
                if (stack.Count == 4 && frame.Name == "Sql" && stack.Take(3).Select(f => f.Name).SequenceEqual(SourcePath)
                    && stack[2].SourceIndex == wanted) return (frame.ContentStart, i);
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                int n = i + 1;
                while (n < end && !char.IsWhiteSpace(text[n]) && text[n] != '/' && text[n] != '>') n++;
                var name = text.Substring(i + 1, n - i - 1);
                var frame = new Frame { Name = name, ContentStart = end + 1 };
                if (stack.Count == 2 && stack[0].Name == SourcePath[0] && stack[1].Name == SourcePath[1] && name == SourcePath[2])
                    frame.SourceIndex = ++ordinal;
                if (text[end - 1] != '/') stack.Add(frame);
            }
            i = end + 1;
        }
        throw new InvalidOperationException("无法唯一定位 SQL 内容，未修改原文件。");
    }
    private static bool At(string text, int at, string value) => at + value.Length <= text.Length && string.CompareOrdinal(text, at, value, 0, value.Length) == 0;
    private static int EndOf(string text, int start, string value)
    {
        var end = text.IndexOf(value, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("XML 结构不完整。");
        return end + value.Length;
    }
    private static int TagEnd(string text, int at)
    {
        char quote = '\0';
        for (int i = at; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '\'' || c == '"') quote = c;
            else if (c == '>') return i;
        }
        throw new InvalidOperationException("XML 标签不完整。");
    }
}
