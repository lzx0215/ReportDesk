using System;
using System.IO;
using System.Linq;
using ReportDesk.Core;

internal static class ReportCatalogChecks
{
    internal static void Run(string folder, Action<string, Action> check)
    {
        var dir = Path.Combine(folder, "catalog-kinds-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var full = "<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>dtMain</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType><AddMapData>true</AddMapData><IsCross>true</IsCross></QueryDataSource></QueryDataSource></ReportQueryInfo>";
        var report = Path.Combine(dir, "普通配置.xml"); File.WriteAllText(report, full);
        var empty = Path.Combine(dir, "空报表查询设置.xml"); File.WriteAllText(empty, "<ReportQueryInfo><QueryDataSource /></ReportQueryInfo>");
        File.WriteAllText(Path.Combine(dir, "伪报表查询设置.xml"), "<configuration><Sql>select 1 from dual</Sql></configuration>");
        check("content classification retains pending reports and separates incomplete definitions", () =>
        {
            var scan = ReportImporter.ImportFolder(dir);
            Assert(scan.Reports.Count == 1 && scan.Reports[0].Issues.Count > 0 && scan.IncompleteReports.Count == 1 && scan.Skipped == 2);
            Assert(scan.Inventory!.OtherFiles.Count == 1 && !ReportClassification.IsStandalone(ReportImporter.ImportFile(empty)!));
        });
        check("single-file import counts only the selected file as skipped", () =>
        { Assert(ReportImporter.ImportWithRelated(report).Skipped == 0 && ReportImporter.ImportWithRelated(empty).Skipped == 1); });
        check("restoring a complete definition keeps legacy metadata and original identity", () =>
        {
            var old = ReportImporter.ImportFile(empty)!; old.Notes = "keep"; old.Favorite = true;
            var catalog = new Catalog(); catalog.Reports.Add(old); File.WriteAllText(empty, full);
            ReportImporter.Merge(catalog, ReportImporter.ImportWithRelated(empty));
            Assert(catalog.Reports.Single().Id == old.Id && catalog.Reports[0].Notes == "keep" && catalog.Reports[0].Favorite && ReportClassification.IsStandalone(catalog.Reports[0]));
        });
        var cfg = Path.Combine(dir, ReportLocations.FileName); var r = ReportImporter.ImportFile(report)!;
        string L(string path) => "<Location evidence=\"现场菜单记录\"><Segment>报表中心</Segment><Segment>" + path + "</Segment></Location>";
        check("multiple menu locations combine across exact identities without overwriting", () =>
        {
            File.WriteAllText(cfg, "<ReportLocations version=\"1\"><Report hash=\"" + r.SourceHash + "\">" + L("药剂科") + L("医务科") + "</Report><Report id=\"" + r.Id + "\">" + L("科室入口") + "</Report></ReportLocations>");
            var map = ReportLocations.Load(cfg); Assert(map.For(r).Count == 3 && map.Warnings.Count == 0);
            var other = new ReportDefinition { SourcePath = r.SourcePath, Id = "other", SourceHash = "other" }; Assert(map.For(other).Count == 0);
        });
        check("known user location stays distinct from unconfirmed similarly named reports", () =>
        {
            var map = ReportLocations.Load(Path.Combine(dir, "missing.xml"));
            Assert(map.For(new ReportDefinition { SourcePath = "普通门诊处方记录查询设置.xml" }).Single().Evidence.Contains("用户提供"));
            Assert(map.For(new ReportDefinition { SourcePath = "门诊处方患者明细查询设置.xml" }).Count == 0);
            Assert(map.For(DemoData.Report()).Count == 0);
        });
        check("invalid or DTD location config applies no partial external mapping", () =>
        {
            foreach (var content in new[] {
                "<ReportLocations version=\"1\"><Report id=\"" + r.Id + "\">" + L("partial") + "</Report><Report file=\"../other.xml\">" + L("bad") + "</Report></ReportLocations>",
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><ReportLocations version=\"1\">&e;</ReportLocations>" })
            { File.WriteAllText(cfg, content); var map = ReportLocations.Load(cfg); Assert(map.Warnings.Count == 1 && map.For(r).Count == 0); }
        });
        check("adaptation guidance distinguishes SQL syntax from read-only rejection", () =>
        { Assert(AdaptationGuidance.For("dt：不支持多语句、原生绑定变量、数据库链接或全角括号，需要适配。").Code == "sql"); Assert(AdaptationGuidance.For("只支持查询；发现需拒绝或适配的关键字：TRUNCATE").Code == "read-only"); });
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Catalog classification/location assertion failed"); }
}
