using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

// A local display preference, not an identity or database authorization boundary.
public sealed class ReportVisibility
{
    public const string FileName = "report-visibility.xml";
    private readonly bool showAll;
    private readonly HashSet<string> reportIds;

    private ReportVisibility(bool showAll, IEnumerable<string> reportIds)
    { this.showAll = showAll; this.reportIds = new HashSet<string>(reportIds, StringComparer.OrdinalIgnoreCase); }

    public bool Includes(ReportDefinition report) => showAll || reportIds.Contains(report.Id);

    public static ReportVisibility Load(string path)
    {
        try
        {
            // Only a genuinely absent file uses the legacy display behavior; access errors must surface.
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            var root = XDocument.Load(reader).Root;
            if (root == null || root.Name != "ReportVisibility" || root.Attributes().Any(a => a.Name != "mode"))
                throw new InvalidDataException();
            var mode = (string?)root.Attribute("mode");
            if (mode != "all" && mode != "selected") throw new InvalidDataException();
            if (root.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw new InvalidDataException();
            var ids = new List<string>();
            foreach (var entry in root.Elements())
            {
                var id = (string?)entry.Attribute("id");
                if (entry.Name != "Report" || entry.HasElements || !string.IsNullOrWhiteSpace(entry.Value)
                    || entry.Attributes().Any(a => a.Name != "id" && a.Name != "name")
                    || string.IsNullOrWhiteSpace(id) || id != id!.Trim()) throw new InvalidDataException();
                ids.Add(id!);
            }
            // Avoid a misleading allow-list that is silently ignored in all mode.
            if (mode == "all" && ids.Count != 0) throw new InvalidDataException();
            return new ReportVisibility(mode == "all", ids);
        }
        catch (FileNotFoundException) { return new ReportVisibility(true, Array.Empty<string>()); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException || ex is System.Security.SecurityException)
        {
            // Do not echo arbitrary file contents or parser messages into the UI/log.
            throw new InvalidOperationException("无法读取报表显示配置 report-visibility.xml。请检查文件权限及格式：根节点 ReportVisibility，mode 为 all 或 selected，Report 必须填写 id。修正后重新启动程序。");
        }
    }
}
