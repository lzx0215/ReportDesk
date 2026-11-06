#if REPORTDESK_WEB
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using ReportDesk.Core;

namespace ReportDesk.Host;

internal sealed partial class Service
{
    // Display metadata only. This path never describes/executes SQL or changes the catalog/XML.
    private sealed class HeaderColumn
    {
        public int ordinal { get; set; }
        public string label { get; set; } = "";
        public int width { get; set; }
    }
    private sealed class HeaderPreview
    {
        public string title { get; set; } = "";
        public HeaderColumn[] columns { get; set; } = Array.Empty<HeaderColumn>();
        public string message { get; set; } = "";
    }
    private static InvalidDataException HeaderUnsupported() => new("Unsupported static header");
    private static int HeaderInt(XElement node, string name)
        => int.TryParse((string?)node.Attribute(name), out var number) ? number : throw HeaderUnsupported();
    private static XElement HeaderSheet(XElement root, string section)
    {
        var sheets = root.Element(section)?.Element("Sheets")?.Elements("Sheet").ToArray();
        if (sheets == null || sheets.Length != 1 || (string?)sheets[0].Attribute("index") != "0") throw HeaderUnsupported();
        return sheets[0];
    }
    private HeaderPreview WebHeaderPreview(ReportDefinition report, int sourceIndex)
    {
        var preview = new HeaderPreview { title = report.Name };
        try
        {
            if (sourceIndex < 0 || sourceIndex >= report.Queries.Count) throw HeaderUnsupported();
            var query = report.Queries[sourceIndex];
            if (query.Kind != "MainReportUsing" && query.Kind != "DetailReportUsing") throw HeaderUnsupported();
            if (query.ResultRulesXml.Length > 0)
            {
                var rules = XElement.Parse(query.ResultRulesXml);
                if (string.Equals(rules.Element("IsCross")?.Value, "true", StringComparison.OrdinalIgnoreCase) ||
                    new[] { "CrossRows", "CrossColumns", "CrossValues", "CrossCombinColumns", "CrossGroupColumns" }
                        .Any(n => rules.Element(n)?.HasElements == true || !string.IsNullOrWhiteSpace(rules.Element(n)?.Value)))
                {
                    preview.message = "此报表含动态交叉列，查询前无法确定完整表头。";
                    return preview;
                }
            }
            var role = query.Kind == "MainReportUsing" ? "主表版式" : "明细版式";
            if (!webRelated.TryGetValue(report.Id, out var related)) throw HeaderUnsupported();
            var matches = related.Where(f => f.Role.StartsWith(role, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Paths.Count != 1 ||
                (matches[0].Status != "Matched" && matches[0].Status != "Candidate"))
            {
                preview.message = "未找到唯一对应的报表模板，暂不能预览表头。";
                return preview;
            }
            var path = matches[0].Paths[0];
            webValidatePath?.Invoke(path); // Recheck containment and links immediately before reading.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 32 * 1024 * 1024) throw HeaderUnsupported();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "Spread" || (string?)root.Attribute("class") != "FarPoint.Win.Spread.FpSpread") throw HeaderUnsupported();
            var settings = HeaderSheet(root, "Settings");
            var presentation = HeaderSheet(root, "Presentation");
            var data = HeaderSheet(root, "Data");
            var count = (int?)settings.Element("Categories")?.Element("Layout")?.Element("ColumnCount") ?? 0;
            if (count < 1 || count > 1024) throw HeaderUnsupported();
            var area = data.Element("DataArea") ?? throw HeaderUnsupported();
            if (HeaderInt(area, "columns") != count) throw HeaderUnsupported();
            var cells = area.Element("Cells")?.Elements("Cell").ToArray() ?? throw HeaderUnsupported();
            var tags = root.Descendants("Tag").ToArray();
            if (tags.Length != 1 || tags[0].Value != query.Name || tags[0].Parent?.Parent != area.Element("Cells")) throw HeaderUnsupported();
            var dataRow = HeaderInt(tags[0].Parent!, "row");
            if (dataRow < 1 || HeaderInt(tags[0].Parent!, "column") != 0) throw HeaderUnsupported();
            int headerRow = dataRow - 1;
            var headerCells = cells.Where(c => HeaderInt(c, "row") <= headerRow).ToArray();
            if (headerCells.Any(c => HeaderInt(c, "row") < 0 || HeaderInt(c, "column") < 0 || HeaderInt(c, "column") >= count) ||
                headerCells.GroupBy(c => (string?)c.Attribute("row") + ":" + (string?)c.Attribute("column")).Any(g => g.Count() > 1) ||
                headerCells.Any(c => HeaderInt(c, "row") < headerRow && HeaderInt(c, "column") != 0 && !string.IsNullOrWhiteSpace(c.Element("Data")?.Value)) ||
                root.Descendants().Any(e => (e.Name.LocalName == "Formula" || e.Name.LocalName == "Formulas") && !string.IsNullOrWhiteSpace(e.Value)) ||
                root.Descendants().Attributes().Any(a => a.Name.LocalName.Equals("Formula", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.Value)))
                throw HeaderUnsupported();
            var axis = presentation.Element("AxisModels")?.Element("Column") ?? throw HeaderUnsupported();
            if (HeaderInt(axis, "count") != count) throw HeaderUnsupported();
            var items = axis.Element("Items")?.Elements("Item").ToArray() ?? throw HeaderUnsupported();
            if (items.GroupBy(i => HeaderInt(i, "index")).Any(g => g.Count() > 1)) throw HeaderUnsupported();
            var defaults = items.SingleOrDefault(i => HeaderInt(i, "index") == -1);
            var columns = new List<HeaderColumn>();
            for (int i = 0; i < count; i++)
            {
                var item = items.SingleOrDefault(c => HeaderInt(c, "index") == i);
                var visible = item?.Element("Visible")?.Value ?? defaults?.Element("Visible")?.Value ?? "true";
                if (!bool.TryParse(visible, out var show)) throw HeaderUnsupported();
                if (!show) continue;
                var cell = headerCells.SingleOrDefault(c => HeaderInt(c, "row") == headerRow && HeaderInt(c, "column") == i);
                var label = cell?.Element("Data")?.Value ?? "";
                if (string.IsNullOrWhiteSpace(label) || label.Length > 1024) throw HeaderUnsupported();
                var size = item?.Element("Size")?.Value ?? defaults?.Element("Size")?.Value;
                var width = size == null ? 120 : int.TryParse(size, out var parsed) && parsed > 0 ? parsed : throw HeaderUnsupported();
                columns.Add(new HeaderColumn { ordinal = i, label = label, width = width });
            }
            if (columns.Count == 0) throw HeaderUnsupported();
            var spans = presentation.Element("SpanModels");
            if (spans?.Elements().Any(e => e.Name != "DataArea" && e.HasElements) == true) throw HeaderUnsupported();
            foreach (var span in spans?.Descendants("CellRange") ?? Enumerable.Empty<XElement>())
            {
                int row = HeaderInt(span, "Row"), rows = HeaderInt(span, "RowCount");
                if (row < 0 || rows < 1 || (long)row + rows > headerRow || HeaderInt(span, "Column") != 0 ||
                    HeaderInt(span, "ColumnCount") < columns.Last().ordinal + 1 || HeaderInt(span, "ColumnCount") > count)
                    throw HeaderUnsupported();
            }
            var titles = headerCells.Where(c => HeaderInt(c, "row") < headerRow && HeaderInt(c, "column") == 0)
                .OrderBy(c => HeaderInt(c, "row")).Select(c => c.Element("Data")?.Value ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (titles.Any(t => t.Length > 1024)) throw HeaderUnsupported();
            if (titles.Length > 0) preview.title = string.Join("\n", titles);
            preview.columns = columns.ToArray();
            preview.message = matches[0].Status == "Candidate" ? "表头来自同名候选模板（已核对数据源标记），关联尚未人工确认。" : "";
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException ||
            ex is InvalidOperationException || ex is ArgumentException || ex is FormatException ||
            ex is OverflowException || ex is System.Security.SecurityException)
        {
            ErrorLog.Write("ReadHeaderPreview", ex, includeMessage: false);
            preview.columns = Array.Empty<HeaderColumn>();
            preview.message = "模板暂不可读或表头结构尚未适配，暂不能预览表头；仍可按原流程查询。";
        }
        return preview;
    }
}
#endif
