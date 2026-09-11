using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

public sealed class ReportLocation
{
    public string Path { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Match { get; set; } = "";
    public bool Candidate { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class ReportLocations
{
    public const string FileName = "report-locations.xml";
    private sealed class Entry
    {
        public string Selector = "", Value = "";
        public List<ReportLocation> Locations = new();
    }
    private readonly List<Entry> entries = new();
    public List<string> Warnings { get; } = new();
    public static ReportLocations Load(string file)
    {
        var result = new ReportLocations();
        // User-confirmed menu path, not inferred from report name/category.
        result.entries.Add(new Entry { Selector = "file", Value = "普通门诊处方记录查询设置.xml", Locations = new()
        {
            new ReportLocation { Path = "报表中心 → 各职能科室用表 → 药剂科 → 抗菌药物查询", Evidence = "用户提供（2026-09-11）；尚无完整菜单导出", Match = "文件名关联，版本未核对" }
        } });
        using (var stream = typeof(ReportLocations).Assembly.GetManifestResourceStream("ReportDesk.HisMenuCandidates"))
        {
            if (stream != null)
                foreach (var report in XDocument.Load(stream).Root!.Elements("Report"))
                    result.entries.Add(new Entry { Selector = "hash", Value = (string)report.Attribute("hash")!,
                        Locations = report.Elements("Location").Select(l => new ReportLocation {
                            Path = string.Join(" → ", l.Elements("Segment").Select(s => s.Value)), Candidate = true,
                            Active = (string?)l.Attribute("active") == "true",
                            Evidence = "2026-09-11 菜单/资源 Excel；菜单 ID " + (string?)l.Attribute("menu") + "，资源 ID " + (string?)l.Attribute("resource"),
                            Match = "同名通用报表菜单候选；缺少菜单到 XML 的绑定记录" }).ToList() });
        }
        if (!File.Exists(file)) return result;
        try
        {
            using var reader = XmlReader.Create(file, ReportFileDiscovery.ReaderSettings());
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "ReportLocations" || (string?)root.Attribute("version") != "1") throw new InvalidOperationException();
            var loaded = new List<Entry>();
            foreach (var node in root.Elements())
            {
                if (node.Name != "Report") throw new InvalidOperationException();
                var keys = new[] { "id", "hash", "file" }.Where(k => node.Attribute(k) != null).ToArray();
                if (keys.Length != 1 || node.Attributes().Count() != 1) throw new InvalidOperationException();
                var value = ((string?)node.Attribute(keys[0]) ?? "").Trim();
                if (value.Length == 0 || (keys[0] == "file" && (value.IndexOfAny(new[] { '/', '\\', ':', '*' }) >= 0 || !value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException();
                var entry = new Entry { Selector = keys[0], Value = value };
                foreach (var location in node.Elements())
                {
                    var segments = location.Elements("Segment").Select(s => s.Value.Trim()).ToArray();
                    var evidence = ((string?)location.Attribute("evidence") ?? "").Trim();
                    if (location.Name != "Location" || evidence.Length == 0 || segments.Length < 2 || segments.Any(s => s.Length == 0) || location.Elements().Any(s => s.Name != "Segment" || s.HasElements)) throw new InvalidOperationException();
                    entry.Locations.Add(new ReportLocation { Path = string.Join(" → ", segments), Evidence = evidence,
                        Match = keys[0] == "file" ? "文件名关联，版本未核对" : keys[0] == "hash" ? "查询文件哈希匹配" : "报表 ID 匹配" });
                }
                if (entry.Locations.Count == 0) throw new InvalidOperationException();
                loaded.Add(entry);
            }
            result.entries.AddRange(loaded);
        }
        catch (Exception ex) when (ex is XmlException || ReportFileDiscovery.IsFileError(ex) || ex is InvalidOperationException || ex is ArgumentException)
        { result.Warnings.Add("report-locations.xml 无法完整读取，本次未使用外部位置配置；请检查 version、报表标识、路径层级及 evidence。已知位置可能不完整。"); }
        return result;
    }
    public List<ReportLocation> For(ReportDefinition report) => report.IsDemo ? new() : entries.Where(e =>
        string.Equals(e.Value, e.Selector == "id" ? report.Id : e.Selector == "hash" ? report.SourceHash : System.IO.Path.GetFileName(report.SourcePath), StringComparison.OrdinalIgnoreCase))
        .SelectMany(e => e.Locations).GroupBy(l => new { l.Path, l.Evidence, l.Match }).Select(g => g.First()).ToList();

    public string CategoryFor(ReportDefinition report)
    {
        if (!string.IsNullOrWhiteSpace(report.Category) && report.Category != "未分类") return report.Category;
        var paths = For(report).Where(l => l.Active).ToList();
        var confirmed = paths.Where(l => !l.Candidate).ToList();
        if (confirmed.Count > 0) paths = confirmed;
        var categories = paths.Select(l => l.Path.Split(new[] { " → " }, StringSplitOptions.None)).Where(s => s.Length >= 2)
            .Select(s => s[s.Length - 2]).Distinct().ToArray();
        return categories.Length == 0 ? "未分类" : (confirmed.Count == 0 ? "候选 · " : "") + string.Join(" / ", categories);
    }
}
