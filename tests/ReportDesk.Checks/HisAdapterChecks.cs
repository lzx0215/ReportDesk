using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using ReportDesk.Core;

internal static class HisAdapterChecks
{
    public static void Run(string folder, Action<string, Action> check)
    {
        check("SQL diagnostics isolate punctuation causes and preserve quoted contents", () => {
            foreach (var sample in new[] { ("select 1; select 2 from dual", "多条 SQL"), ("select :x from dual", "原生绑定变量"), ("select * from t@db", "数据库链接"), ("select\n f（x) from dual", "第 2 行，第 3 列") })
            {
                try { SqlTemplate.Compile(sample.Item1); throw new Exception("Expected rejection"); }
                catch (InvalidOperationException ex) { Assert(ex.Message.Contains(sample.Item2)); }
            }
            Assert(SqlTemplate.Compile("select '（;:@）' as \"（备注）\" from dual -- （").Sql.Contains("'（;:@）'"));
        });
        check("independent SQL errors isolate sources while shared dependencies block all", () => {
            var file = Path.Combine(folder, "scoped.xml");
            var xml = "<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource><QueryDataSource><Name>detail</Name><Sql>select :x from dual</Sql><SqlType>DetailReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>";
            File.WriteAllText(file, xml); var r = ReportImporter.ImportFile(file)!;
            Assert(r.Issues.Count == 1 && ReportReadiness.IssuesFor(r, r.Queries[0]).Count == 0 && ReportReadiness.IssuesFor(r, r.Queries[1]).Count == 1);
            Assert(r.Status.Contains("部分可试查"));
            var store = new CatalogStore(Path.Combine(folder, "scoped-catalog")); var catalog = new Catalog(); catalog.Reports.Add(r); store.Save(catalog);
            var restored = store.Load().Reports.Single(); Assert(ReportReadiness.IssuesFor(restored, restored.Queries[0]).Count == 0);
            r.ScopedValidation = false; Assert(ReportReadiness.IssuesFor(r, r.Queries[0]).Count == 1);
            File.WriteAllText(file, xml.Replace("<Name>detail</Name>", "<Name>detail</Name><IsCross>true</IsCross>"));
            r = ReportImporter.ImportFile(file)!; Assert(ReportReadiness.IssuesFor(r, r.Queries[0]).Count > 0);
        });
        check("recheck preserves metadata reports unavailable files and cancels before merge", () => {
            var file = Path.Combine(folder, "recheck.xml");
            File.WriteAllText(file, "<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>");
            var r = ReportImporter.ImportFile(file)!; r.Favorite = true; r.Notes = "keep"; r.Verified = true;
            var catalog = new Catalog(); catalog.Reports.Add(r); catalog.Reports.Add(new ReportDefinition { SourcePath = Path.Combine(folder, "missing.xml"), Name = "missing" });
            var recheck = ReportImporter.Recheck(catalog.Reports); Assert(recheck.Reports.Count == 1 && recheck.Errors.Count == 1);
            ReportImporter.Merge(catalog, recheck); var updated = catalog.Reports.Single(x => x.Id == r.Id);
            Assert(updated.Favorite && updated.Notes == "keep" && updated.Verified && catalog.Reports.Count == 2);
            try { ReportImporter.Recheck(catalog.Reports, new System.Threading.CancellationToken(true)); throw new Exception("Expected cancel"); } catch (OperationCanceledException) { }
        });
        ReportDefinition Import(string controls = "", string flags = "", string sql = "select 1 from dual")
        {
            var file = Path.Combine(folder, "adapter-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, "<ReportQueryInfo><List>" + controls + "</List><QueryDataSource><QueryDataSource><Name>dtMain</Name><Sql>" + System.Security.SecurityElement.Escape(sql) + "</Sql><SqlType>MainReportUsing</SqlType>" + flags + "</QueryDataSource></QueryDataSource></ReportQueryInfo>");
            return ReportImporter.ImportFile(file)!;
        }
        string Control(string name, string kind, string body) => "<List><Name>" + name + "</Name><Text>" + name + "</Text><ControlType Type=\"FS.Core.UI.Report.Common.ControlType." + kind + ",FS.Core.UI\">" + body + "</ControlType></List>";
        check("plain AddMap output is queryable while data transformations remain blocked", () => {
            var flags = "<AddMapData>true</AddMapData><AddMapRow>true</AddMapRow><AddMapColumn>true</AddMapColumn><AddMapSourceData>true</AddMapSourceData>";
            Assert(Import(flags: flags).Issues.Count == 0);
            foreach (var transform in new[] { "<IsCross>True</IsCross>", "<SumRows>合计:金额</SumRows>", "<CrossCombinColumns>组:列</CrossCombinColumns>", "<RowGroup><Name>x</Name></RowGroup>" })
                Assert(Import(flags: transform).Issues.Count > 0);
            Assert(Import(sql: "select '&dtMain.Rows[0][ID]' from dual").Issues.Count > 0);
        });
        check("configured totals preserve decimal precision nulls and nonnumeric identities", () => {
            var table = new DataTable(); table.Columns.Add("名称"); table.Columns.Add("金额", typeof(decimal)); table.Columns.Add("空值", typeof(decimal)); table.Columns.Add("编码", typeof(int));
            table.Rows.Add("甲", 0.1m, DBNull.Value, 1); table.Rows.Add("乙", 0.2m, DBNull.Value, 2);
            var definition = Import(flags:"<SumColumns>名称</SumColumns><IsSumRow>true</IsSumRow>"); Assert(definition.Issues.Count == 0);
            TabularReportAdapter.Complete(table, definition.Queries.Single());
            Assert(table.Rows.Count == 3 && (string)table.Rows[2][0] == "合计：" && (decimal)table.Rows[2][1] == 0.3m && table.Rows[2].IsNull(2) && table.Rows[2].IsNull(3));
            var empty = table.Clone(); TabularReportAdapter.Complete(empty, definition.Queries.Single()); Assert(empty.Rows.Count == 1 && empty.Rows[0].IsNull(1));
            var disabled = table.Clone(); TabularReportAdapter.Complete(disabled, new QueryDefinition { SumColumns="名称", IsSumRow=false }); Assert(disabled.Rows.Count == 0);
            TabularReportAdapter.Complete(disabled, new QueryDefinition { SumColumns="不存在", IsSumRow=true }); Assert(disabled.Rows.Count == 0);
            var cancelled = table.Clone(); var cancellation = new System.Threading.CancellationToken(true);
            try { TabularReportAdapter.Complete(cancelled, definition.Queries.Single(), cancellation); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Assert(cancelled.Rows.Count == 0); }
        });
        check("custom choices preserve leading zero IDs and custom All label", () => {
            var r = Import(Control("kind", "ComboBoxType", "<QueryDataSource>Custom</QueryDataSource><IsAddAll>true</IsAddAll><AllValue><ID>1</ID><Name>入库</Name></AllValue><DefaultDataSource><DefaultDataSource><ID>002</ID><Name>出库</Name></DefaultDataSource></DefaultDataSource>"), sql: "select '&kind' from dual");
            var p = r.Parameters.Single(); Assert(r.Issues.Count == 0 && p.AllLabel == "入库" && p.Options.Single().Value == "002");
            Assert(ParameterOptions.Validate(p, "002") == "002"); Reject(() => ParameterOptions.Validate(p, "出库"));
            var store = new CatalogStore(Path.Combine(folder, "his-options")); var c = new Catalog(); c.Reports.Add(r); store.Save(c);
            Assert(store.Load().Reports.Single().Parameters.Single().Options.Single().Value == "002");
        });
        check("empty custom choices and disabled controls are not silently enabled", () => {
            Assert(Import(Control("x", "ComboBoxType", "<QueryDataSource>Custom</QueryDataSource><DefaultDataSource />")).Issues.Count > 0);
            Assert(Import(Control("x", "TextBoxType", "<QueryDataSource>Custom</QueryDataSource><DefaultDataSource>65</DefaultDataSource><Enabled>false</Enabled>")).Issues.Count > 0);
        });
        check("SQL and dictionary combos ignore inactive Custom defaults as the HIS engine does", () => {
            var stale = "<DefaultDataSource><DefaultDataSource><ID>1</ID><Name>已结</Name></DefaultDataSource></DefaultDataSource>";
            var r = Import(Control("settled", "ComboBoxType", "<QueryDataSource>Sql</QueryDataSource><DataSourceTypeName>select '1','已结' from dual union select '2','未结' from dual</DataSourceTypeName>" + stale), sql: "select '&settled' from dual");
            Assert(r.Issues.Count == 0 && r.Parameters.Single().DefaultValue == "" && r.Parameters.Single().Options.Count == 0);
            Assert(r.Parameters.Single().LookupSql.Contains("未结"));
            var dictionary = Import(Control("dept", "ComboBoxType", "<QueryDataSource>DepartmentType</QueryDataSource><DataSourceTypeName>I</DataSourceTypeName>" + stale));
            Assert(dictionary.Issues.Count == 0);
            Assert(Import(Control("x", "ComboBoxType", "<QueryDataSource>Unknown</QueryDataSource>" + stale)).Issues.Count > 0);
            Assert(Import(Control("x", "TextBoxType", "<QueryDataSource>Sql</QueryDataSource><DefaultDataSource>1</DefaultDataSource>")).Issues.Count > 0);
        });
        check("adaptation diagnostics identify the actual transformation and tree rule", () => {
            var r = Import(flags: "<RowGroup><Name>x</Name></RowGroup><SumRows>合计:金额</SumRows><AddMapData>true</AddMapData>");
            Assert(r.Issues.Count == 2 && r.Issues.Any(x => x.Contains("RowGroup")) && r.Issues.All(x => !x.Contains("AddMapData")));
            Assert(r.Issues.Select(AdaptationGuidance.For).Select(x => x.Code).Distinct().Count() == 2);
            var tree = Import(Control("tree", "TreeViewType", "<IsCheckBox>true</IsCheckBox>"));
            Assert(tree.Issues.Any(x => AdaptationGuidance.For(x).Code == "tree-multi"));
        });
        check("checkbox and fixed text defaults follow the engine and values remain bound", () => {
            var r = Import(Control("yes", "CheckBoxType", "<DefaultDataSource>false</DefaultDataSource>") + Control("text", "TextBoxType", "<QueryDataSource>Custom</QueryDataSource><DefaultDataSource>0065</DefaultDataSource>"), sql: "select '&yes','&text' from dual");
            Assert(r.Issues.Count == 0 && ParameterOptions.Initial(r.Parameters[0]) == "False" && ParameterOptions.Initial(r.Parameters[1]) == "0065");
            Reject(() => ParameterOptions.Validate(r.Parameters[0], "0"));
        });
        check("dictionary lookups preserve actual DAL filters and bind exported type", () => {
            foreach (var source in new[] { "Dictionary", "DepartmentType", "EmployeeType" }) {
                var type = source == "Dictionary" ? "TYPE'VALUE" : source == "DepartmentType" ? "I" : "D";
                var p = new ParameterDefinition { OptionSource = source, Dictionary = type };
                var b = SqlTemplate.Compile(ParameterOptions.LookupSql(p), ParameterOptions.LookupValues(p));
                Assert(b.Values.Single().Value == type && !b.Sql.Contains("TYPE'VALUE"));
                Assert(source != "DepartmentType" || b.Sql.Contains("BUSINESS_FLAG = '1'"));
                Assert(source != "Dictionary" || !b.Sql.Contains("VALID_FLAG"));
            }
            var all = new ParameterDefinition { OptionSource = "DepartmentType", Dictionary = "ALL" };
            Assert(!ParameterOptions.LookupSql(all).Contains("BUSINESS_FLAG") && SqlTemplate.Compile(ParameterOptions.LookupSql(all)).RequiredNames.Count == 0);
            Assert(!ParameterOptions.IsDictionary(new ParameterDefinition { OptionSource = "EmployeeType", Dictionary = "UNKNOWN" }));
        });
        check("LISTAGG overflow clause works but TRUNCATE statements and map expressions are denied", () => {
            var b = SqlTemplate.Compile("select listagg(name, ',' ON OVERFLOW TRUNCATE '...' WITH COUNT) within group(order by name) from t where id='&id'", new Dictionary<string,string> { ["id"] = "x' OR 1=1" });
            Assert(b.Sql.Contains("ON OVERFLOW TRUNCATE") && !b.Sql.Contains("x' OR"));
            foreach (var sql in new[] { "truncate table t", "select count(x ON OVERFLOW TRUNCATE) from t", "select listagg(x, ',') ON OVERFLOW TRUNCATE from t", "select '&x.Value' from dual", "select &x.Rows[0] from dual" }) Reject(() => SqlTemplate.Compile(sql));
        });
        check("menu-derived categories preserve manual categories and candidate evidence", () => {
            var map = ReportLocations.Load(Path.Combine(folder, "absent-locations.xml"));
            var r = new ReportDefinition { SourcePath = "普通门诊处方记录查询设置.xml" };
            Assert(map.CategoryFor(r) == "药剂科"); r.Category = "自定义"; Assert(map.CategoryFor(r) == "自定义");
        });
    }
    private static void Assert(bool v) { if (!v) throw new Exception("HIS adapter assertion failed"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
}
