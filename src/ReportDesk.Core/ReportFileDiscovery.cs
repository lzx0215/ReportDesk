using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

// Import-time/source-file diagnostics only. None of these types changes catalog.json.
public sealed class RelatedXmlFile
{
    public string Role { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

public sealed class ReportFileInventory
{
    public string Root { get; set; } = "";
    public List<string> Queries { get; } = new();
    public List<string> Layouts { get; } = new();
    public List<string> OtherFiles { get; } = new();
    public List<string> Warnings { get; } = new();
    public int OtherXml { get; set; }
    public int ScannedXml { get; set; }
}

public static class ReportFileDiscovery
{
    internal static XmlReaderSettings ReaderSettings() => new()
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };

    public static ReportFileInventory Scan(string directory, CancellationToken cancellation = default, Action<string>? progress = null)
    {
        var result = new ReportFileInventory { Root = Path.GetFullPath(directory) };
        if (!Directory.Exists(result.Root)) throw new InvalidOperationException("所选目录不存在。");
        if ((File.GetAttributes(result.Root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("请选择实际目录，不使用目录链接作为扫描入口。");
        var pending = new Stack<string>(); pending.Push(result.Root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested(); var current = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current); }
            catch (Exception ex) when (IsFileError(ex)) { Warning(result, "无法读取目录", current, ex); continue; }
            foreach (var entry in entries.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    { result.Warnings.Add("未跟随链接：" + entry); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    if (!Path.GetExtension(entry).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    result.ScannedXml++;
                    // Classify by XML root, not filename. Do not load proprietary layout bodies.
                    using var stream = File.OpenRead(entry);
                    using var reader = XmlReader.Create(stream, ReaderSettings()); reader.MoveToContent();
                    if (reader.LocalName == "ReportQueryInfo") result.Queries.Add(entry);
                    else if (reader.LocalName == "Spread" && reader.GetAttribute("class") == "FarPoint.Win.Spread.FpSpread") result.Layouts.Add(entry);
                    else { result.OtherXml++; result.OtherFiles.Add(entry); }
                    if (result.ScannedXml % 100 == 0) progress?.Invoke("已扫描 " + result.ScannedXml + " 个 XML，识别 " + result.Queries.Count + " 份查询定义…");
                }
                catch (Exception ex) when (IsFileError(ex) || ex is XmlException) { Warning(result, "无法识别 XML/文件", entry, ex); }
            }
        }
        result.Queries.Sort(StringComparer.OrdinalIgnoreCase); result.Layouts.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public static List<RelatedXmlFile> Match(string queryPath, ReportFileInventory inventory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(queryPath);
        if (!Within(inventory.Root, full) || !inventory.Queries.Contains(full, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("查询定义不在本次扫描范围内。");
        using var reader = XmlReader.Create(full, ReaderSettings()); var root = XDocument.Load(reader).Root!;
        var results = new List<RelatedXmlFile>();
        var name = Path.GetFileNameWithoutExtension(full);
        if (name.EndsWith("查询设置", StringComparison.Ordinal))
        {
            var expected = name.Substring(0, name.Length - "查询设置".Length) + "报表设置.xml";
            results.Add(Resolve("主表版式（按命名约定查找）", expected, full, inventory, false));
        }
        var detail = root.Element("ReportInfo");
        if (bool.TryParse(detail?.Element("IsDetail")?.Value, out var enabled) && enabled)
        {
            var reference = detail?.Element("DetailDirectory")?.Value ?? "";
            results.Add(Resolve("明细版式（XML 显式引用）", reference, full, inventory, true));
        }
        cancellation.ThrowIfCancellationRequested(); return results;
    }

    private static RelatedXmlFile Resolve(string role, string reference, string source, ReportFileInventory inventory, bool explicitReference)
    {
        var result = new RelatedXmlFile { Role = role, Reference = reference, Status = "Missing" };
        if (string.IsNullOrWhiteSpace(reference)) return result;
        var normalized = reference.Trim().Replace('/', '\\');
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal) || normalized.Split('\\').Contains("..") ||
            normalized.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0 || !normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        { result.Status = "Rejected"; return result; }
        var exact = new List<string>();
        try
        {
            if (normalized.IndexOf(':') >= 0)
            {
                if (normalized.Length < 3 || normalized[1] != ':' || normalized[2] != '\\') { result.Status = "Rejected"; return result; }
                var absolute = Path.GetFullPath(normalized);
                if (!Within(inventory.Root, absolute)) { result.Status = "Rejected"; return result; }
                exact.Add(absolute);
            }
            else
            {
                // HIS \Config\Xml is relative to the selected HIS root, not the drive root.
                if (!normalized.StartsWith("\\", StringComparison.Ordinal)) exact.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, normalized)));
                exact.Add(Path.GetFullPath(Path.Combine(inventory.Root, normalized.TrimStart('\\'))));
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        { result.Status = "Rejected"; return result; }
        var found = exact.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => Within(inventory.Root, p) && inventory.Layouts.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (found.Count > 0)
        {
            result.Paths = found;
            result.Status = found.Count > 1 ? "Ambiguous" : explicitReference ? "Matched" : "Candidate";
            return result;
        }
        // A relocated/flattened directory is not evidence of the original reference target.
        result.Paths = inventory.Layouts.Where(p => Path.GetFileName(p).Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase)).ToList();
        result.Status = result.Paths.Count == 0 ? "Missing" : result.Paths.Count == 1 ? "Candidate" : "Ambiguous";
        return result;
    }

    public static string InferRoot(string queryPath)
    {
        var full = Path.GetFullPath(queryPath);
        using var reader = XmlReader.Create(full, ReaderSettings()); var root = XDocument.Load(reader).Root!;
        var reference = (root.Element("QueryFilePath")?.Value ?? "").Replace('/', '\\');
        // Infer an HIS root only from an exact self-reference suffix. Otherwise stay in the source directory.
        if (reference.StartsWith("\\", StringComparison.Ordinal) && !reference.StartsWith("\\\\", StringComparison.Ordinal) &&
            !reference.Split('\\').Contains("..") && full.EndsWith(reference, StringComparison.OrdinalIgnoreCase))
        {
            var prefix = full.Substring(0, full.Length - reference.Length);
            if (prefix.Length > 0) return Path.GetFullPath(prefix.EndsWith(":", StringComparison.Ordinal) ? prefix + "\\" : prefix);
        }
        return Path.GetDirectoryName(full)!;
    }

    internal static bool Within(string root, string path) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool IsFileError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
    private static void Warning(ReportFileInventory inventory, string label, string path, Exception ex)
    { ErrorLog.Write("DiscoverReportXml", ex, includeMessage: false); inventory.Warnings.Add(label + "：" + path + "（" + ex.GetType().Name + "）"); }
}
