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

    public static ImportSummary ImportFolder(string directory, CancellationToken cancellation = default, Action<string>? progress = null)
    {
        var inventory = ReportFileDiscovery.Scan(directory, cancellation, progress);
        return ImportDiscovered(inventory.Queries, inventory, cancellation, progress);
    }

    public static ImportSummary ImportWithRelated(string file, CancellationToken cancellation = default, Action<string>? progress = null)
    {
        var inventory = ReportFileDiscovery.Scan(Path.GetDirectoryName(Path.GetFullPath(file))!, cancellation, progress);
        return ImportDiscovered(new[] { Path.GetFullPath(file) }, inventory, cancellation, progress, true);
    }

    private static ImportSummary ImportDiscovered(System.Collections.Generic.IEnumerable<string> files, ReportFileInventory inventory, CancellationToken cancellation, Action<string>? progress, bool single = false)
    {
        var result = new ImportSummary { Inventory = inventory, Skipped = single ? 0 : inventory.OtherXml + inventory.Layouts.Count };
        result.Errors.AddRange(inventory.Warnings);
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var report = ImportFile(file);
                if (report == null) { if (single) result.Skipped++; continue; }
                if (!ReportClassification.IsStandalone(report))
                { result.IncompleteReports.Add(report); result.Skipped++; continue; }
                result.Reports.Add(report);
                result.RelatedFiles[report.Id] = ReportFileDiscovery.Match(file, inventory, cancellation);
                if (result.Reports.Count % 50 == 0) progress?.Invoke("已读取 " + result.Reports.Count + " 份报表查询定义，正在匹配配套 XML…");
            }
            catch (Exception ex) when (ex is XmlException || ReportFileDiscovery.IsFileError(ex) || ex is InvalidOperationException || ex is ArgumentException)
            { ErrorLog.Write("ImportXml", ex, includeMessage: false); result.Errors.Add(Path.GetFileName(file) + "：读取/匹配失败（" + ex.GetType().Name + "）"); }
        }
        cancellation.ThrowIfCancellationRequested();
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
            // Unnamed, plain text decorations cannot be addressed by an &name template.
            if(string.IsNullOrWhiteSpace(Text(node,"Name")) && kind=="TextBoxType" && string.IsNullOrWhiteSpace(Text(control,"DataSourceTypeName"))) continue;
            var parameter = new ParameterDefinition
            {
                Name = Text(node, "Name"), Label = Text(node, "Text").TrimEnd('：', ':'), Kind = kind,
                TreeSelect = kind == "TreeViewType", Multiple = kind == "TreeViewType" && (Flag(control,"IsCheckBox") || Flag(control,"IsAddAll")),
                Format = Text(control, "CustomFormat", "yyyy-MM-dd HH:mm:ss"),
                AddDays = Number(control, "AddDays"), AddMonths = Number(control, "AddMonths"),
                LookupSql = Text(control, "QueryDataSource") == "Sql" ? Text(control, "DataSourceTypeName") : "",
                Dictionary = Text(control, "DataSourceTypeName"),
                OptionSource = Text(control, "QueryDataSource"),
                AllLabel = Text(control.Element("AllValue"), "Name", "全部"),
                HasAll = Flag(control, "IsAddAll"), AllValue = Text(control.Element("AllValue"), "ID"),
                DefaultValue = Text(control, "DefaultDataSource"), IsLike = Flag(control, "IsLike"),
                LikeFormat = Text(control, "LikeStr", "%{0}%"), PadLeft = Flag(control, "IsPadLeft"),
                PadLength = Number(control, "Length"), PadCharacter = Text(control, "PadLeftName", "0")
            };
            if(kind == "ComboBoxType" && parameter.OptionSource == "DataSource")
            {
                var lookupSource = root.Element("QueryDataSource")?.Elements("QueryDataSource").FirstOrDefault(s=>Text(s,"Name")==parameter.Dictionary && Text(s,"SqlType")=="ConditionUsing");
                if(lookupSource != null) { parameter.LookupSourceName=parameter.Dictionary; parameter.LookupSql=Text(lookupSource,"Sql"); }
            }
            if(kind=="CustomControl" && Text(control,"TypeName")=="FS.Finance.UI.InPatient.Base.ucInPatientNOForReport" && Text(control,"ValueProperty")=="RegisterID")
            {
                // Decompiled getter returns register.ID, not the visible hospital number.
                kind=parameter.Kind="RegisterIdType";
                parameter.Implicit=true;
                parameter.Label="住院流水号（RegisterID，非住院号）";
            }
            if (kind == "ComboBoxType" && parameter.OptionSource == "Custom")
            {
                var options = control.Element("DefaultDataSource")?.Elements().ToArray() ?? System.Array.Empty<XElement>();
                foreach (var option in options)
                {
                    if (option.Element("ID") == null || option.Element("Name") == null)
                        report.Issues.Add(parameter.Label + " 的自定义选项缺少 ID/Name。");
                    else parameter.Options.Add(new ParameterOption { Value = Text(option, "ID"), Label = Text(option, "Name") });
                }
                if (options.Length == 0 && !parameter.HasAll) report.Issues.Add(parameter.Label + " 的自定义选项为空。");
            }
            if (string.IsNullOrWhiteSpace(parameter.Name) || report.Parameters.Any(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
            { report.Issues.Add("参数名为空或重复。"); continue; }
            if (kind == "TreeViewType" && !((parameter.OptionSource == "Sql" || ParameterOptions.IsDictionary(parameter)) && !Flag(control,"IsShowEmployee")))
                report.Issues.Add(parameter.Label + (Flag(control, "IsCheckBox") ? " 的树形多选条件待适配（TreeViewType/IsCheckBox）；多个编码需逐项绑定。" : " 的树形条件待适配（TreeViewType）；需核对节点及全部选项的取值。") +
                    (Flag(control, "IsShowEmployee") ? " 还需加载科室下人员。" : ""));
            else if (!new[] { "DateTimeType", "TextBoxType", "ComboBoxType", "CheckBoxType", "TreeViewType", "RegisterIdType" }.Contains(kind))
                report.Issues.Add(parameter.Label + " 使用尚未适配的控件 " + kind);
            if (kind == "ComboBoxType" && parameter.LookupSql.Length == 0 && parameter.OptionSource != "Custom" && !ParameterOptions.IsDictionary(parameter))
                report.Issues.Add(parameter.Label + " 的字典/自定义选项尚未适配。");
            if (control.Elements().Any(e => e.Name.LocalName.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) >= 0 && bool.TryParse(e.Value, out var multiple) && multiple))
                report.Issues.Add(parameter.Label + " 的多选逻辑尚未适配。");
            if (bool.TryParse(control.Element("Enabled")?.Value, out var enabled) && !enabled &&
                (kind!="DateTimeType" || System.Text.RegularExpressions.Regex.IsMatch(root.ToString(), @"&(?:amp;)?"+System.Text.RegularExpressions.Regex.Escape(parameter.Name)+@"\b")))
                report.Issues.Add(parameter.Label + " 是禁用控件，默认值语义待核对。");
            if (kind == "CheckBoxType" && parameter.DefaultValue.Length > 0 && !bool.TryParse(parameter.DefaultValue, out _))
                report.Issues.Add(parameter.Label + " 的默认数据源不是有效的布尔值。");
            // ucCommonWindow.queryDataByControlType ignores DefaultDataSource for
            // SQL and supported dictionary lookups. It is a Custom option list,
            // not a selected default; stale exported entries must not block SQL.
            var unusedComboDefaults = (kind == "ComboBoxType" || kind == "TreeViewType") &&
                (parameter.OptionSource == "Sql" || ParameterOptions.IsDictionary(parameter) || parameter.LookupSourceName.Length>0);
            if (unusedComboDefaults) parameter.DefaultValue = "";
            // QueryConst returns List<Const>; ucCommonWindow assigns it with 'as string'
            // for TextBoxType. No scalar initialization comes from this empty dictionary.
            if(kind=="TextBoxType" && parameter.OptionSource=="Dictionary" && parameter.Dictionary.Length==0) parameter.DefaultValue="";
            if (!string.IsNullOrWhiteSpace(parameter.DefaultValue) && kind != "CheckBoxType" &&
                !(parameter.OptionSource == "Custom" && (kind == "ComboBoxType" || kind == "TextBoxType")))
                report.Issues.Add(parameter.Label + " 的默认数据源语义待核对。");
            if ((kind == "TextBoxType" || kind == "DateTimeType") &&
                parameter.OptionSource != "Custom" && !string.IsNullOrWhiteSpace(Text(control, "DataSourceTypeName")))
                report.Issues.Add(parameter.Label + " 的默认数据源/上下文初始化待适配。");
            report.Parameters.Add(parameter);
        }
        report.SharedIssues.AddRange(report.Issues);
        foreach (var source in root.Element("QueryDataSource")?.Elements("QueryDataSource") ?? Enumerable.Empty<XElement>())
        {
            var sql = Text(source, "Sql"); if (string.IsNullOrWhiteSpace(sql)) continue;
            var normalized=SqlPunctuation.Normalize(sql,out var punctuationCount);
            if(punctuationCount>0)
            {
                try
                {
                    // Do not repair unsupported Q/N strings, dynamic identifiers or other
                    // unsupported templates. Only accept the normalized copy if it compiles.
                    SqlTemplate.Compile(normalized,null,MultipleNames(report));
                    sql=normalized;
                    report.AdaptationNote += Text(source,"Name")+"：导入副本修正 "+punctuationCount+" 个 SQL 全角语法括号；原 XML、字符串和注释保持原样。\n";
                }
                catch(InvalidOperationException) { }
            }
            // Exact exported revision only. Correct one punctuation typo in the imported
            // copy; never rewrite source XML, literals, filters, or unknown revisions.
            if (report.SourceHash == "8b6b85cf2f899fccc5002a41fd33fae560f84256c9d0fe356867dcff29c2356c" && sql.Contains("wm_concat（类型)"))
            {
                sql = sql.Replace("wm_concat（类型)", "wm_concat(类型)");
                report.AdaptationNote = "已修正导入副本：wm_concat（类型) → wm_concat(类型)。原 XML 未改动；仍需在内网验证函数及查询结果。";
            }
            var query = new QueryDefinition { Name = Text(source, "Name"), Kind = Text(source, "SqlType"), Sql = sql,
                IsSumRow = bool.TryParse(Text(source, "IsSumRow", "true"), out var sumRow) && sumRow, SumColumns = Text(source, "SumColumns") };
            report.Queries.Add(query);
            query.ResultRulesXml = new XElement("QueryDataSource", source.Elements().Where(e => new[] {
                "IsCross", "CrossRows", "CrossColumns", "CrossValues", "CrossCombinColumns", "CrossGroupColumns", "SumRows", "SumColumns", "IsSumRow", "RowGroup"
            }.Contains(e.Name.LocalName))).ToString(SaveOptions.DisableFormatting);
            foreach (var issue in HisResultRules.Validate(source))
                report.Issues.Add(query.Name + "：" + issue);
            if (TabularReportAdapter.HasMapReference(sql) && !ControlReferencesOnly(sql,report)) report.Issues.Add(query.Name + " 使用数据源/控件属性映射，执行依赖待适配。");
            if (!new[] { "MainReportUsing", "DetailReportUsing", "ConditionUsing" }.Contains(query.Kind))
                report.Issues.Add(query.Name + " 的数据源执行顺序/用途待适配：" + query.Kind);
            // Transformation/dependency semantics can affect every source. Keep them
            // shared; only a standalone SQL syntax failure is isolated to its source.
            report.SharedIssues.AddRange(report.Issues.Except(report.SharedIssues));
            var priorIssues = report.Issues.ToList();
            CheckSql(query.Sql, report, query.Name);
            query.Issues.AddRange(report.Issues.Except(priorIssues));
            report.Issues = priorIssues;
        }
        foreach (var param in report.Parameters.Where(p => p.LookupSql.Length > 0).ToArray()) CheckSql(param.LookupSql, report, param.Label + "选项");
        if(report.Parameters.Any(p=>p.LookupSourceName.Length>0))
            report.Issues.AddRange(report.Queries.Where(q=>q.Kind=="ConditionUsing").SelectMany(q=>q.Issues));
        if (!string.IsNullOrWhiteSpace(Text(root.Element("TableGroup"), "GroupCondition")) || !string.IsNullOrWhiteSpace(Text(root.Element("TableGroup")?.Element("QueryDataSource"), "Sql")))
            report.Issues.Add("分组数据源待适配。");
        // Details are explicitly selected and parameterized in v1; never silently remove row filters.
        if (report.Queries.Count == 0) report.Issues.Add("没有查询 SQL。");
        report.SharedIssues = report.Issues.Distinct().ToList();
        report.ScopedValidation = true;
        report.Issues.AddRange(report.Queries.SelectMany(q => q.Issues));
        report.Issues = report.Issues.Distinct().ToList();
        return report;
    }

    private static void CheckSql(string sql, ReportDefinition report, string source)
    {
        try
        {
            foreach (var name in SqlTemplate.Compile(sql,null,MultipleNames(report)).RequiredNames)
                if (!report.Parameters.Any(p => p.Name.Equals(ParameterName(name), StringComparison.OrdinalIgnoreCase)))
                    report.Parameters.Add(new ParameterDefinition { Name = name, Label = name + "（上下文/明细参数，必填）", Implicit = true });
        }
        catch (InvalidOperationException ex) { report.Issues.Add(source + "：" + ex.Message); }
    }

    public static string ParameterName(string name) => name.EndsWith(".Value",StringComparison.Ordinal) || name.EndsWith(".Text",StringComparison.Ordinal) ? name.Substring(0,name.LastIndexOf('.')) : name;
    public static System.Collections.Generic.Dictionary<string,string[]> MultipleNames(ReportDefinition report) => report.Parameters.Where(p=>p.Multiple).SelectMany(p=>new[]{p.Name,p.Name+".Value"}).ToDictionary(n=>n,n=>System.Array.Empty<string>(),StringComparer.OrdinalIgnoreCase);
    private static bool ControlReferencesOnly(string sql, ReportDefinition report)
    {
        try { return SqlTemplate.Compile(sql).RequiredNames.Where(n=>n.Contains(".")).All(n=>report.Parameters.Any(p=>p.Name.Equals(ParameterName(n),StringComparison.OrdinalIgnoreCase))); }
        catch(InvalidOperationException) { return false; }
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
                fresh.Verified = old.SourceHash == fresh.SourceHash && old.Verified &&
                    old.Queries.Select(q => q.Sql).SequenceEqual(fresh.Queries.Select(q => q.Sql));
                catalog.Reports.Remove(old);
            }
            catalog.Reports.Add(fresh);
        }
    }

    public static ImportSummary Recheck(System.Collections.Generic.IEnumerable<ReportDefinition> reports,
        CancellationToken cancellation = default, Action<string>? progress = null)
    {
        var result = new ImportSummary();
        foreach (var previous in reports.Where(r => !r.IsDemo))
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke("正在重新检查：" + previous.Name);
            try
            {
                var fresh = ImportFile(previous.SourcePath);
                if (fresh == null) throw new InvalidOperationException("原文件已不是报表定义。");
                result.Reports.Add(fresh);
                if (!ReportClassification.IsStandalone(fresh)) result.IncompleteReports.Add(fresh);
            }
            catch (Exception ex) when (ex is XmlException || ReportFileDiscovery.IsFileError(ex) || ex is InvalidOperationException || ex is ArgumentException)
            {
                result.Errors.Add(previous.Name + "：原 XML 不可读取或不是有效查询定义，保留原条目；请检查来源路径或重新导入。（" + ex.GetType().Name + "）");
                ErrorLog.Write("RecheckXml", ex, includeMessage: false);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private static string Text(XElement? node, string name, string fallback = "") => node?.Element(name)?.Value ?? fallback;
    private static bool Flag(XElement? node, string name) => bool.TryParse(Text(node, name), out var value) && value;
    private static int Number(XElement? node, string name) => int.TryParse(Text(node, name), out var number) ? number : 0;
}
