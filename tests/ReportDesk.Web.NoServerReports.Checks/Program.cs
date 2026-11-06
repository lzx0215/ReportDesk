using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Reflection-only checks run against either modern Core or the delivered legacy Core.
// Report XML is copied from retained real HIS fixtures; no Oracle call or result is fabricated.
internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static Assembly web = null!;
    private static int checks;
    private const string QueryName = "抗菌药物消耗情况查询设置.xml";
    private const string LayoutName = "抗菌药物消耗情况报表设置.xml";
    private static void Check(bool pass, string name)
    { if (!pass) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    private static Dictionary<string, object> A(params object[] values)
    { var result = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1]; return result; }
    private static Dictionary<string, object> D(object value) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(value));
    private static Dictionary<string, object>[] Items(object value) => ((IEnumerable)value).Cast<object>().Select(D).ToArray();
    private static object Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static Dictionary<string, object> Call(object adapter, IDisposable context, string method, Dictionary<string, object>? args = null, bool admin = true)
        => D(Invoke(adapter, "Call", context, method, args ?? A(), admin, CancellationToken.None, (Action<string>)(_ => { })));
    private static void Fails(Action action, string contains, string name)
    {
        try { action(); }
        catch (Exception error) { Check(error.Message.Contains(contains), name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    private static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)); }
    private static object Options(string root, string reports, bool server = false)
    {
        var result = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
        Set(result, "DataDirectory", Path.Combine(root, "data")); Set(result, "SiteDirectory", Path.Combine(root, "site"));
        Set(result, "ReportsDirectory", reports); Set(result, "ServerReportsEnabled", server); Set(result, "Offline", true);
        return result;
    }
    private static IDisposable Adapter(object options)
    { Invoke(options, "Validate"); return (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.BackendAdapter")!, options)!; }
    private static Dictionary<string, string> ConfigSnapshot(string data)
        => Directory.EnumerateFiles(Path.Combine(data, "config")).ToDictionary(Path.GetFileName, f => Hash(File.ReadAllBytes(f)), StringComparer.OrdinalIgnoreCase)!;
    private static bool SameConfig(Dictionary<string, string> expected, string data)
    { var actual = ConfigSnapshot(data); return expected.Count == actual.Count && expected.All(p => actual.TryGetValue(p.Key, out var value) && value == p.Value); }
    private static string Activate(object adapter, IDisposable context, byte[] query, byte[] layout, string name)
    {
        Call(adapter, context, "localBegin", A("name", name));
        foreach (var item in new[] { Tuple.Create(QueryName, query), Tuple.Create(LayoutName, layout) })
            for (int offset = 0; offset < item.Item2.Length; offset += 1024)
                Call(adapter, context, "localUpload", A("path", "Config/Xml/" + item.Item1, "offset", offset, "total", item.Item2.Length,
                    "content", Convert.ToBase64String(item.Item2, offset, Math.Min(1024, item.Item2.Length - offset))));
        return (string)Items(Call(adapter, context, "localActivate")["reports"]).Single()["id"];
    }
    private static Dictionary<string, object> ConnectionInput(string name) => A("newProfile", true, "name", name, "host", "localhost",
        "port", 1521, "service", "offline", "username", "offline_reader", "password", "SYNTHETIC_LOCAL_ONLY", "remember", false);

    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "verification", "no-server-reports-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
        try
        {
            string bin = Path.GetFullPath(args[0]);
            string source = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml");
            var querySource = Path.Combine(source, QueryName); var layoutSource = Path.Combine(source, LayoutName);
            var query = File.ReadAllBytes(querySource); var layout = File.ReadAllBytes(layoutSource);
            var sourceHashes = new[] { Hash(query), Hash(layout) };
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                var file = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            var core = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Core.dll"));
            web = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Web.dll"));
            core.GetType("ReportDesk.Core.ErrorLog")!.GetMethod("Initialize")!.Invoke(null, new object[] { Path.Combine(root, "Logs") });
            var fresh = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
            Check(!(bool)fresh.GetType().GetProperty("ServerReportsEnabled")!.GetValue(fresh)!, "server report mode defaults off");
            var loaded = fresh.GetType().GetMethod("Load")!.Invoke(null, new object[] { Path.Combine(root, "site") })!;
            Check(!(bool)loaded.GetType().GetProperty("ServerReportsEnabled")!.GetValue(loaded)!, "absent app setting loads server report mode off");

            var data = Path.Combine(root, "data"); var config = Path.Combine(data, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "connection.json"), "{\"Name\":\"retained\",\"Host\":\"localhost\",\"Port\":1521,\"Service\":\"offline\",\"Username\":\"offline_reader\",\"RememberPassword\":false}");
            File.WriteAllText(Path.Combine(config, "catalog.json"), "retained-unused-catalog");
            File.WriteAllText(Path.Combine(config, "import-sources.json"), "retained-unread-import-sources");
            var initialConfig = ConfigSnapshot(data);
            // Invalid characters prove the legacy reports value is not normalized or inspected.
            var options = Options(root, "old\0invalid:reports");
            Set(options, "SyncEnabled", true); Set(options, "SyncSeconds", -1);
            using (var adapter = Adapter(options))
            using (var first = (IDisposable)Invoke(adapter, "CreateContext"))
            using (var second = (IDisposable)Invoke(adapter, "CreateContext"))
            using (var empty = (IDisposable)Invoke(adapter, "CreateContext"))
            {
                Check(Items(Call(adapter, first, "bootstrap", admin: false)["reports"]).Length == 0, "invalid old report path starts with empty catalog");
                Check(SameConfig(initialConfig, data), "startup and bootstrap preserve every existing config byte");
                Check(Field(adapter, "work") == null && Field(adapter, "files") == null && Field(adapter, "sync") == null, "no server report lock transaction or sync coordinator created");
                Check(!Directory.Exists(Path.Combine(data, "Reports")) && !Directory.Exists(Path.Combine(data, "SyncState")), "no placeholder report or sync state directory created");
                var status = D(Invoke(adapter, "SyncStatus"));
                Check((string)status["code"] == "ServerReportsDisabled" && !(bool)status["enabled"] && !(bool)status["serverReportsEnabled"] && (string)status["reportsDirectory"] == "", "disabled sync response exposes no stale report path");
                Check((string)D(Invoke(adapter, "Synchronize", CancellationToken.None, (Action<string>)(_ => throw new Exception("Unexpected sync"))))["code"] == "ServerReportsDisabled", "old enabled sync flag cannot run synchronization");
                foreach (var method in new[] { "import", "checkNewReports", "recheck", "publishPreview", "resolveSyncConflict", "sqlEditorSave", "xmlEditorSave", "reloadReport" })
                    Fails(() => Call(adapter, first, method), "服务器报表目录已停用", "disabled server operation rejected: " + method);
                Fails(() => Invoke(adapter, "BrowseReports", ""), "服务器报表目录已停用", "server directory browsing disabled");
                Fails(() => Invoke(adapter, "UploadImport", A()), "服务器报表目录已停用", "server file upload disabled");
                Fails(() => Call(adapter, first, "localBegin", A("name", "first"), false), "维护权限", "local directory still requires maintenance authority");
                var settings = Call(adapter, first, "settings");
                Check((string)settings["name"] == "retained", "saved shared connection readable without report directory");
                Fails(() => Call(adapter, first, "settings", admin: false), "不允许", "database settings retain maintenance boundary");

                string firstId = Activate(adapter, first, query, layout, "first"), secondId = Activate(adapter, second, query, layout, "second");
                Check(firstId != secondId, "two local directories get isolated report identities");
                Check(Items(Call(adapter, empty, "bootstrap", admin: false)["reports"]).Length == 0, "unselected tab retains empty catalog");
                Fails(() => Call(adapter, first, "select", A("reportId", secondId), false), "报表不存在", "one local workspace cannot access the other report");
                var editor = Call(adapter, first, "xmlEditorOpen", A("reportId", firstId, "kind", "query"));
                Check(((string)editor["text"]).Contains("ReportQueryInfo"), "local report editor works without server LIB");
                Check(SameConfig(initialConfig, data), "local upload activation and editor preserve stored configuration bytes");
                string savedId = (string)Call(adapter, first, "saveSettings", ConnectionInput("additional"))["profileId"];
                Check((string)Call(adapter, second, "settings")["name"] == "retained", "adding shared connection does not switch active profile");
                var active = Call(adapter, second, "activateConnection", A("profileId", savedId));
                Check((string)active["name"] == "additional" && (bool)active["hasPassword"], "shared connection save and activate work in local mode");
                var afterConnection = ConfigSnapshot(data);
                Check(afterConnection["import-sources.json"] == initialConfig["import-sources.json"] && afterConnection["catalog.json"] == initialConfig["catalog.json"], "explicit connection save leaves other config files unchanged");
                Check(!File.ReadAllText(Path.Combine(config, "connection.json")).Contains("SYNTHETIC_LOCAL_ONLY"), "session password does not persist as plaintext");
                var pending = Call(adapter, first, "xmlEditorSave", A("reportId", firstId, "token", editor["token"], "text", editor["text"]));
                Check((bool)Invoke(adapter, "HasPendingLocalSave", first), "pending local save is available to lifecycle cleanup guard");
                Fails(() => Call(adapter, first, "localUseServer"), "请先完成", "closing directory refuses unconfirmed local save");
                Call(adapter, first, "localCommit", A("saveId", pending["saveId"], "files", Array.Empty<object>()));
                var closed = Call(adapter, first, "localUseServer");
                Check(Items(closed["reports"]).Length == 0 && !(bool)Invoke(adapter, "IsLocalContext", first), "localUseServer closes current directory and returns empty bootstrap");
                Check((string)Items(Call(adapter, second, "bootstrap", admin: false)["reports"]).Single()["id"] == secondId, "closing one directory preserves the other workspace");
                Check(SameConfig(afterConnection, data), "closing directory preserves saved connection and remaining config bytes");
                Check(Directory.EnumerateFiles(Path.Combine(data, "LocalWorkspaces"), QueryName, SearchOption.AllDirectories).Count() == 1, "closing current directory removes only its temporary XML");
            }
            Check(!Directory.Exists(Path.Combine(data, "LocalWorkspaces")) || !Directory.EnumerateFiles(Path.Combine(data, "LocalWorkspaces"), "*.xml", SearchOption.AllDirectories).Any(), "disposing local sessions releases remaining temporary XML");

            var missing = Path.Combine(root, "never-created-LIB");
            using (var adapter = Adapter(Options(Path.Combine(root, "missing-case"), missing)))
                Check(!Directory.Exists(missing), "missing old LIB path remains absent after startup");
            Fails(() => Invoke(Options(Path.Combine(root, "server-missing"), missing, true), "Validate"), "不存在", "explicit server mode still rejects missing configured directory");
            var serverRoot = Path.Combine(root, "server-mode"); var reports = Path.Combine(serverRoot, "reports");
            Directory.CreateDirectory(reports);
            File.WriteAllBytes(Path.Combine(reports, QueryName), query); File.WriteAllBytes(Path.Combine(reports, LayoutName), layout);
            using (var adapter = Adapter(Options(serverRoot, reports, true)))
            using (var context = (IDisposable)Invoke(adapter, "CreateContext"))
            {
                var serverId = (string)Items(Call(adapter, context, "bootstrap", admin: false)["reports"]).Single()["id"];
                Check(serverId.Length > 0 && Field(adapter, "work") != null && Field(adapter, "sync") != null, "explicit server mode retains scan and synchronization infrastructure");
                Check((bool)D(Invoke(adapter, "SyncStatus"))["serverReportsEnabled"], "explicit server mode status remains enabled");
                Call(adapter, context, "checkNewReports");
                Activate(adapter, context, query, layout, "temporary");
                Check((string)Items(Call(adapter, context, "localUseServer")["reports"]).Single()["id"] == serverId, "explicit server mode can return from local directory to original catalog");
            }
            Check(Hash(File.ReadAllBytes(querySource)) == sourceHashes[0] && Hash(File.ReadAllBytes(layoutSource)) == sourceHashes[1], "retained genuine HIS fixture bytes unchanged");
            File.WriteAllText(Path.Combine(root, "PASS.txt"), "PASS " + checks + " offline no-server-reports checks. Oracle/IIS/browser writes NOT RUN.\r\n", Encoding.UTF8);
            Console.WriteLine("PASS " + checks + " checks; evidence: " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Evidence retained: " + root); return 1; }
    }
}
