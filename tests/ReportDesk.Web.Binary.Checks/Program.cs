using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

// Load the delivered binaries, never a project reference or a rebuilt Core.
internal static class Program
{
    private const string LegacyCoreHash = "6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7";
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static Assembly web = null!;
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static Dictionary<string, object> D(object value) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(value));
    private static Dictionary<string, object> A(params object[] values)
    { var result = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1]; return result; }
    private static object Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method)!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    private static object Call(object backend, IDisposable context, string method, Dictionary<string, object>? args = null, bool admin = false)
        => Invoke(backend, "Call", context, method, args ?? A(), admin, CancellationToken.None, (Action<string>)(_ => { }));
    private static IDisposable Backend(string root, string reports)
    {
        object options = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
        foreach (var pair in A("DataDirectory", Path.Combine(root, "data"), "SiteDirectory", Path.Combine(root, "site"), "ReportsDirectory", reports, "Offline", true))
            options.GetType().GetProperty(pair.Key)!.SetValue(options, pair.Value);
        Directory.CreateDirectory(Path.Combine(root, "site"));
        Invoke(options, "Validate");
        // BackendAdapter normally runs after WebRuntime configures logging.
        var log = Assembly.Load("ReportDesk.Core").GetType("ReportDesk.Core.ErrorLog")!;
        log.GetMethod("Initialize")!.Invoke(null, new object[] { Path.Combine(root, "data", "Logs") });
        try { return (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.BackendAdapter")!, options)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    private static Dictionary<string, object>[] Reports(object backend, IDisposable context)
        => ((IEnumerable)D(Call(backend, context, "bootstrap"))["reports"]).Cast<object>().Select(D).ToArray();
    private static string StateRoot(string root, string reports)
        => Path.Combine(root, "data", "SyncState", Hash(Encoding.UTF8.GetBytes(reports.ToUpperInvariant())));
    private static void Pending(string root, string reports, string relative)
    {
        string state = StateRoot(root, reports), id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(state, id));
        var bytes = File.ReadAllBytes(Path.Combine(reports, relative.Replace('/', '\\')));
        File.WriteAllBytes(Path.Combine(state, id, "0.bin"), bytes);
        using var writer = XmlWriter.Create(Path.Combine(state, "pending.xml"));
        writer.WriteStartElement("FileTransactionJournal"); writer.WriteElementString("Id", id); writer.WriteElementString("Committed", "false");
        writer.WriteStartElement("Entries"); writer.WriteStartElement("FileTransactionEntry");
        writer.WriteElementString("Path", relative); writer.WriteElementString("Backup", "0.bin"); writer.WriteElementString("Existed", "true"); writer.WriteElementString("Hash", Hash(bytes));
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
    }
    private static int Main(string[] args)
    {
        try
        {
            string bin = Path.GetFullPath(args[0]), sourceRoot = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
            bool negative = args.Length > 3 && args[3] == "--expect-missing-method";
            bool httpOnly = args.Length > 3 && args[3] == "--http-only";
            if (Directory.Exists(root)) throw new Exception("Evidence root must be a new directory.");
            string reports = Path.Combine(root, "reports"); Directory.CreateDirectory(reports);
            var sources = Directory.GetFiles(sourceRoot, "*.xml", SearchOption.AllDirectories);
            if (httpOnly) sources = sources.Where(path => new[] { "按科室(出院)查询设置.xml", "门诊中药处方查询查询设置.xml" }.Contains(Path.GetFileName(path))).ToArray();
            if (httpOnly && sources.Length != 2) throw new Exception("HTTP check requires the two real LIB fixture files.");
            var hashes = sources.Select(path => Hash(File.ReadAllBytes(path))).ToArray();
            for (int i = 0; i < sources.Length; i++)
            {
                string destination = Path.Combine(reports, sources[i].Substring(sourceRoot.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(sources[i], destination);
            }
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
                string path = Path.Combine(bin, new AssemblyName(request.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var core = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Core.dll"));
            Check(Hash(File.ReadAllBytes(core.Location)).Equals(LegacyCoreHash, StringComparison.OrdinalIgnoreCase), "actual deployed legacy Core hash loaded");
            Check(core.GetType("ReportDesk.Core.ParameterOptions")!.GetMethod("IsCurrentDepartment") == null, "deployed Core has no IsCurrentDepartment API");
            web = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Web.dll"));
            if (httpOnly) { CheckHttpExport(root, reports); return 0; }
            string relative = "Config/Xml/住院病历上报-检查结果查询设置.xml", target = Path.Combine(reports, relative.Replace('/', '\\'));
            var original = File.ReadAllBytes(target); var attributes = File.GetAttributes(target);
            if (!negative) { Pending(root, reports, relative); File.SetAttributes(target, attributes | FileAttributes.ReadOnly); }
            try
            {
                using var backend = Backend(root, reports);
                using var context = (IDisposable)Invoke(backend, "CreateContext");
                var list = Reports(backend, context);
                Check(list.Length > 1000, "bootstrap loads real LIB report list without Oracle");
                if (negative)
                {
                    bool failed = false;
                    try { Call(backend, context, "select", A("reportId", list[0]["id"])); }
                    catch (Exception ex) { failed = ex.GetType().FullName == "ReportDesk.Web.WebUserException"; }
                    string log = string.Join("\n", Directory.GetFiles(Path.Combine(root, "data", "Logs"), "*.log").Select(File.ReadAllText));
                    Check(failed && log.Contains("System.MissingMethodException") && log.Contains(".Details"), "reproduces field bootstrap/first-select MissingMethodException with mismatched binaries");
                }
                else
                {
                    Check(!File.Exists(Path.Combine(StateRoot(root, reports), "pending.xml")) && File.ReadAllBytes(target).SequenceEqual(original), "actual DLL clears unchanged read-only legacy journal before startup");
                    var fullExport = Invoke(backend, "ExportSql", context, A(), CancellationToken.None, (Action<string>)(_ => { }));
                    var exportBytes = (byte[])fullExport.GetType().GetProperty("Content")!.GetValue(fullExport)!;
                    Check((string)fullExport.GetType().GetProperty("ReportName")!.GetValue(fullExport)! == "", "full export carries no individual filename");
                    Check(exportBytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "full SQL export uses UTF-8 BOM");
                    var expectedExport = new StringBuilder();
                    var nonQueryOnly = 0;
                    foreach (var report in list)
                    {
                        var exportedEditor = D(Call(backend, context, "sqlEditorOpen", A("reportId", report["id"]), true));
                        var exportSources = ((IEnumerable)exportedEditor["sources"]).Cast<object>().Select(D)
                            .Where(item => !string.IsNullOrWhiteSpace((string)item["sql"]) && (string)item["kind"] != "ConditionUsing").ToArray();
                        if (expectedExport.Length > 0) expectedExport.Append("\r\n\r\n");
                        expectedExport.Append((string)report["name"]).Append("：\r\n");
                        if (exportSources.Length == 0) { nonQueryOnly++; expectedExport.Append("-- 此报表没有主查询、明细或其他非条件数据源 SQL。\r\n"); }
                        for (int i = 0; i < exportSources.Length; i++)
                        {
                            var sourceData = exportSources[i];
                            if (exportSources.Length > 1) expectedExport.Append("-- 数据源：").Append((string)sourceData["name"]).Append("（").Append((string)sourceData["kind"]).Append("）\r\n");
                            expectedExport.Append((string)sourceData["sql"]);
                            if (i + 1 < exportSources.Length) expectedExport.Append("\r\n\r\n");
                        }
                    }
                    Check(Encoding.UTF8.GetString(exportBytes, 3, exportBytes.Length - 3) == expectedExport.ToString(), "full export contains every real report and its original non-condition SQL; no partial success (condition-only=" + nonQueryOnly + ")");
                    var firstReport = list[0];
                    var firstEditor = D(Call(backend, context, "sqlEditorOpen", A("reportId", firstReport["id"]), true));
                    var singleExport = Invoke(backend, "ExportSql", context, A("reportId", firstReport["id"], "name", "untrusted-name"), CancellationToken.None, (Action<string>)(_ => { }));
                    var singleBytes = (byte[])singleExport.GetType().GetProperty("Content")!.GetValue(singleExport)!;
                    Check((string)singleExport.GetType().GetProperty("ReportName")!.GetValue(singleExport)! == (string)firstReport["name"], "single export uses server catalog name, not caller supplied name");
                    Check(Encoding.UTF8.GetString(singleBytes, 3, singleBytes.Length - 3).StartsWith((string)firstReport["name"] + "：\r\n", StringComparison.Ordinal), "selected report exports separately");
                    // Deserialize exactly as the HTTP runtime does, including its array representation.
                    var oneSelected = Invoke(backend, "ExportSql", context, D(new { reportIds = new[] { (string)firstReport["id"] } }), CancellationToken.None, (Action<string>)(_ => { }));
                    Check((string)oneSelected.GetType().GetProperty("ReportName")!.GetValue(oneSelected)! == (string)firstReport["name"] && ((byte[])oneSelected.GetType().GetProperty("Content")!.GetValue(oneSelected)!).SequenceEqual(singleBytes), "one checked report preserves its name and exact SQL");
                    var secondReport = list[1];
                    var secondExport = Invoke(backend, "ExportSql", context, A("reportId", secondReport["id"]), CancellationToken.None, (Action<string>)(_ => { }));
                    var secondBytes = (byte[])secondExport.GetType().GetProperty("Content")!.GetValue(secondExport)!;
                    var multi = Invoke(backend, "ExportSql", context, D(new { reportIds = new[] { (string)secondReport["id"], (string)firstReport["id"] } }), CancellationToken.None, (Action<string>)(_ => { }));
                    var multiBytes = (byte[])multi.GetType().GetProperty("Content")!.GetValue(multi)!;
                    Check((string)multi.GetType().GetProperty("ReportName")!.GetValue(multi)! == "" && Encoding.UTF8.GetString(multiBytes, 3, multiBytes.Length - 3) == Encoding.UTF8.GetString(secondBytes, 3, secondBytes.Length - 3) + "\r\n\r\n" + Encoding.UTF8.GetString(singleBytes, 3, singleBytes.Length - 3), "multiple checked reports export exactly in selection order");
                    foreach (var invalid in new[] {
                        D(new { reportIds = Array.Empty<string>() }), D(new { reportIds = (object)null! }),
                        D(new { reportIds = "not-an-array" }), D(new { reportIds = new object[] { 123 } }),
                        D(new { reportIds = new[] { "" } }), D(new { reportIds = new[] { (string)firstReport["id"], (string)firstReport["id"] } }),
                        D(new { reportIds = new[] { (string)firstReport["id"], "missing-report-id" } }),
                        D(new { reportId = (string)firstReport["id"], reportIds = new[] { (string)secondReport["id"] } }) })
                    {
                        bool rejected = false;
                        try { Invoke(backend, "ExportSql", context, invalid, CancellationToken.None, (Action<string>)(_ => { })); }
                        catch (Exception ex) { rejected = ex.GetType().FullName == "ReportDesk.Web.WebUserException"; }
                        Check(rejected, "invalid or conflicting selected IDs reject the whole download");
                    }
                    var firstSource = ((IEnumerable)firstEditor["sources"]).Cast<object>().Select(D).First(item => (bool)item["editable"]);
                    Call(backend, context, "sqlEditorCheck", A("reportId", firstReport["id"], "token", firstEditor["token"], "sourceIndex", firstSource["index"], "sql", firstSource["sql"]), true);
                    Check(true, "SQL export does not invalidate the open editor token");
                    var movedSource = (string)firstEditor["path"];
                    var temporarySource = movedSource + ".sql-export-test-backup";
                    bool missingRejected = false;
                    try
                    {
                        File.Move(movedSource, temporarySource);
                        try { Invoke(backend, "ExportSql", context, A(), CancellationToken.None, (Action<string>)(_ => { })); }
                        catch (Exception ex) { missingRejected = ex.GetType().FullName == "ReportDesk.Web.WebUserException" && !ex.Message.Contains(movedSource) && ex.Message.Contains("未生成部分文件"); }
                    }
                    finally { if (File.Exists(temporarySource)) File.Move(temporarySource, movedSource); }
                    Check(missingRejected, "missing source rejects the full export without exposing its path or returning a partial file");
                    using (var cancelled = new CancellationTokenSource())
                    {
                        cancelled.Cancel();
                        bool stopped = false; try { Invoke(backend, "ExportSql", context, A(), cancelled.Token, (Action<string>)(_ => { })); } catch (OperationCanceledException) { stopped = true; }
                        Check(stopped, "cancelled SQL export does not produce a download");
                    }
                    foreach (var report in list)
                    {
                        var selected = D(Call(backend, context, "select", A("reportId", report["id"])));
                        CheckMetadata(selected);
                        Call(backend, context, "definition", A("reportId", report["id"]));
                    }
                    Check(true, "actual DLL opens all " + list.Length + " real reports with legacy parameter metadata");
                    var reportDto = list.Single(item => (string)item["name"] == "住院病历上报-检查结果");
                    var editor = D(Call(backend, context, "sqlEditorOpen", A("reportId", reportDto["id"]), true));
                    var source = ((IEnumerable)editor["sources"]).Cast<object>().Select(D).First(item => (bool)item["editable"]);
                    bool rolledBack = false;
                    try { Call(backend, context, "sqlEditorSave", SaveArgs(editor, source), true); }
                    catch (Exception ex) { rolledBack = ex.Message.Contains("原文件未改变或已恢复"); }
                    Check(rolledBack && File.ReadAllBytes(target).SequenceEqual(original) && !File.Exists(Path.Combine(StateRoot(root, reports), "pending.xml")), "actual SQL save refusal preserves original and does not poison recovery");
                    Call(backend, context, "select", A("reportId", reportDto["id"]));
                    File.SetAttributes(target, attributes);
                    editor = D(Call(backend, context, "sqlEditorOpen", A("reportId", reportDto["id"]), true));
                    var saved = D(Call(backend, context, "sqlEditorSave", SaveArgs(editor, source), true));
                    Check((bool)saved["saved"] && (bool)saved["reloaded"] && !File.ReadAllBytes(target).SequenceEqual(original) && !File.Exists(Path.Combine(StateRoot(root, reports), "pending.xml")), "actual writable XML save succeeds, reloads and clears journal");
                    Call(backend, context, "select", A("reportId", reportDto["id"]));
                }
            }
            finally { File.SetAttributes(target, attributes); }
            if (!negative)
            {
                using var restarted = Backend(root, reports);
                using var context = (IDisposable)Invoke(restarted, "CreateContext");
                var list = Reports(restarted, context);
                Call(restarted, context, "select", A("reportId", list[0]["id"]));
                Check(true, "restart after failed and successful saves remains usable");
            }
            Check(sources.Select(path => Hash(File.ReadAllBytes(path))).SequenceEqual(hashes), "all original LIB XML source hashes unchanged");
            Console.WriteLine("PASS " + checks + " binary checks; real Oracle and field IIS NOT RUN.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    private static void CheckMetadata(Dictionary<string, object> selected)
    {
        foreach (var parameter in ((IEnumerable)selected["parameters"]).Cast<object>().Select(D))
            if (parameter.ContainsKey("contextDepartment")) throw new Exception("Unexpected department-picker contract for legacy Core/UI.");
    }
    private static void CheckHttpExport(string root, string reports)
    {
        object options = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
        foreach (var pair in A("DataDirectory", Path.Combine(root, "data"), "SiteDirectory", Path.Combine(root, "site"), "ReportsDirectory", reports, "Offline", true))
            options.GetType().GetProperty(pair.Key)!.SetValue(options, pair.Value);
        Directory.CreateDirectory(Path.Combine(root, "site"));
        Action<Func<CancellationToken, Task>> schedule = work => work(CancellationToken.None).GetAwaiter().GetResult();
        using var runtime = (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.WebRuntime")!, options, schedule, null)!;
        object Request(string method, string path, string cookie = "", string csrf = "", object? args = null)
        {
            object req = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebRequestData")!)!;
            foreach (var pair in A("Method", method, "Path", path, "Secure", true, "RemoteAddress", "127.0.0.1", "Authority", "https://reportdesk.test", "Origin", "https://reportdesk.test", "ContentType", "application/json", "Cookie", cookie, "Csrf", csrf, "Body", Json.Serialize(new { tabId = "http-test-01", args = args ?? new { } })))
                req.GetType().GetProperty(pair.Key)!.SetValue(req, pair.Value);
            var task = (Task)Invoke(runtime, "HandleAsync", req);
            task.GetAwaiter().GetResult();
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        int Status(object r) => (int)r.GetType().GetProperty("Status")!.GetValue(r)!;
        byte[] Body(object r) => (byte[])r.GetType().GetProperty("Body")!.GetValue(r)!;
        Dictionary<string, object> Parse(object r) => Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Body(r)));
        object session = Request("GET", "/api/session");
        string cookie = ((Dictionary<string, string>)session.GetType().GetProperty("Headers")!.GetValue(session)!)["Set-Cookie"].Split(';')[0].Split('=')[1];
        string csrf = (string)Parse(session)["csrf"];
        Dictionary<string, object> Submit(string method, object args)
        {
            var response = Request("POST", "/api/" + method, cookie, csrf, args);
            if (Status(response) != 202) throw new Exception("HTTP task was not accepted: " + Status(response));
            var id = (string)Parse(response)["jobId"];
            return Parse(Request("GET", "/api/tasks/" + id, cookie));
        }
        var listTask = Submit("list", new { });
        var list = ((IEnumerable)listTask["data"]).Cast<object>().Select(D).ToArray();
        Check(list.Length == 2, "HTTP real backend lists both copied real reports");
        var task = Submit("admin/exportSql", new { reportIds = new[] { (string)list[0]["id"], (string)list[1]["id"] } });
        Check((string)task["state"] == "succeeded", "HTTP JSON reportIds reaches real backend and finishes");
        var download = Request("GET", (string)D(task["data"])["downloadUrl"], cookie);
        var text = Encoding.UTF8.GetString(Body(download));
        Check(Status(download) == 200 && text.Contains((string)list[0]["name"] + "：\r\n") && text.Contains((string)list[1]["name"] + "：\r\n"), "HTTP download contains both selected reports");
        var empty = Submit("admin/exportSql", new { reportIds = Array.Empty<string>() });
        Check((string)empty["state"] == "failed" && empty["data"] == null, "HTTP empty selection fails without a download");
        Console.WriteLine("PASS " + checks + " HTTP checks; field IIS and Oracle NOT RUN.");
    }
    private static Dictionary<string, object> SaveArgs(Dictionary<string, object> editor, Dictionary<string, object> source)
        => A("reportId", editor["reportId"], "token", editor["token"], "sourceIndex", source["index"], "sql", source["sql"] + "\r\n-- ReportDesk isolated binary regression");
}
