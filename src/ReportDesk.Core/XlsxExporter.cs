using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace ReportDesk.Core;

public static class XlsxExporter
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Export(DataTable table, string destination, string context, CancellationToken cancellation = default)
    {
        if (table.Columns.Count > 16384 || table.Rows.Count > 1048575) throw new InvalidOperationException("结果超过 Excel 工作表容量。");
        var stage = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Export(table, file, context, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(stage, destination, null);
            else File.Move(stage, destination);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    /// <summary>Writes an XLSX without temporary files; the caller owns the writable stream.</summary>
    public static void Export(DataTable table, Stream destination, string context, CancellationToken cancellation = default)
    {
        if (table == null) throw new ArgumentNullException(nameof(table));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (!destination.CanWrite) throw new ArgumentException("导出流不可写。", nameof(destination));
        if (table.Columns.Count > 16384 || table.Rows.Count > 1048575) throw new InvalidOperationException("结果超过 Excel 工作表容量。");
        cancellation.ThrowIfCancellationRequested();
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Part(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Part(zip, "xl/workbook.xml", "<workbook xmlns=\"" + Ns + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"查询结果\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"导出说明\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            Part(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>");
            WriteSheet(zip, "xl/worksheets/sheet1.xml", table, cancellation);
            using var info = new DataTable(); info.Columns.Add("项目"); info.Columns.Add("说明");
            info.Rows.Add("查询来源", context); info.Rows.Add("导出时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            info.Rows.Add("导出范围", "当前表格筛选与排序后的已加载结果；不另行查询数据库。");
            info.Rows.Add("格式", "日期保存为 ISO 文本；超过 15 位有效数字的数值保存为文本，避免精度丢失。字符串始终写为文本。");
            WriteSheet(zip, "xl/worksheets/sheet2.xml", info, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
    }
    private static void Part(ZipArchive zip, string path, string xml)
    { using var stream = zip.CreateEntry(path).Open(); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(xml); }
    private static void WriteSheet(ZipArchive zip, string path, DataTable table, CancellationToken cancellation)
    {
        using var stream = zip.CreateEntry(path).Open();
        using var x = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });
        x.WriteStartDocument(); x.WriteStartElement("worksheet", Ns);
        x.WriteStartElement("sheetViews", Ns); x.WriteStartElement("sheetView", Ns); x.WriteAttributeString("workbookViewId", "0");
        x.WriteStartElement("pane", Ns); x.WriteAttributeString("ySplit", "1"); x.WriteAttributeString("topLeftCell", "A2"); x.WriteAttributeString("state", "frozen"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("sheetData", Ns);
        x.WriteStartElement("row", Ns); x.WriteAttributeString("r", "1");
        for (var c = 0; c < table.Columns.Count; c++) Cell(x, c, 1, table.Columns[c].ColumnName);
        x.WriteEndElement();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            cancellation.ThrowIfCancellationRequested(); x.WriteStartElement("row", Ns); x.WriteAttributeString("r", (r + 2).ToString(CultureInfo.InvariantCulture));
            for (var c = 0; c < table.Columns.Count; c++) Cell(x, c, r + 2, table.Rows[r][c]);
            x.WriteEndElement();
        }
        x.WriteEndElement();
        if (table.Columns.Count > 0) { x.WriteStartElement("autoFilter", Ns); x.WriteAttributeString("ref", "A1:" + Column(table.Columns.Count - 1) + (table.Rows.Count + 1)); x.WriteEndElement(); }
        x.WriteEndElement(); x.WriteEndDocument();
    }
    private static string Column(int zero)
    { var s = ""; for (var n = zero + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s; return s; }
    private static void Cell(XmlWriter x, int column, int row, object value)
    {
        x.WriteStartElement("c", Ns); x.WriteAttributeString("r", Column(column) + row);
        if (value == DBNull.Value) { x.WriteEndElement(); return; }
        var text = value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        var numeric = value is decimal || value is double || value is float || value is int || value is long || value is short;
        var significant = text.Replace("-", "").Replace(".", "").TrimStart('0').Length;
        if (numeric && significant <= 15 && text != "NaN" && !text.Contains("Infinity"))
        { x.WriteElementString("v", Ns, text); }
        else
        {
            if (text.Length > 32767) throw new InvalidOperationException("有单元格超过 Excel 的 32767 字符限制，导出未覆盖原文件。");
            XmlConvert.VerifyXmlChars(text);
            x.WriteAttributeString("t", "inlineStr"); x.WriteStartElement("is", Ns); x.WriteStartElement("t", Ns);
            x.WriteAttributeString("xml", "space", null, "preserve"); x.WriteString(text); x.WriteEndElement(); x.WriteEndElement();
        }
        x.WriteEndElement();
    }
}
