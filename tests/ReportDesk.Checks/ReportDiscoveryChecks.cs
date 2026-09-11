using System;
using System.IO;
using System.Linq;
using System.Threading;
using ReportDesk.Core;

internal static class ReportDiscoveryChecks
{
    internal static void Run(string folder, Action<string, Action> check)
    {
        var root = Path.Combine(folder, "his-root-" + Guid.NewGuid().ToString("N"));
        var xml = Path.Combine(root, "Config", "Xml"); Directory.CreateDirectory(xml);
        const string layout = "<Spread class=\"FarPoint.Win.Spread.FpSpread\"><Settings /></Spread>";
        var query = Path.Combine(xml, "样例查询设置.xml");
        var source = "<ReportQueryInfo><QueryFilePath>\\Config\\Xml\\样例查询设置.xml</QueryFilePath><QueryDataSource><QueryDataSource><Name>dtMain</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType><AddMapColumn>true</AddMapColumn><IsCross>true</IsCross></QueryDataSource></QueryDataSource><ReportInfo><IsDetail>true</IsDetail><DetailDirectory>\\Config\\Xml\\样例明细.xml</DetailDirectory></ReportInfo></ReportQueryInfo>";
        File.WriteAllText(query, source); File.WriteAllText(Path.Combine(xml,"样例报表设置.xml"),layout); File.WriteAllText(Path.Combine(xml,"样例明细.xml"),layout);
        File.WriteAllText(Path.Combine(root,"ordinary.xml"),"<settings />");
        File.WriteAllText(Path.Combine(root,"not-xml.txt"),"ignored");
        var oddName = Path.Combine(root,"arbitrary.XML"); File.WriteAllText(oddName,"<ReportQueryInfo><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select 2 from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>");
        check("HIS root recursively identifies query XML by root and preserves sources", () =>
        {
            var before = File.ReadAllBytes(query); var imported = ReportImporter.ImportFolder(root);
            Assert(imported.Reports.Count == 2 && imported.Inventory!.Layouts.Count == 2 && imported.Inventory.OtherXml == 1 && imported.Errors.Count == 0);
            Assert(before.SequenceEqual(File.ReadAllBytes(query)) && imported.Reports.Any(r => r.SourcePath == oddName));
        });
        check("explicit detail path matches and main filename stays an unverified candidate", () =>
        {
            var imported = ReportImporter.ImportFolder(root); var report = imported.Reports.Single(r=>r.SourcePath==query); var links = imported.RelatedFiles[report.Id];
            Assert(links[0].Status=="Candidate" && links[1].Status=="Matched" && links[1].Paths.Single()==Path.Combine(xml,"样例明细.xml"));
            Assert(report.Issues.Any(i=>i.Contains("IsCross")) && report.Issues.All(i=>!i.Contains("AddMapColumn")) && report.Status=="待适配");
        });
        check("single query import matches siblings without importing unrelated queries", () =>
        { var s=ReportImporter.ImportWithRelated(query); Assert(s.Reports.Count==1 && s.RelatedFiles.Count==1); });
        check("restart root inference requires an exact query self reference", () =>
        { Assert(ReportFileDiscovery.InferRoot(query)==root && ReportFileDiscovery.InferRoot(oddName)==root); });
        check("reimport preserves catalog identity annotations and serialization shape", () =>
        {
            var s=ReportImporter.ImportFolder(root); var c=new Catalog(); ReportImporter.Merge(c,s); var r=c.Reports.Single(x=>x.SourcePath==query);
            r.Favorite=true; r.Notes="original"; var id=r.Id; ReportImporter.Merge(c,ReportImporter.ImportFolder(root));
            Assert(c.Reports.Single(x=>x.Id==id).Favorite && c.Reports.Single(x=>x.Id==id).Notes=="original");
            var store=new CatalogStore(Path.Combine(folder,"discovery-catalog")); store.Save(c);
            var json=File.ReadAllText(store.FilePath); Assert(!json.Contains("RelatedFiles") && !json.Contains("Inventory") && store.Load().Reports.Count==2);
        });
        check("flattened and duplicate layout names never become an exact match", () =>
        {
            var flat=Path.Combine(root,"flat"); Directory.CreateDirectory(flat); var q=Path.Combine(flat,"flat.xml"); File.WriteAllText(q,source);
            File.WriteAllText(Path.Combine(flat,"样例明细.xml"),layout);
            var inv=ReportFileDiscovery.Scan(flat); Assert(ReportFileDiscovery.Match(q,inv)[0].Status=="Candidate");
            var duplicate=Path.Combine(flat,"other"); Directory.CreateDirectory(duplicate); File.WriteAllText(Path.Combine(duplicate,"样例明细.xml"),layout);
            Assert(ReportFileDiscovery.Match(q,ReportFileDiscovery.Scan(flat))[0].Status=="Ambiguous");
        });
        check("missing or external layout references are not followed or silently accepted", () =>
        {
            var dir=Path.Combine(folder,"references-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            var q=Path.Combine(dir,"single.xml");
            foreach(var reference in new[]{"..\\outside.xml","\\\\server\\share\\template.xml","Z:\\outside.xml"})
            { File.WriteAllText(q,source.Replace("\\Config\\Xml\\样例明细.xml",reference)); Assert(ReportFileDiscovery.Match(q,ReportFileDiscovery.Scan(dir))[0].Status=="Rejected"); }
            File.WriteAllText(q,source); Assert(ReportFileDiscovery.Match(q,ReportFileDiscovery.Scan(dir))[0].Status=="Missing");
        });
        check("unrelated malformed XML and DTD produce diagnostics without aborting discovery", () =>
        {
            var dir=Path.Combine(folder,"bad-xml-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir,"ok.xml"),source);
            File.WriteAllText(Path.Combine(dir,"broken.xml"),"<");
            File.WriteAllText(Path.Combine(dir,"entity.xml"),"<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///never-read'>]><ReportQueryInfo>&secret;</ReportQueryInfo>");
            var s=ReportImporter.ImportFolder(dir); Assert(s.Reports.Count==1 && s.Errors.Count==2 && s.Errors.All(x=>!x.Contains("never-read")));
        });
        check("HIS root scan cancellation prevents a successful partial import", () =>
        {
            var cancel=new CancellationTokenSource(); cancel.Cancel();
            try { ReportImporter.ImportFolder(root,cancel.Token); throw new Exception("Cancellation not honored"); } catch(OperationCanceledException) { }
            var dir=Path.Combine(folder,"cancel-scan-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            for(var i=0;i<110;i++) File.WriteAllText(Path.Combine(dir,i+".xml"),"<settings />");
            using var during=new CancellationTokenSource();
            try { ReportImporter.ImportFolder(dir,during.Token,_=>during.Cancel()); throw new Exception("Mid-scan cancellation not honored"); } catch(OperationCanceledException) { }
        });
    }
    private static void Assert(bool condition) { if(!condition) throw new Exception("Report discovery assertion failed"); }
}
