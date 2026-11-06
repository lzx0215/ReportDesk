using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Exercise the Web bridge with the actual delivered legacy Core; no Oracle or IIS required.
internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static Assembly web = null!;
    private static int checks;
    private static Dictionary<string, object> A(params object[] values)
    { var d = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) d[(string)values[i]] = values[i + 1]; return d; }
    private static Dictionary<string, object> D(object o) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(o));
    private static Dictionary<string, object>[] Items(object o) => ((IEnumerable)o).Cast<object>().Select(D).ToArray();
    private static object Invoke(object o, string m, params object[] args)
    { try { return o.GetType().GetMethod(m, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args)!; } catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; } }
    private static Dictionary<string, object> Call(object a, IDisposable c, string method, Dictionary<string, object>? args = null, bool admin = true)
        => D(Invoke(a, "Call", c, method, args ?? A(), admin, CancellationToken.None, (Action<string>)(_ => { })));
    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); checks++; }
    private static void Reject(Action action, string label)
    { try { action(); } catch (Exception) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private static void Upload(object adapter, IDisposable context, string path, byte[] bytes)
    {
        for (int offset = 0; offset < bytes.Length; offset += 1024)
            Call(adapter, context, "localUpload", A("path", path, "offset", offset, "total", bytes.Length,
                "content", Convert.ToBase64String(bytes, offset, Math.Min(1024, bytes.Length - offset))));
    }
    private static string Activate(object adapter, IDisposable context, byte[] query, params Tuple<string, byte[]>[] layouts)
    {
        Call(adapter, context, "localBegin", A("name", "local-index"));
        Upload(adapter, context, "a/工作集查询设置.xml", query);
        foreach (var layout in layouts) Upload(adapter, context, layout.Item1, layout.Item2);
        var result = Call(adapter, context, "localActivate");
        var id = (string)Items(result["reports"]).Single()["id"];
        var path = Items(result["queryPaths"]).Single();
        Check((string)path["id"] == id && (string)path["path"] == "a/工作集查询设置.xml", "activation maps actual report ID to browser-relative query path");
        return id;
    }
    private static Dictionary<string, object> Related(object adapter, IDisposable context, string id, string role)
        => Items(Call(adapter, context, "relatedFiles", A("reportId", id))["files"]).Single(x => ((string)x["Role"]).Contains(role));
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "verification", "workset-backend-" + Guid.NewGuid().ToString("N")));
        try
        {
            var bin = Path.GetFullPath(args[0]);
            var fixtures = Path.GetFullPath("artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml");
            var queryText = File.ReadAllText(Path.Combine(fixtures, "抗菌药物消耗情况查询设置.xml"));
            var layout = File.ReadAllBytes(Path.Combine(fixtures, "抗菌药物消耗情况报表设置.xml"));
            var query = Encoding.UTF8.GetBytes(queryText.Replace("<IsDetail>false</IsDetail>", "<IsDetail>true</IsDetail>").Replace("<DetailDirectory></DetailDirectory>", "<DetailDirectory>detail.xml</DetailDirectory>"));
            Check(Encoding.UTF8.GetString(query).Contains("<DetailDirectory>detail.xml</DetailDirectory>"), "fixture has explicit detail reference");
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var f = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(f) ? Assembly.LoadFrom(f) : null; };
            var core = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Core.dll")); web = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Web.dll"));
            core.GetType("ReportDesk.Core.ErrorLog")!.GetMethod("Initialize")!.Invoke(null, new object[] { Path.Combine(root, "Logs") });
            var options = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
            options.GetType().GetProperty("DataDirectory")!.SetValue(options, Path.Combine(root, "data"));
            options.GetType().GetProperty("SiteDirectory")!.SetValue(options, Path.Combine(root, "site"));
            options.GetType().GetProperty("ReportsDirectory")!.SetValue(options, "ignored");
            options.GetType().GetProperty("Offline")!.SetValue(options, true);
            Invoke(options, "Validate");
            var config = Path.Combine(root, "data", "config"); Directory.CreateDirectory(config);
            var hash = new string('a', 64);
            File.WriteAllText(Path.Combine(config, "report-locations.xml"),
                "<ReportLocations version=\"1\"><Report file=\"重名查询设置.xml\"><Location evidence=\"file-evidence\"><Segment>A</Segment><Segment>B</Segment></Location></Report>" +
                "<Report hash=\"" + hash + "\"><Location evidence=\"hash-evidence\"><Segment>C</Segment><Segment>D</Segment></Location></Report>" +
                "<Report id=\"server-only-id\"><Location evidence=\"id-evidence\"><Segment>E</Segment><Segment>F</Segment></Location></Report></ReportLocations>", Encoding.UTF8);
            var originalLocations = File.ReadAllBytes(Path.Combine(config, "report-locations.xml"));
            using var adapter = (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.BackendAdapter")!, options)!;
            using var first = (IDisposable)Invoke(adapter, "CreateContext");
            using var second = (IDisposable)Invoke(adapter, "CreateContext");
            var input = A("items", new[] { A("path", "a/重名查询设置.xml", "hash", hash) });
            var located = Items(Call(adapter, first, "localLocations", input)["items"]).Single();
            var matches = Items(located["locations"]);
            Check((string)located["path"] == "a/重名查询设置.xml" && matches.Length == 2 &&
                matches.Any(m => (string)m["Evidence"] == "file-evidence" && (string)m["Match"] == "文件名关联，版本未核对") &&
                matches.Any(m => (string)m["Evidence"] == "hash-evidence" && (string)m["Match"] == "查询文件哈希匹配"),
                "local location lookup preserves file/hash evidence without inventing an ID match");
            var batch = Items(Call(adapter, first, "localLocations", A("items", new[] {
                A("path", "a/重名查询设置.xml", "hash", hash), A("path", "b/重名查询设置.xml", "hash", new string('b', 64)) }))["items"]);
            Check(batch.Length == 2 && (string)batch[0]["path"] == "a/重名查询设置.xml" && (string)batch[1]["path"] == "b/重名查询设置.xml" &&
                Items(batch[0]["locations"]).Length == 2 && Items(batch[1]["locations"]).Length == 1 &&
                (string)Items(batch[1]["locations"]).Single()["Match"] == "文件名关联，版本未核对",
                "batch mapping keeps same-named files and different hashes separate");
            Check(originalLocations.SequenceEqual(File.ReadAllBytes(Path.Combine(config, "report-locations.xml"))), "location lookup leaves configuration bytes untouched");
            Reject(() => Call(adapter, first, "localLocations", input, false), "location lookup requires maintenance authority");
            Reject(() => Call(adapter, first, "localLocations", A("items", new[] { A("path", "../secret.xml", "hash", hash) })), "location lookup rejects traversal");
            Reject(() => Call(adapter, first, "localLocations", A("items", new[] { A("path", "a/x.xml", "hash", "bad") })), "location lookup rejects malformed hash");
            Reject(() => Call(adapter, first, "localLocations", A("items", Enumerable.Range(0, 101).Select(i => A("path", i + ".xml", "hash", hash)).ToArray())), "location lookup limits batch size");
            Call(adapter, first, "localBegin", A("name", "two reports"));
            Upload(adapter, first, "a/重名查询设置.xml", query);
            Upload(adapter, first, "b/重名查询设置.xml", query);
            var multiple = Call(adapter, first, "localActivate");
            var mapped = Items(multiple["queryPaths"]);
            Check(mapped.Length == 2 && mapped.Select(x => (string)x["id"]).Distinct().Count() == 2 &&
                mapped.Select(x => (string)x["path"]).OrderBy(x => x).SequenceEqual(new[] { "a/重名查询设置.xml", "b/重名查询设置.xml" }),
                "same-named reports retain distinct IDs and source paths");
            var id = Activate(adapter, first, query, Tuple.Create("b/detail.xml", layout), Tuple.Create("c/detail.xml", layout));
            var related = Related(adapter, first, id, "明细");
            Check((string)related["Status"] == "Ambiguous" && ((IEnumerable)related["Paths"]).Cast<object>().Count() == 2, "all fallback candidates remain ambiguous");
            var other = Activate(adapter, second, query, Tuple.Create("a/detail.xml", layout));
            Check((string)Related(adapter, second, other, "明细")["Status"] == "Matched", "exact explicit reference remains matched");
            id = Activate(adapter, first, query, Tuple.Create("a/detail.xml", layout), Tuple.Create("detail.xml", layout), Tuple.Create("b/detail.xml", layout));
            related = Related(adapter, first, id, "明细");
            Check((string)related["Status"] == "Ambiguous" && ((IEnumerable)related["Paths"]).Cast<object>().Count() == 2, "two exact paths beat a third fallback candidate");
            var xmls = Directory.GetFiles(Path.Combine(root, "data", "LocalWorkspaces"), "*.xml", SearchOption.AllDirectories);
            Check(xmls.Length == 6, "successful replacement removes previous workset while preserving other tab");
            Call(adapter, first, "localUseServer");
            Check(Directory.GetFiles(Path.Combine(root, "data", "LocalWorkspaces"), "*.xml", SearchOption.AllDirectories).Length == 2, "closing one tab removes its temporary XML only");
            Check((string)Related(adapter, second, other, "明细")["Status"] == "Matched", "other tab remains active");
            Console.WriteLine("PASS " + checks + " legacy Core workset checks; " + root); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); Console.Error.WriteLine("Evidence: " + root); return 1; }
    }
}
