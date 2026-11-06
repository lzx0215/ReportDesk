using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

// A snapshot always refers to a server-selected existing file, never a client-provided path.
public sealed class FullXmlSnapshot
{
    public string Path { get; internal set; } = "";
    public string Hash { get; internal set; } = "";
    public string Text { get; internal set; } = "";
    public string RootName { get; internal set; } = "";
    internal byte[] Bytes = Array.Empty<byte>();
    internal Encoding Encoding = Encoding.UTF8;
    internal int PreambleLength;
}

public sealed class FullXmlSaveResult
{
    public FullXmlSnapshot Snapshot { get; internal set; } = new();
    public bool Changed { get; internal set; }
}

/// <summary>Edits an entire existing report XML. Validates syntax, not report semantics.</summary>
public static class FullXmlEditor
{
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private const int MaxCharacters = 16 * 1024 * 1024;

    public static FullXmlSnapshot Read(string path, string expectedRoot)
    {
        CheckRoot(expectedRoot);
        path = System.IO.Path.GetFullPath(path);
        CheckPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxFileBytes) throw new InvalidOperationException("XML 文件超过大小限制，不能编辑。");
        var bytes = new byte[(int)input.Length];
        int offset = 0, count;
        while (offset < bytes.Length && (count = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
        if (offset != bytes.Length) throw new IOException("读取 XML 未完成。");
        return Parse(path, bytes, expectedRoot);
    }

    private static void CheckRoot(string root)
    {
        if (root != "ReportQueryInfo" && root != "Spread")
            throw new InvalidOperationException("不支持此 XML 类型。");
    }

    private static void CheckPath(string path)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许编辑 XML 文件。");
        for (string? entry = path; entry != null; entry = System.IO.Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不通过文件或目录链接写入 XML。");
    }

    private static FullXmlSnapshot Parse(string path, byte[] bytes, string expectedRoot)
    {
        try { return ParseCore(path, bytes, expectedRoot); }
        catch (XmlException ex) { throw new InvalidOperationException("XML 格式无效或包含不允许的 DTD（行 " + ex.LineNumber + "，列 " + ex.LinePosition + "），未保存。", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidOperationException("XML 编码无效，未保存。", ex); }
    }

    private static FullXmlSnapshot ParseCore(string path, byte[] bytes, string expectedRoot)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidOperationException("XML 文件超过大小限制。");
        using var input = new MemoryStream(bytes);
        using var raw = new XmlTextReader(input) { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Normalization = true };
        using var reader = XmlReader.Create(raw, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters, IgnoreWhitespace = false
        });
        reader.MoveToContent();
        var encoding = (Encoding)raw.Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != expectedRoot || (expectedRoot == "Spread" && (string?)document.Root.Attribute("class") != "FarPoint.Win.Spread.FpSpread"))
            throw new InvalidOperationException("XML 根节点或报表模板类型不匹配，未保存。");
        var preamble = encoding.GetPreamble();
        int skip = preamble.Length > 0 && bytes.Take(preamble.Length).SequenceEqual(preamble) ? preamble.Length : 0;
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        if (text.Length > MaxCharacters || !encoding.GetBytes(text).SequenceEqual(bytes.Skip(skip)))
            throw new InvalidOperationException("XML 编码无法无损识别或文本超出大小限制，未保存。");
        return new FullXmlSnapshot { Path = path, Hash = ReportImporter.Hash(bytes), Text = text,
            RootName = expectedRoot, Bytes = bytes, Encoding = encoding, PreambleLength = skip };
    }

    // Browsers normalize textarea CRLF to LF. Restore a uniform source style so a small edit
    // does not silently alter every line; mixed source styles cannot be reconstructed safely.
    private static string NormalizeNewlines(string original, string text)
    {
        bool crlf = false, lf = false, cr = false;
        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] == '\r') { if (i + 1 < original.Length && original[i + 1] == '\n') { crlf = true; i++; } else cr = true; }
            else if (original[i] == '\n') lf = true;
        }
        if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1)
            throw new InvalidOperationException("原 XML 使用混合换行格式，无法安全保留；未保存。");
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return crlf ? normalized.Replace("\n", "\r\n") : cr ? normalized.Replace("\n", "\r") : normalized;
    }

    public static FullXmlSnapshot Prepare(FullXmlSnapshot opened, string text, CancellationToken token = default)
    {
        if (opened == null) throw new ArgumentNullException(nameof(opened));
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (text.Length > MaxCharacters) throw new InvalidOperationException("XML 文本超过大小限制，未保存。");
        token.ThrowIfCancellationRequested();
        var current = Read(opened.Path, opened.RootName);
        if (current.Hash != opened.Hash) throw new InvalidOperationException("原 XML 已发生变化；未覆盖，请保留草稿并重新读取。");
        text = NormalizeNewlines(current.Text, text);
        if (text == current.Text) return current;
        byte[] body;
        try { body = current.Encoding.GetBytes(text); }
        catch (EncoderFallbackException ex) { throw new InvalidOperationException("内容无法用原 XML 编码表示；未保存。", ex); }
        var bytes = current.Bytes.Take(current.PreambleLength).Concat(body).ToArray();
        var next = Parse(current.Path, bytes, current.RootName);
        if (next.Encoding.CodePage != current.Encoding.CodePage || next.PreambleLength != current.PreambleLength || next.Text != text)
            throw new InvalidOperationException("不允许更改 XML 声明编码或 BOM；未保存。");
        return next;
    }

    public static void Validate(FullXmlSnapshot opened, string text, CancellationToken token = default) => Prepare(opened, text, token);

    public static FullXmlSaveResult Save(FullXmlSnapshot opened, string text, CancellationToken token = default)
    {
        var next = Prepare(opened, text, token);
        if (next.Hash == opened.Hash) return new FullXmlSaveResult { Snapshot = next };
        var current = opened;
        var bytes = next.Bytes;
        var directory = System.IO.Path.GetDirectoryName(current.Path)!;
        var temporary = System.IO.Path.Combine(directory, ".reportdesk-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            if (Read(current.Path, current.RootName).Hash != current.Hash)
                throw new InvalidOperationException("保存前原 XML 已发生变化；未覆盖，请保留草稿并重新读取。");
            token.ThrowIfCancellationRequested();
            File.Replace(temporary, current.Path, null, false);
            try
            {
                var persisted = Read(current.Path, current.RootName);
                if (persisted.Hash != next.Hash || persisted.Text != next.Text) throw new IOException("保存后的磁盘内容与提交内容不一致。");
                return new FullXmlSaveResult { Snapshot = persisted, Changed = true };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is InvalidOperationException || ex is ArgumentException)
            { throw new SqlXmlVerificationException(current.Path, ex); }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) { ErrorLog.Write("FullXmlTemporaryCleanup", ex, includeMessage: false); }
        }
    }
}
