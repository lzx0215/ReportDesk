using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ReportDesk.Core;

public static class ReportImporter
{
    public static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    public static ImportSummary ImportFolder(string directory, CancellationToken cancellation = default)
    {
        var result = new ImportSummary();
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var report = ImportFile(file);
                if (report == null) result.Skipped++; else result.Reports.Add(report);
            }
            catch (Exception ex) when (ex is XmlException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException)
            { ErrorLog.Write("ImportXml", ex); result.Errors.Add(Path.GetFileName(file) + "：" + ex.Message); }
        }
        return result;
    }

    public static ReportDefinition? ImportFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        // Do not parse proprietary layout bodies: only query definitions are in scope.
        reader.MoveToContent();
        if (reader.LocalName != "ReportQueryInfo") return null;
        var root = XDocument.Load(reader).Root;
        if (root?.Name.LocalName != "ReportQueryInfo") return null;
        var full = Path.GetFullPath(path);
        var report = new ReportDefinition
        {
            Id = Hash(Encoding.UTF8.GetBytes(full.ToUpperInvariant())), SourcePath = full,
            SourceHash = Hash(bytes), Name = Path.GetFileNameWithoutExtension(path).Replace("查询设置", "")
        };
        foreach (var node in root.Element("List")?.Elements("List") ?? Enumerable.Empty<XElement>())
        {
            var control = node.Element("ControlType");
            if (control == null) continue;
            var kind = ((string?)control.Attribute("Type") ?? "").Split(',')[0].Split('.').Last();
            var parameter = new ParameterDefinition
            {
                Name = Text(node, "Name"), Label = Text(node, "Text").TrimEnd('：', ':'), Kind = kind,
                Format = Text(control, "CustomFormat", "yyyy-MM-dd HH:mm:ss"),
                AddDays = Number(control, "AddDays"), AddMonths = Number(control, "AddMonths"),
                LookupSql = Text(control, "QueryDataSource") == "Sql" ? Text(control, "DataSourceTypeName") : "",
                Dictionary = Text(control, "QueryDataSource") == "Dictionary" ? Text(control, "DataSourceTypeName") : "",
                HasAll = Flag(control, "IsAddAll"), AllValue = Text(control.Element("AllValue"), "ID"),
                DefaultValue = Text(control, "DefaultDataSource"), IsLike = Flag(control, "IsLike"),
                LikeFormat = Text(control, "LikeStr", "%{0}%"), PadLeft = Flag(control, "IsPadLeft"),
                PadLength = Number(control, "Length"), PadCharacter = Text(control, "PadLeftName", "0")
            };
            if (string.IsNullOrWhiteSpace(parameter.Name) || report.Parameters.Any(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
            { report.Issues.Add("参数名为空或重复。"); continue; }
            if (!new[] { "DateTimeType", "TextBoxType", "ComboBoxType" }.Contains(kind))
                report.Issues.Add(parameter.Label + " 使用尚未适配的控件 " + kind);
            if (kind == "ComboBoxType" && parameter.LookupSql.Length == 0)
                report.Issues.Add(parameter.Label + " 的字典/自定义选项尚未适配。");
            if (control.Elements().Any(e => e.Name.LocalName.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) >= 0 && e.Value == "true"))
                report.Issues.Add(parameter.Label + " 的多选逻辑尚未适配。");
            if (control.Element("Enabled")?.Value == "false") report.Issues.Add(parameter.Label + " 是禁用控件，默认值语义待核对。");
            if (!string.IsNullOrWhiteSpace(parameter.DefaultValue)) report.Issues.Add(parameter.Label + " 的默认数据源语义待核对。");
            report.Parameters.Add(parameter);
        }
        foreach (var source in root.Element("QueryDataSource")?.Elements("QueryDataSource") ?? Enumerable.Empty<XElement>())
        {
            var sql = Text(source, "Sql"); if (string.IsNullOrWhiteSpace(sql)) continue;
            var query = new QueryDefinition { Name = Text(source, "Name"), Kind = Text(source, "SqlType"), Sql = sql };
            report.Queries.Add(query);
            if (Flag(source, "IsCross") || new[] { "AddMapRow", "AddMapColumn", "AddMapData", "AddMapSourceData" }.Any(f => Flag(source, f)))
                report.Issues.Add(query.Name + " 存在交叉或数据映射处理，待适配。");
            if (!new[] { "MainReportUsing", "DetailReportUsing" }.Contains(query.Kind))
                report.Issues.Add(query.Name + " 的数据源执行顺序/用途待适配：" + query.Kind);
            CheckSql(query.Sql, report, query.Name);
        }
        foreach (var param in report.Parameters.Where(p => p.LookupSql.Length > 0).ToArray()) CheckSql(param.LookupSql, report, param.Label + "选项");
        if (!string.IsNullOrWhiteSpace(Text(root.Element("TableGroup"), "GroupCondition")) || !string.IsNullOrWhiteSpace(Text(root.Element("TableGroup")?.Element("QueryDataSource"), "Sql")))
            report.Issues.Add("分组数据源待适配。");
        // Details are explicitly selected and parameterized in v1; never silently remove row filters.
        if (report.Queries.Count == 0) report.Issues.Add("没有查询 SQL。");
        report.Issues = report.Issues.Distinct().ToList();
        return report;
    }

    private static void CheckSql(string sql, ReportDefinition report, string source)
    {
        try
        {
            foreach (var name in SqlTemplate.Compile(sql).RequiredNames)
                if (!report.Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    report.Parameters.Add(new ParameterDefinition { Name = name, Label = name + "（上下文/明细参数，必填）", Implicit = true });
        }
        catch (InvalidOperationException ex) { report.Issues.Add(source + "：" + ex.Message); }
    }

    public static void Merge(Catalog catalog, ImportSummary imported)
    {
        foreach (var fresh in imported.Reports)
        {
            var old = catalog.Reports.FirstOrDefault(r => r.Id == fresh.Id);
            if (old != null)
            {
                fresh.Category = old.Category; fresh.Aliases = old.Aliases; fresh.Notes = old.Notes;
                fresh.Favorite = old.Favorite; fresh.LastUsed = old.LastUsed;
                fresh.Verified = old.SourceHash == fresh.SourceHash && old.Verified;
                catalog.Reports.Remove(old);
            }
            catalog.Reports.Add(fresh);
        }
    }

    private static string Text(XElement? node, string name, string fallback = "") => node?.Element(name)?.Value ?? fallback;
    private static bool Flag(XElement? node, string name) => bool.TryParse(Text(node, name), out var value) && value;
    private static int Number(XElement? node, string name) => int.TryParse(Text(node, name), out var number) ? number : 0;
}
