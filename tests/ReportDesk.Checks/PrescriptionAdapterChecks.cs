using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ReportDesk.Core;

internal static class PrescriptionAdapterChecks
{
    internal static void Run(string folder, Action<string, Action> check, string? sources)
    {
        check("unreviewed mappings never use the prescription adapter", () =>
        {
            var path = Path.Combine(folder, "unreviewed-prescription.xml");
            File.WriteAllText(path, "<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>dtMain</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType><AddMapRow>true</AddMapRow></QueryDataSource></QueryDataSource></ReportQueryInfo>");
            Assert(!OutpatientPrescriptionAdapter.Matches(ReportImporter.ImportFile(path)!.SourceHash, path) && ReportImporter.ImportFile(path)!.Issues.Count == 0);
        });
        if (sources == null) { Console.WriteLine("NOT RUN prescription original-file checks: pass the HIS reports directory as the second argument."); return; }
        const string queryName = "门诊处方患者明细查询设置.xml";
        var original = Path.Combine(sources, queryName);
        if (!File.Exists(original)) throw new Exception("Prescription verification source missing.");
        var queryBytes = File.ReadAllBytes(original);
        var mainBytes = File.ReadAllBytes(Path.Combine(sources, OutpatientPrescriptionAdapter.MainLayoutName));
        var detailBytes = File.ReadAllBytes(Path.Combine(sources, OutpatientPrescriptionAdapter.DetailLayoutName));
        string Fixture(string name, byte[]? main, byte[]? detail, byte[]? query = null)
        {
            var dir = Path.Combine(folder, "prescription-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, queryName); File.WriteAllBytes(file, query ?? queryBytes);
            if (main != null) File.WriteAllBytes(Path.Combine(dir, OutpatientPrescriptionAdapter.MainLayoutName), main);
            if (detail != null) File.WriteAllBytes(Path.Combine(dir, OutpatientPrescriptionAdapter.DetailLayoutName), detail);
            return file;
        }
        check("reviewed main template contains one source tag five headers and no formulas", () =>
        {
            var layout = XDocument.Load(Path.Combine(sources, OutpatientPrescriptionAdapter.MainLayoutName));
            Assert(!layout.Descendants().Any(e => e.Name.LocalName == "Formula"));
            Assert(layout.Descendants("Tag").Select(e => e.Value).SequenceEqual(new[] { "dtMain" }));
            Assert(layout.Descendants("ColumnHeader").Descendants("Data").Select(e => e.Value).SequenceEqual(new[] { "处方号", "唯一号", "患者姓名", "诊断", "科室名称" }));
        });
        check("reviewed XML combination enables table queries without changing source SQL", () =>
        {
            var path = Fixture("valid", mainBytes, detailBytes); var report = ReportImporter.ImportFile(path)!;
            var source = XDocument.Load(original); var sql = source.Root!.Element("QueryDataSource")!.Elements("QueryDataSource").Select(e => e.Element("Sql")!.Value);
            Assert(report.Issues.Count == 0 && report.Queries.Count == 2 && report.Queries.Select(q => q.Sql).SequenceEqual(sql));
            Assert(SqlTemplate.Compile(report.Queries[0].Sql).RequiredNames.OrderBy(x => x).SequenceEqual(new[] { "dtBeginTime", "dtEndTime", "dtYYPE" }.OrderBy(x => x)));
            Assert(SqlTemplate.Compile(report.Queries[1].Sql).RequiredNames.OrderBy(x => x).SequenceEqual(new[] { "唯一号", "处方号" }.OrderBy(x => x)));
            Assert(report.Parameters.Count == 5 && report.Parameters.Count(p => p.Implicit) == 2);
            Assert(OutpatientPrescriptionAdapter.NoticeFor(report).Contains("主明细分别执行") && !report.Verified);
            var detail = SqlTemplate.Compile(report.Queries[1].Sql, new Dictionary<string, string> { ["唯一号"] = "0000123", ["处方号"] = "RX'001" });
            Assert(detail.Values.Values.Contains("0000123") && detail.Values.Values.Contains("RX'001") && !detail.Sql.Contains("RX'001"));
            Assert(File.ReadAllBytes(path).SequenceEqual(queryBytes));
        });
        check("reimport clears the reviewed mapping block while preserving user metadata", () =>
        {
            var path = Fixture("reimport", mainBytes, detailBytes); var old = ReportImporter.ImportFile(path)!;
            old.Issues.Add("legacy mapping guard"); old.Favorite = true; old.Notes = "keep this note";
            var catalog = new Catalog(); catalog.Reports.Add(old); ReportImporter.Merge(catalog, ReportImporter.ImportWithRelated(path));
            var fresh = catalog.Reports.Single(); Assert(fresh.Issues.Count == 0 && fresh.Favorite && fresh.Notes == old.Notes && fresh.Id == old.Id);
            var store = new CatalogStore(Path.Combine(folder, "prescription-catalog")); store.Save(catalog);
            Assert(store.Load().Reports.Single().Issues.Count == 0 && OutpatientPrescriptionAdapter.NoticeFor(store.Load().Reports.Single()).Length > 0);
        });
        check("missing companions do not block raw SQL tables and cannot claim a reviewed layout", () =>
        {
            foreach (var path in new[] { Fixture("no-main", null, detailBytes), Fixture("no-detail", mainBytes, null) })
                Assert(!OutpatientPrescriptionAdapter.Matches(ReportImporter.ImportFile(path)!.SourceHash, path) && ReportImporter.ImportFile(path)!.Issues.Count == 0);
        });
        check("changed companion files cannot inherit the reviewed exception", () =>
        {
            foreach (var path in new[] { Fixture("changed-main", mainBytes.Concat(new byte[] { 10 }).ToArray(), detailBytes), Fixture("changed-detail", mainBytes, detailBytes.Concat(new byte[] { 10 }).ToArray()) })
                Assert(!OutpatientPrescriptionAdapter.Matches(ReportImporter.ImportFile(path)!.SourceHash, path) && ReportImporter.ImportFile(path)!.Issues.Count == 0);
        });
        check("a changed plain query does not inherit the exact-version notice", () =>
        {
            var xml = XDocument.Load(original); xml.Root!.Element("QueryDataSource")!.Elements("QueryDataSource").First().Element("Sql")!.Value += "\n-- changed definition";
            var changed = System.Text.Encoding.UTF8.GetBytes(xml.ToString());
            var r = ReportImporter.ImportFile(Fixture("changed-query", mainBytes, detailBytes, changed))!; Assert(r.Issues.Count == 0 && OutpatientPrescriptionAdapter.NoticeFor(r).Length == 0);
        });
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Prescription adapter assertion failed"); }
}
