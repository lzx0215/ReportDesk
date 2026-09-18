using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using ReportDesk.Core;

internal static class LayoutEditingChecks
{
    internal static int Run(string root)
    {
        int failures = 0;
        void Check(string title, Action action) { try { action(); Console.WriteLine("PASS layout: " + title); } catch (Exception ex) { failures++; Console.WriteLine("FAIL layout: " + title + "\n" + ex); } }
        void Assert(bool ok) { if (!ok) throw new Exception("Layout assertion failed"); }
        void Throws(Action action) { bool caught = false; try { action(); } catch { caught = true; } Assert(caught); }
        (string query, string layout) Create(Encoding? encoding = null)
        {
            var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var query = Path.Combine(dir, "样例查询设置.xml"); var layout = Path.Combine(dir, "样例报表设置.xml");
            File.WriteAllText(query, "<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select 1 A, 2 B, 3 ID from dual</Sql><SqlType>MainReportUsing</SqlType><IsCross>false</IsCross></QueryDataSource></QueryDataSource></ReportQueryInfo>");
            encoding ??= new UTF8Encoding(false, true);
            var xml = "<?xml version=\"1.0\" encoding=\"" + encoding.WebName + "\"?>" +
                "<Spread class='FarPoint.Win.Spread.FpSpread'><Settings><Sheets><Sheet index='0'><Categories><Layout><ColumnCount>3</ColumnCount><RowCount>3</RowCount><ActiveColumnIndex>1</ActiveColumnIndex></Layout></Categories>" +
                "<SelectionModel><CellRange Row='1' Column='1' RowCount='1' ColumnCount='1'/><AnchorColumn>1</AnchorColumn></SelectionModel></Sheet></Sheets></Settings>" +
                "<Presentation><Sheets><Sheet index='0'><AxisModels><Column count='3'><Items><Item index='0'><Size>100</Size></Item><Item index='1'><Size>120</Size></Item><Item index='2'><Size>30</Size><Visible>False</Visible></Item></Items></Column></AxisModels>" +
                "<SpanModels><DataArea><CellRange Row='0' Column='0' RowCount='1' ColumnCount='3'/></DataArea></SpanModels>" +
                "<StyleModels><DataArea Rows='3' Columns='3'><CellStyles><CellStyle Row='1' Column='0'><Font><Name>宋体</Name></Font></CellStyle><CellStyle Row='2' Column='0'><HorizontalAlignment>Center</HorizontalAlignment></CellStyle>" +
                "<CellStyle Row='1' Column='1'><Font><Name>宋体</Name></Font></CellStyle><CellStyle Row='2' Column='1'><HorizontalAlignment>Center</HorizontalAlignment></CellStyle></CellStyles></DataArea></StyleModels></Sheet></Sheets></Presentation>" +
                "<Data><Sheets><Sheet index='0'><DataArea rows='3' columns='3'><Cells><Cell row='0' column='0'><Data type='System.String'>标题</Data></Cell><Cell row='1' column='0'><Data type='System.String'>旧表头A</Data></Cell>" +
                "<Cell row='1' column='1'><Data type='System.String'>旧表头B</Data></Cell><Cell row='2' column='0'><Tag type='System.String'>main</Tag></Cell></Cells></DataArea><ColumnHeader rows='1' columns='3'/></Sheet></Sheets></Data><Drawing/></Spread>";
            File.WriteAllBytes(layout, encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray());
            return (query, layout);
        }
        LayoutPlan Plan((string query, string layout) pair, string[]? fields = null) => ReportLayoutEditor.Preview(SqlXmlEditor.Read(pair.query), 0,
            "select 1 A, 4 新增, 2 B, 3 ID from dual", pair.layout, new[] { "A", "B", "ID" }, fields ?? new[] { "A", "新增", "B", "ID" });
        (string query, string layout) SavedPair(string[] fields)
        {
            var pair = Create(); var doc = XDocument.Load(pair.layout); var cells = doc.Descendants("Cells").Single();
            foreach (var cell in cells.Elements("Cell").Where(c => (int)c.Attribute("row")! == 1))
                cell.Element("Data")!.Value = (int)cell.Attribute("column")! == 0 ? "A" : "B";
            cells.Add(new XElement("Cell", new XAttribute("row", 1), new XAttribute("column", 2), new XElement("Data", new XAttribute("type", "System.String"), "ID")));
            doc.Save(pair.layout);
            SqlXmlEditor.Save(SqlXmlEditor.Read(pair.query), 0, "select " + string.Join(", ", fields.Select(f => "1 AS \"" + f + "\"")) + " from dual");
            return pair;
        }
        Check("saved SQL reconciles prefix, middle and suffix insertions with no query rewrite", () => {
            foreach (var fields in new[] { new[] { "A", "B", "ID", "末列" }, new[] { "首列", "A", "中列", "B", "ID", "末列" } })
            {
                var pair = SavedPair(fields); var snap = SqlXmlEditor.Read(pair.query); var bytes = File.ReadAllBytes(pair.query); var stamp = File.GetLastWriteTimeUtc(pair.query);
                var plan = ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql, pair.layout, fields, fields);
                Assert(plan.Reconciled && plan.Columns.Count(c => c.OriginalIndex < 0) == fields.Length - 3);
                // An unchanged SQL file may be read-only/held without delete sharing; only the layout should be replaced.
                File.SetAttributes(pair.query, FileAttributes.ReadOnly);
                try { using var held = new FileStream(pair.query, FileMode.Open, FileAccess.Read, FileShare.Read); ReportLayoutEditor.Save(plan, plan.Columns); }
                finally { File.SetAttributes(pair.query, FileAttributes.Normal); }
                Assert(File.ReadAllBytes(pair.query).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(pair.query) == stamp);
                Assert(plan.Columns.Single(c => c.Field == "ID").Hidden);
                var reopened = ReportLayoutEditor.Preview(SqlXmlEditor.Read(pair.query), 0, snap.Sources[0].Sql, pair.layout, fields, fields);
                Assert(!reopened.Reconciled && reopened.Columns.All(c => c.OriginalIndex >= 0));
                Assert(Directory.GetFiles(Path.GetDirectoryName(pair.query)!).Length == 2);
            }
        });
        Check("saved SQL recovery can combine additional unsaved fields", () => {
            var fields = new[] { "A", "B", "ID", "已保存" }; var pair = SavedPair(fields); var snap = SqlXmlEditor.Read(pair.query);
            var plan = ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql + " -- draft", pair.layout, fields, new[] { "A", "草稿", "B", "ID", "已保存" });
            Assert(plan.Reconciled && plan.Columns.Count(c => c.OriginalIndex < 0) == 2);
        });
        Check("saved SQL recovery refuses unknown duplicate missing and reordered headers", () => {
            var fields = new[] { "A", "B", "ID", "新增" };
            foreach (var headers in new[] { new[] { "自定义", "B", "ID" }, new[] { "A", "A", "ID" }, new[] { "B", "A", "ID" }, new[] { "A", "B", "" } })
            {
                var pair = SavedPair(fields); var doc = XDocument.Load(pair.layout);
                foreach (var cell in doc.Descendants("Cell").Where(c => (int)c.Attribute("row")! == 1)) cell.Element("Data")!.Value = headers[(int)cell.Attribute("column")!];
                doc.Save(pair.layout); var snap = SqlXmlEditor.Read(pair.query); var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
                Throws(() => ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql, pair.layout, fields, fields));
                Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
            }
        });
        Check("saved SQL recovery does not silently remove or reorder saved extra fields", () => {
            var fields = new[] { "A", "已保存", "B", "ID" }; var pair = SavedPair(fields); var snap = SqlXmlEditor.Read(pair.query);
            foreach (var after in new[] { new[] { "A", "B", "ID" }, new[] { "A", "B", "ID", "已保存" }, new[] { "A", "改名", "B", "ID" } })
                Throws(() => ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql, pair.layout, fields, after));
        });
        Check("saved SQL recovery still rejects post-preview file changes", () => {
            foreach (bool queryChanged in new[] { true, false })
            {
                var fields = new[] { "A", "B", "ID", "新增" }; var pair = SavedPair(fields); var snap = SqlXmlEditor.Read(pair.query);
                var plan = ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql, pair.layout, fields, fields);
                File.AppendAllText(queryChanged ? pair.query : pair.layout, " "); var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
                Throws(() => ReportLayoutEditor.Save(plan, plan.Columns));
                Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
            }
        });
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), Encoding.GetEncoding(936) })
            Check("insert preserves hidden columns, binding, styles and encoding " + encoding.WebName + "/" + encoding.GetPreamble().Length, () => {
                var pair = Create(encoding); var plan = Plan(pair); var beforeQuery = File.ReadAllBytes(pair.query); var beforeLayout = File.ReadAllBytes(pair.layout);
                Assert(plan.Columns[1].OriginalIndex == -1 && plan.Columns[1].Header == "新增" && plan.Columns[3].Hidden);
                Assert(File.ReadAllBytes(pair.query).SequenceEqual(beforeQuery) && File.ReadAllBytes(pair.layout).SequenceEqual(beforeLayout));
                plan.Columns[1].Header = "新表头<&"; plan.Columns[1].Width = 155;
                var saved = ReportLayoutEditor.Save(plan, plan.Columns);
                var doc = XDocument.Load(pair.layout);
                Assert(saved.Snapshot.Sources[0].Sql.Contains("新增"));
                Assert(doc.Descendants("ColumnCount").First().Value == "4");
                Assert(doc.Descendants("CellRange").Last().Attribute("ColumnCount")!.Value == "4");
                var cells = doc.Descendants("Cells").First().Elements("Cell").ToArray();
                Assert(cells.Single(c => (string?)c.Attribute("row") == "1" && (string?)c.Attribute("column") == "1").Element("Data")!.Value == "新表头<&");
                Assert(cells.Single(c => (string?)c.Attribute("row") == "1" && (string?)c.Attribute("column") == "2").Element("Data")!.Value == "旧表头B");
                Assert(doc.Descendants("Tag").Single().Parent!.Attribute("column")!.Value == "0");
                Assert(doc.Descendants("Item").Single(e => (string?)e.Attribute("index") == "3").Element("Visible")!.Value == "False");
                Assert(doc.Descendants("CellStyle").Any(e => (string?)e.Attribute("Row") == "1" && (string?)e.Attribute("Column") == "1" && e.Element("Font") != null));
                Assert(File.ReadAllBytes(pair.layout).Take(encoding.GetPreamble().Length).SequenceEqual(encoding.GetPreamble()));
                Assert(doc.Declaration!.Encoding == encoding.WebName);
                Assert(Directory.GetFiles(Path.GetDirectoryName(pair.query)!).Length == 2);
            });
        Check("front and multiple insertions keep tag at column zero", () => {
            var pair = Create(); var plan = Plan(pair, new[] { "首列", "A", "中间", "B", "ID", "末列" });
            ReportLayoutEditor.Save(plan, plan.Columns); var doc = XDocument.Load(pair.layout);
            Assert(doc.Descendants("Tag").Single().Parent!.Attribute("column")!.Value == "0");
            Assert(doc.Descendants("Cell").Single(c => (string?)c.Attribute("row") == "1" && (string?)c.Attribute("column") == "0").Element("Data")!.Value == "首列");
        });
        Check("format-only changes keep original SQL bytes", () => {
            var pair = Create(); var snap = SqlXmlEditor.Read(pair.query); var before = File.ReadAllBytes(pair.query);
            var plan = ReportLayoutEditor.Preview(snap, 0, snap.Sources[0].Sql, pair.layout, new[] { "A", "B", "ID" }, new[] { "A", "B", "ID" });
            plan.Columns[0].Header = "改名"; plan.Columns[0].Width = 200; ReportLayoutEditor.Save(plan, plan.Columns);
            Assert(File.ReadAllBytes(pair.query).SequenceEqual(before));
        });
        Check("duplicates, removal, rename, reorder and mismatched count blocked", () => {
            foreach (var fields in new[] { new[] { "A", "A", "ID" }, new[] { "A", "ID" }, new[] { "A", "RENAMED", "ID" }, new[] { "B", "A", "ID" } })
            { var pair = Create(); Throws(() => Plan(pair, fields)); }
            var p = Create(); File.WriteAllText(p.layout, File.ReadAllText(p.layout).Replace("<ColumnCount>3", "<ColumnCount>4")); Throws(() => Plan(p));
        });
        Check("complex merge, formulas, missing tag and cross-table rejected", () => {
            foreach (var replacement in new[] { ("Row='0' Column='0' RowCount='1' ColumnCount='3'", "Row='1' Column='0' RowCount='1' ColumnCount='2'"),
                ("<Tag type='System.String'>main</Tag>", "<Formula>A1+B1</Formula>"), ("<Tag type='System.String'>main</Tag>", "<Tag type='System.String'>other</Tag>") })
            { var pair = Create(); File.WriteAllText(pair.layout, File.ReadAllText(pair.layout).Replace(replacement.Item1, replacement.Item2)); Throws(() => Plan(pair)); }
            var p = Create(); File.WriteAllText(p.query, File.ReadAllText(p.query).Replace("<IsCross>false", "<IsCross>true")); Throws(() => Plan(p));
        });
        Check("query and layout external edits are never overwritten", () => {
            foreach (bool query in new[] { true, false }) {
                var pair = Create(); var plan = Plan(pair); File.AppendAllText(query ? pair.query : pair.layout, " ");
                var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
                Throws(() => ReportLayoutEditor.Save(plan, plan.Columns)); Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
            }
        });
        Check("second replacement failure rolls back first without backup", () => {
            var pair = Create(); var plan = Plan(pair); var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
            using (var hold = new FileStream(pair.query, FileMode.Open, FileAccess.Read, FileShare.Read)) Throws(() => ReportLayoutEditor.Save(plan, plan.Columns));
            Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
            Assert(Directory.GetFiles(Path.GetDirectoryName(pair.query)!).Length == 2);
        });
        Check("cancel and bad settings make no writes", () => {
            var pair = Create(); var plan = Plan(pair); var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
            using var cts = new CancellationTokenSource(); cts.Cancel(); Throws(() => ReportLayoutEditor.Save(plan, plan.Columns, cts.Token));
            plan.Columns[1].Width = 0; Throws(() => ReportLayoutEditor.Save(plan, plan.Columns));
            plan.Columns[1].Width = 100; plan.Columns[1].Header = ""; Throws(() => ReportLayoutEditor.Save(plan, plan.Columns));
            Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
        });
        Check("unencodable header and DTD rejected", () => {
            var pair = Create(Encoding.GetEncoding(936)); var plan = Plan(pair); var q = File.ReadAllBytes(pair.query); var l = File.ReadAllBytes(pair.layout);
            plan.Columns[1].Header = "😀"; Throws(() => ReportLayoutEditor.Save(plan, plan.Columns));
            Assert(File.ReadAllBytes(pair.query).SequenceEqual(q) && File.ReadAllBytes(pair.layout).SequenceEqual(l));
            File.WriteAllText(pair.layout, "<!DOCTYPE Spread [<!ENTITY a SYSTEM 'file:///never'>]>" + File.ReadAllText(pair.layout).Substring(File.ReadAllText(pair.layout).IndexOf("<Spread", StringComparison.Ordinal)));
            Throws(() => Plan(pair));
        });
        Check("real observation-room XML copy accepts new column", () => {
            const string original = "E:/his/LIB/Config/Xml/(病案)观察室工作日志查询设置.xml";
            if (!File.Exists(original)) { Console.WriteLine("SKIP local fixture not installed"); return; }
            var originalLayout = original.Replace("查询设置", "报表设置"); var pair = Create();
            File.Copy(original, pair.query, true); File.Copy(originalLayout, pair.layout, true);
            var file = SqlXmlEditor.Read(pair.query); var fields = new[] { "日期", "科室编码", "科室", "观察床位费收费次数", "留观诊查费收费次数" };
            if (file.Sources[0].Sql.Contains("a.apply_id 申请id")) fields = fields.Concat(new[] { "申请ID" }).ToArray();
            var plan = ReportLayoutEditor.Preview(file, 0, file.Sources[0].Sql, pair.layout, fields, fields.Concat(new[] { "新增列" }).ToArray());
            ReportLayoutEditor.Save(plan, plan.Columns); Assert(XDocument.Load(pair.layout).Descendants("ColumnCount").First().Value == (fields.Length + 1).ToString());
        });
        Check("real consultation completion merged header fails closed", () => {
            const string original = "E:/his/LIB/Config/Xml/普通会诊及时完成率查询设置.xml";
            if (!File.Exists(original)) { Console.WriteLine("SKIP local fixture not installed"); return; }
            var layout = original.Replace("查询设置", "报表设置");
            var queryBytes = File.ReadAllBytes(original); var layoutBytes = File.ReadAllBytes(layout);
            var file = SqlXmlEditor.Read(original); var fields = Enumerable.Range(0, 7).Select(i => "C" + i).ToArray();
            bool rejected = false;
            try { ReportLayoutEditor.Preview(file, 0, file.Sources[0].Sql, layout, fields, fields.Concat(new[] { "新增列" }).ToArray()); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("合并"); }
            Assert(rejected && File.ReadAllBytes(original).SequenceEqual(queryBytes) && File.ReadAllBytes(layout).SequenceEqual(layoutBytes));
        });
        return failures;
    }
}
