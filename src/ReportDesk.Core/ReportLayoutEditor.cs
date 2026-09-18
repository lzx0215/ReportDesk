using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

public sealed class LayoutColumn
{
    public int Index { get; internal set; }
    public int OriginalIndex { get; internal set; }
    public string Field { get; internal set; } = "";
    public string Header { get; set; } = "";
    public int Width { get; set; }
    public bool Hidden { get; internal set; }
}

public sealed class LayoutPlan
{
    public string Path { get; internal set; } = "";
    public string Hash { get; internal set; } = "";
    public IReadOnlyList<LayoutColumn> Columns { get; internal set; } = Array.Empty<LayoutColumn>();
    internal SqlXmlSnapshot Query = null!;
    public string QueryHash => Query.Hash;
    public int SourceIndex { get; internal set; }
    public string Sql { get; internal set; } = "";
    public bool Reconciled { get; internal set; }
    internal int HeaderRow, DataRow, OldCount;
    internal LayoutFile File = null!;
}

internal sealed class LayoutFile
{
    internal byte[] Bytes = Array.Empty<byte>();
    internal Encoding Encoding = Encoding.UTF8;
    internal int Preamble;
    internal XDocument Document = new();
}

// Deliberately small, fail-closed editor for a single ordinal-bound table, not a general Spread designer.
public static class ReportLayoutEditor
{
    private static InvalidOperationException Unsupported(string why) => new("不能自动同步：" + why + "。原文件未修改，请使用 HIS 模板设计器处理。");
    private static int Int(XElement node, string attribute) => (int?)node.Attribute(attribute) ?? throw Unsupported("模板坐标不完整");
    private static XElement Sheet(XDocument doc, string section)
    {
        var sheets = doc.Root?.Element(section)?.Element("Sheets")?.Elements("Sheet").ToArray();
        if (sheets == null || sheets.Length != 1 || (string?)sheets[0].Attribute("index") != "0")
            throw Unsupported("仅支持单工作表模板");
        return sheets[0];
    }
    private static LayoutFile Read(string path)
    {
        SqlXmlEditor.CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32 * 1024 * 1024) throw Unsupported("模板过大");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }
    private static LayoutFile Parse(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var raw = new XmlTextReader(input) { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Normalization = true };
        using var reader = XmlReader.Create(raw, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        reader.Read();
        var declaration = reader.NodeType == XmlNodeType.XmlDeclaration
            ? new XDeclaration("1.0", reader.GetAttribute("encoding"), reader.GetAttribute("standalone")) : null;
        reader.MoveToContent();
        var encoding = (Encoding)raw.Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        doc.Declaration = declaration;
        if (doc.Root?.Name != "Spread" || (string?)doc.Root.Attribute("class") != "FarPoint.Win.Spread.FpSpread") throw Unsupported("不是已支持的 Spread 模板");
        var preamble = encoding.GetPreamble();
        return new LayoutFile { Bytes = bytes, Document = doc, Encoding = encoding,
            Preamble = preamble.Length > 0 && bytes.Take(preamble.Length).SequenceEqual(preamble) ? preamble.Length : 0 };
    }
    private static byte[] Encode(LayoutFile file, XDocument doc)
    {
        // StringWriter with the original encoding keeps the XML declaration consistent, without adding a BOM.
        using var text = new EncodedWriter(file.Encoding);
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = doc.Declaration == null,
            NewLineHandling = NewLineHandling.Entitize })) doc.Save(writer);
        var bytes = file.Bytes.Take(file.Preamble).Concat(file.Encoding.GetBytes(text.ToString())).ToArray();
        Parse(bytes); // Strict encoding + XML validation before any write.
        return bytes;
    }
    private sealed class EncodedWriter : StringWriter
    {
        private readonly Encoding encoding;
        internal EncodedWriter(Encoding value) : base(CultureInfo.InvariantCulture) { encoding = value; }
        public override Encoding Encoding => encoding;
    }

    public static void ValidateSource(SqlXmlSnapshot query, int sourceIndex)
    {
        var source = query.Sources.SingleOrDefault(s => s.Index == sourceIndex);
        if (source == null || !source.Editable || string.IsNullOrWhiteSpace(source.Name)) throw Unsupported("数据源不可编辑或未命名");
        if (query.Sources.Count(s => s.Kind == source.Kind) != 1) throw Unsupported("同一模板存在多个同用途数据源");
        var node = query.Document.Root!.Element("QueryDataSource")!.Elements("QueryDataSource").ElementAt(sourceIndex);
        if (node.Element("IsCross")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
            new[] { "CrossRows", "CrossColumns", "CrossValues", "CrossCombinColumns", "CrossGroupColumns", "SumRows", "RowGroup" }
                .Any(n => node.Element(n)?.HasElements == true || !string.IsNullOrWhiteSpace(node.Element(n)?.Value)))
            throw Unsupported("存在交叉表、分组或行汇总规则");
        var group = query.Document.Root.Element("TableGroup");
        if (!string.IsNullOrWhiteSpace(group?.Element("GroupCondition")?.Value) || !string.IsNullOrWhiteSpace(group?.Element("QueryDataSource")?.Element("Sql")?.Value))
            throw Unsupported("存在分组数据源");
    }

    public static LayoutPlan Preview(SqlXmlSnapshot query, int sourceIndex, string sql, string layoutPath,
        IReadOnlyList<string> oldFields, IReadOnlyList<string> newFields)
    {
        ValidateSource(query, sourceIndex);
        SqlXmlEditor.Prepare(query, sourceIndex, sql, CancellationToken.None);
        foreach (var fields in new[] { oldFields, newFields })
            if (fields.Count == 0 || fields.Count > 256 || fields.Any(string.IsNullOrWhiteSpace) || fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count)
                throw Unsupported("字段为空、重名或超过 256 列，请为每个字段设置唯一别名");
        var file = Read(layoutPath);
        var doc = file.Document;
        var settings = Sheet(doc, "Settings"); var presentation = Sheet(doc, "Presentation"); var data = Sheet(doc, "Data");
        var count = (int?)settings.Element("Categories")?.Element("Layout")?.Element("ColumnCount") ?? -1;
        if (count < 1 || count > oldFields.Count) throw Unsupported("模板列数超过已保存 SQL 字段数或列数无效，无法安全补同步");
        bool reconciled = count < oldFields.Count;
        if (doc.Descendants().Any(e => (e.Name.LocalName == "Formula" || e.Name.LocalName == "Formulas") && !string.IsNullOrWhiteSpace(e.Value)) ||
            doc.Descendants().Attributes().Any(a => a.Name.LocalName.Equals("Formula", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.Value)))
            throw Unsupported("模板包含公式");
        if (doc.Descendants().Any(e => e.Name.LocalName.StartsWith("Print", StringComparison.OrdinalIgnoreCase) && (e.HasElements || !string.IsNullOrWhiteSpace(e.Value))))
            throw Unsupported("模板含自定义打印范围或打印设置");
        var area = data.Element("DataArea") ?? throw Unsupported("缺少数据区域");
        var cells = area.Element("Cells") ?? throw Unsupported("缺少单元格定义");
        var tags = doc.Descendants("Tag").ToArray();
        if (tags.Length != 1 || tags[0].Value != query.Sources.Single(s => s.Index == sourceIndex).Name || tags[0].Parent?.Parent != cells)
            throw Unsupported("不能唯一确认模板与数据源的绑定");
        var dataRow = Int(tags[0].Parent!, "row");
        if (Int(tags[0].Parent!, "column") != 0 || (dataRow != 1 && dataRow != 2)) throw Unsupported("数据起点不是首列或存在多层表头");
        var headerRow = dataRow - 1;
        if (cells.Elements("Cell").Any(c => Int(c, "row") < headerRow && Int(c, "column") != 0)) throw Unsupported("标题区包含非整行内容");
        var axis = presentation.Element("AxisModels")?.Element("Column") ?? throw Unsupported("缺少列宽定义");
        if (Int(axis, "count") != count || Int(area, "columns") != count) throw Unsupported("模板列数定义不一致");
        if (data.Elements().Where(e => e.Name != "DataArea").Descendants("Cell").Any() ||
            cells.Elements("Cell").Any(c => c.Elements().Any(e => e.Name != "Data" && e.Name != "Tag"))) throw Unsupported("包含额外的单元格绑定或表头数据模型");
        if (cells.Elements("Cell").Any(c => Int(c, "row") >= dataRow && c.Element("Data") != null)) throw Unsupported("数据区包含固定内容或汇总行");
        if (cells.Elements("Cell").GroupBy(c => (string?)c.Attribute("row") + ":" + (string?)c.Attribute("column")).Any(g => g.Count() > 1)) throw Unsupported("单元格坐标重复");
        var spans = presentation.Element("SpanModels");
        if (spans != null && spans.Elements().Any(e => e.Name != "DataArea" && e.HasElements)) throw Unsupported("存在复杂表头合并");
        foreach (var span in spans?.Descendants("CellRange") ?? Enumerable.Empty<XElement>())
            if (headerRow != 1 || Int(span, "Row") != 0 || Int(span, "Column") != 0 || Int(span, "RowCount") != 1 || Int(span, "ColumnCount") != count)
                throw Unsupported("存在复杂合并单元格；仅支持整行标题合并");
        var emptyDrawingNames = new[] { "Sheets", "Sheet", "DrawingContainer", "Top", "Left", "Height", "Width", "Name", "Text", "ForeColor", "BackColor", "AlignHorz", "AlignVert", "AlphaBlendBackColor", "Font", "PictureTransparencyColor", "PictureTransparencyTolerance", "ShapeOutlineColor", "ShapeOutlineThickness", "ShapeOutlineStyle", "RotationAngle", "PictureRotationAngle", "TextRotationAngle", "Anchor", "Locked", "CanRotate", "CanPrint", "SizeProportional", "CanMove", "CanSize" };
        if (doc.Root!.Element("Drawing")?.Descendants().Any(e => !emptyDrawingNames.Contains(e.Name.LocalName) ||
            (e.Name == "Text" && !string.IsNullOrEmpty(e.Value)) ||
            (e.Name == "DrawingContainer" && (string?)e.Attribute("class") != "FarPoint.Win.Spread.DrawingSpace.SpreadShapesContainer")) == true)
            throw Unsupported("存在图形或图表");
        if (settings.Descendants("FrozenColumnCount").Any(e => (int)e != 0)) throw Unsupported("存在冻结列，需人工核对插入位置");
        var items = axis.Element("Items") ?? throw Unsupported("缺少列设置");
        // A saved SQL no longer contains the pre-edit field list. Recover only from unique,
        // ordered exact header/name matches; never assume the first N fields are the old columns.
        if (reconciled)
        {
            int saved = 0;
            foreach (var field in newFields)
            {
                if (saved < oldFields.Count && field == oldFields[saved]) saved++;
                else if (oldFields.Contains(field, StringComparer.OrdinalIgnoreCase)) throw Unsupported("不支持删除、重命名或重排已保存 SQL 字段");
            }
            if (saved != oldFields.Count) throw Unsupported("不支持删除或重命名已保存 SQL 字段");
            var recovered = new List<string>(); int previous = -1;
            for (int column = 0; column < count; column++)
            {
                var header = cells.Elements("Cell").SingleOrDefault(c => Int(c, "row") == headerRow && Int(c, "column") == column)?.Element("Data")?.Value;
                var matches = oldFields.Select((field, index) => new { field, index }).Where(f => string.Equals(f.field, header, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (string.IsNullOrWhiteSpace(header) || matches.Length != 1 || matches[0].index <= previous)
                    throw Unsupported("SQL 已保存，但模板第 " + (column + 1) + " 列表头无法按原顺序唯一匹配字段；可能有自定义表头、无标题隐藏列或字段重排，未按列数猜测对应关系");
                previous = matches[0].index; recovered.Add(matches[0].field);
            }
            oldFields = recovered;
        }
        var columns = new List<LayoutColumn>(); int old = 0;
        for (int i = 0; i < newFields.Count; i++)
        {
            bool existing = old < oldFields.Count && newFields[i] == oldFields[old];
            if (!existing && oldFields.Contains(newFields[i], StringComparer.OrdinalIgnoreCase)) throw Unsupported("不支持删除、重命名或重排已有 SQL 字段");
            var item = existing ? items.Elements("Item").SingleOrDefault(e => Int(e, "index") == old) : null;
            var header = existing ? cells.Elements("Cell").SingleOrDefault(c => Int(c, "row") == headerRow && Int(c, "column") == old)?.Element("Data") : null;
            bool hidden = item?.Element("Visible")?.Value.Equals("False", StringComparison.OrdinalIgnoreCase) == true;
            if (existing && header == null && !hidden) throw Unsupported("可见列缺少明确的表头");
            columns.Add(new LayoutColumn { Index = i, OriginalIndex = existing ? old : -1, Field = newFields[i],
                Header = existing ? header?.Value ?? "" : newFields[i], Width = (int?)item?.Element("Size") ?? 120, Hidden = hidden });
            if (existing) old++;
        }
        if (old != oldFields.Count) throw Unsupported("不支持删除或重命名已有 SQL 字段");
        if (columns.All(c => c.OriginalIndex < 0 || c.Hidden)) throw Unsupported("没有可继承格式的可见列");
        return new LayoutPlan { Query = query, SourceIndex = sourceIndex, Sql = sql, Path = System.IO.Path.GetFullPath(layoutPath),
            Hash = ReportImporter.Hash(file.Bytes), File = file, HeaderRow = headerRow, DataRow = dataRow, OldCount = count, Columns = columns, Reconciled = reconciled };
    }

    private static byte[] PrepareLayout(LayoutPlan plan, IReadOnlyList<LayoutColumn> edits)
    {
        if (edits.Count != plan.Columns.Count) throw new InvalidOperationException("预览列数已变化，请重新识别。");
        for (int i = 0; i < edits.Count; i++)
        {
            if ((!plan.Columns[i].Hidden && string.IsNullOrWhiteSpace(edits[i].Header)) || edits[i].Header.Length > 128 || edits[i].Width < 24 || edits[i].Width > 1000)
                throw new InvalidOperationException("表头须为 1–128 个字符，列宽须为 24–1000。");
            XmlConvert.VerifyXmlChars(edits[i].Header);
        }
        var doc = new XDocument(plan.File.Document);
        var settings = Sheet(doc, "Settings"); var presentation = Sheet(doc, "Presentation"); var data = Sheet(doc, "Data");
        var map = plan.Columns.Where(c => c.OriginalIndex >= 0).ToDictionary(c => c.OriginalIndex, c => c.Index);
        int Map(int index) => index < 0 ? index : map.TryGetValue(index, out var next) ? next : throw Unsupported("模板坐标超出原字段范围");
        var cells = data.Element("DataArea")!.Element("Cells")!;
        var styles = presentation.Element("StyleModels");
        var originals = new XDocument(doc);
        // Shift all table cells and styles by SQL ordinal, preserving data binding at column zero and title at zero.
        foreach (var cell in cells.Elements("Cell"))
            if (cell.Element("Tag") == null && Int(cell, "row") >= plan.HeaderRow) cell.SetAttributeValue("column", Map(Int(cell, "column")));
        foreach (var model in styles?.Elements().Where(e => e.Name == "DataArea" || e.Name == "ColumnHeader") ?? Enumerable.Empty<XElement>())
        {
            model.SetAttributeValue("Columns", edits.Count);
            foreach (var e in model.Descendants().Where(e => e.Attribute("Column") != null))
                if (model.Name == "ColumnHeader" || e.Attribute("Row") == null || Int(e, "Row") >= plan.HeaderRow) e.SetAttributeValue("Column", Map(Int(e, "Column")));
            foreach (var e in model.Element("ColumnStyles")?.Elements() ?? Enumerable.Empty<XElement>())
                if (e.Attribute("Index") != null) e.SetAttributeValue("Index", Map(Int(e, "Index")));
        }
        var axis = presentation.Element("AxisModels")!.Element("Column")!;
        var items = axis.Element("Items")!;
        foreach (var item in items.Elements("Item")) item.SetAttributeValue("index", Map(Int(item, "index")));
        axis.SetAttributeValue("count", edits.Count);
        settings.Element("Categories")!.Element("Layout")!.Element("ColumnCount")!.Value = edits.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var element in settings.Descendants().Where(e => new[] { "ActiveColumnIndex", "AnchorColumn", "FrozenColumnCount" }.Contains(e.Name.LocalName)))
            if (element.Name != "FrozenColumnCount") element.Value = Map((int)element).ToString(CultureInfo.InvariantCulture);
        foreach (var range in settings.Descendants("CellRange"))
        {
            int col = Int(range, "Column"), width = Int(range, "ColumnCount");
            if (col >= 0 && width > 0) { range.SetAttributeValue("Column", Map(col)); range.SetAttributeValue("ColumnCount", Map(col + width - 1) - Map(col) + 1); }
        }
        foreach (var left in presentation.Descendants("LeftColumn")) left.Value = Map((int)left).ToString(CultureInfo.InvariantCulture);
        foreach (var area in data.Elements().Where(e => e.Name == "DataArea" || e.Name == "ColumnHeader")) area.SetAttributeValue("columns", edits.Count);
        foreach (var span in presentation.Element("SpanModels")?.Descendants("CellRange") ?? Enumerable.Empty<XElement>()) span.SetAttributeValue("ColumnCount", edits.Count);
        for (int i = 0; i < edits.Count; i++)
        {
            var column = plan.Columns[i]; var edit = edits[i];
            var item = items.Elements("Item").SingleOrDefault(e => Int(e, "index") == i);
            if (item == null) { item = new XElement("Item", new XAttribute("index", i)); items.Add(item); }
            item.SetElementValue("Size", edit.Width);
            if (column.OriginalIndex < 0) item.SetElementValue("Visible", "True");
            var cell = cells.Elements("Cell").SingleOrDefault(c => Int(c, "row") == plan.HeaderRow && Int(c, "column") == i);
            if (cell == null) { cell = new XElement("Cell", new XAttribute("row", plan.HeaderRow), new XAttribute("column", i)); cells.Add(cell); }
            cell.SetElementValue("Data", edit.Header); cell.Element("Data")!.SetAttributeValue("type", "System.String");
            if (column.OriginalIndex >= 0) continue;
            var nearest = plan.Columns.Where(c => c.OriginalIndex >= 0 && !c.Hidden).OrderBy(c => Math.Abs(c.Index - i)).First();
            // Copy header/body appearance, not values, tags, formulas or type-specific numeric formatting.
            var originalStyles = Sheet(originals, "Presentation").Element("StyleModels")?.Element("DataArea");
            var dest = styles?.Element("DataArea");
            if (dest == null || originalStyles == null) continue;
            foreach (var style in originalStyles.Element("CellStyles")?.Elements("CellStyle").Where(s => Int(s, "Column") == nearest.OriginalIndex && Int(s, "Row") >= plan.HeaderRow) ?? Enumerable.Empty<XElement>())
            {
                var copy = new XElement(style); copy.SetAttributeValue("Column", i);
                copy.Elements().Where(e => e.Name == "CellType" || e.Name == "Formatter").Remove();
                if (dest.Element("CellStyles") == null) dest.Add(new XElement("CellStyles"));
                dest.Element("CellStyles")!.Add(copy);
            }
        }
        return Encode(plan.File, doc);
    }

    public static SqlXmlSaveResult Save(LayoutPlan plan, IReadOnlyList<LayoutColumn> edits, CancellationToken token = default)
    {
        var nextQuery = SqlXmlEditor.Prepare(plan.Query, plan.SourceIndex, plan.Sql, token);
        var nextLayout = PrepareLayout(plan, edits);
        if (ReportImporter.Hash(Read(plan.Path).Bytes) != plan.Hash) throw new InvalidOperationException("模板已被外部修改，请重新预览；未覆盖。");
        var paths = new[] { plan.Path, plan.Query.Path };
        var before = new[] { plan.File.Bytes, plan.Query.Bytes };
        var after = new[] { nextLayout, nextQuery.Bytes };
        var temps = paths.Select(p => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p)!, ".reportdesk-" + Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
        var changed = Enumerable.Range(0, 2).Where(i => !before[i].SequenceEqual(after[i])).ToArray();
        var committed = new List<int>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                SqlXmlEditor.CheckPath(paths[i]);
                if (changed.Contains(i))
                {
                    if ((File.GetAttributes(paths[i]) & FileAttributes.ReadOnly) != 0) throw new IOException("文件为只读。");
                    Stage(temps[i], after[i]);
                }
            }
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < 2; i++)
                if (!File.ReadAllBytes(paths[i]).SequenceEqual(before[i])) throw new InvalidOperationException("预览后文件已改变；请重新预览，未覆盖。");
            foreach (int i in changed)
            {
                // Do not honor cancellation between the two commit points. No filesystem-wide atomicity is claimed.
                if (!File.ReadAllBytes(paths[i]).SequenceEqual(before[i])) throw new IOException("保存时文件发生变化。");
                File.Replace(temps[i], paths[i], null, false); committed.Add(i);
            }
            for (int i = 0; i < 2; i++)
                if (!File.ReadAllBytes(paths[i]).SequenceEqual(after[i])) throw new IOException("保存后文件回读不一致。");
            return new SqlXmlSaveResult { Snapshot = SqlXmlEditor.Read(plan.Query.Path), Changed = !before[0].SequenceEqual(after[0]) || !before[1].SequenceEqual(after[1]) };
        }
        catch (Exception ex) when (committed.Count > 0)
        {
            bool restored = true;
            foreach (int i in committed.AsEnumerable().Reverse())
            {
                try
                {
                    // Never undo someone else's external changes. Rollback uses memory, not a .bak file.
                    if (!File.ReadAllBytes(paths[i]).SequenceEqual(after[i])) { restored = false; continue; }
                    Stage(temps[i], before[i]); File.Replace(temps[i], paths[i], null, false);
                    if (!File.ReadAllBytes(paths[i]).SequenceEqual(before[i])) restored = false;
                }
                catch { restored = false; }
            }
            ErrorLog.Write("LayoutPairSave", ex, includeMessage: false);
            throw new InvalidOperationException(restored ? "两文件保存未完成，已撤回本次已写入的内容。请重新预览后重试。" :
                "两文件保存未完成，且无法安全恢复全部内容。请停止使用此报表，人工核对查询 XML 与模板：" + string.Join("；", paths));
        }
        finally
        {
            foreach (var temp in temps) try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) { ErrorLog.Write("LayoutTemporaryCleanup", ex, includeMessage: false); }
        }
    }
    private static void Stage(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes, 0, bytes.Length); output.Flush(true);
    }
}
