using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ReportDesk.Web;
using ReportDesk.Core;

internal static class Checks
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static int checks;
    private static void Assert(bool value, string name) { if (!value) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    private static Dictionary<string, object> Read(WebResponseData response) => Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(response.Body));
    private static Action<Func<CancellationToken, Task>> Scheduler => action => { _ = Task.Run(() => action(CancellationToken.None)); };
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if ((args.Length == 3 || args.Length == 4) && args[0] == "--serve") { await Serve(args[1], args[2], args.Length == 4 ? args[3] : ""); return 0; }
            if (args.Length == 5 && args[0] == "--check-startup") { await CheckStartup(args[1], args[2], args[3], int.Parse(args[4])); return 0; }
#if !REPORTDESK_LEGACY_CORE
            if (args.Length == 2 && args[0] == "--check-context") { await CheckContext(args[1]); return 0; }
#endif
            var root = Path.GetFullPath(Path.Combine("artifacts", "web-checks", Guid.NewGuid().ToString("N")));
            var options = Options(root, Path.Combine(root, "site"));
            options.DataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(options.SiteDirectory);
            using var runtime = new WebRuntime(options, Scheduler, new StateBackend());
            var session = await runtime.HandleAsync(Request("GET", "/api/session"));
            Assert(session.Status == 200, "anonymous session created");
            string cookie = session.Headers["Set-Cookie"].Split(';')[0].Split('=')[1], csrf = (string)Read(session)["csrf"];
            Assert(session.Headers["Set-Cookie"].Contains("HttpOnly") && session.Headers["Set-Cookie"].Contains("SameSite=Strict") && session.Headers["Set-Cookie"].Contains("Secure"), "HTTPS cookie protections");
            var other = await runtime.HandleAsync(Request("GET", "/api/session"));
            string otherCookie = other.Headers["Set-Cookie"].Split(';')[0].Split('=')[1];
            Assert(cookie != otherCookie, "browser sessions isolated");
            async Task<WebResponseData> Send(string method, string path, string tab = "test-tab-001", object? values = null, string? address = null, string? token = null)
            {
                var request = Request(method, path); request.Cookie = cookie; request.Csrf = token ?? csrf;
                if (address != null) request.RemoteAddress = address;
                request.Body = Json.Serialize(new { tabId = tab, args = values ?? new { } });
                return await runtime.HandleAsync(request);
            }
            Assert((await Send("POST", "/api/query", token: "bad")).Status == 403, "CSRF rejection");
            var origin = Request("POST", "/api/query"); origin.Cookie = cookie; origin.Csrf = csrf; origin.Origin = "https://attacker.invalid";
            Assert((await runtime.HandleAsync(origin)).Status == 403, "cross-origin rejection");
            Assert((await Send("POST", "/api/saveSettings")).Status == 404, "maintenance unavailable on public route");
            Assert((await Send("POST", "/api/exportSql")).Status == 404, "SQL export unavailable on public route");
            Assert((await Send("POST", "/api/admin/exportSql", address: "192.0.2.50")).Status == 403, "SQL export denied to non-maintenance address");
            Assert((await Send("POST", "/api/admin/settings", address: "192.0.2.50")).Status == 403, "maintenance denied to non-allowlisted IP");
            Assert((await Send("POST", "/api/demo")).Status == 404, "production demo unavailable");
            Assert((await Send("GET", "/../Web.config")).Status == 404, "path traversal rejected");
            Assert((await Send("GET", "/Web.config")).Status == 404, "configuration never served");
            Assert((await Send("GET", "/api/query")).Status == 405, "GET cannot submit work");
            var catalogStatus = Read(await Send("GET", "/api/catalog-status"));
            Assert((string)catalogStatus["version"] == "state-version" && catalogStatus.Count == 1, "catalog status exposes only version");
            Assert((await runtime.HandleAsync(Request("GET", "/api/catalog-status"))).Status == 401, "catalog status requires existing session");
            var submitted = await Send("POST", "/api/query");
            var jobId = (string)Read(submitted)["jobId"];
            Assert(submitted.Status == 202, "query accepted as background task");
            var theft = Request("GET", "/api/tasks/" + jobId); theft.Cookie = otherCookie;
            Assert((await runtime.HandleAsync(theft)).Status == 404, "cross-session task lookup rejected");
            var completed = await Poll(runtime, cookie, jobId);
            Assert((string)completed["state"] == "succeeded", "task status completed");
            var firstOwner = (string)((Dictionary<string, object>)completed["data"])["owner"];
            var second = await Send("POST", "/api/query", "test-tab-002");
            var secondDone = await Poll(runtime, cookie, (string)Read(second)["jobId"]);
            Assert(firstOwner != (string)((Dictionary<string, object>)secondDone["data"])["owner"], "tabs own independent contexts");
            var blocking = await Send("POST", "/api/query", "test-tab-003", new { wait = true });
            var cancelId = (string)Read(blocking)["jobId"];
            var gate = typeof(WebRuntime).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            lock (gate)
            {
                var sessions = (IDictionary)typeof(WebRuntime).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
                var currentSession = sessions[cookie]!;
                var tabs = (IDictionary)currentSession.GetType().GetField("Tabs")!.GetValue(currentSession)!;
                var activeTab = tabs["test-tab-003"]!;
                activeTab.GetType().GetField("Seen")!.SetValue(activeTab, DateTime.UtcNow.AddHours(-2));
            }
            Assert((await Send("GET", "/api/catalog-status")).Status == 200, "catalog polling available during active task");
            Assert((await Send("POST", "/api/page", "test-tab-003")).Status == 409, "busy tab cannot mutate its result");
            var busyRelease = (Dictionary<string, object>)Read(await Send("POST", "/api/tab-lifecycle", "test-tab-003", new { action = "release" }))["data"];
            Assert((bool)busyRelease["alive"] && (bool)busyRelease["busy"] && !(bool)busyRelease["released"], "idle pruning and lifecycle release preserve active job");
            Assert((await Send("DELETE", "/api/tasks/" + cancelId)).Status == 200, "cancel accepted");
            Assert((string)(await Poll(runtime, cookie, cancelId))["state"] == "cancelled", "cancel waits for actual execution completion");
            var exported = await Send("POST", "/api/export", "export-tab-01");
            var exportId = (string)Read(exported)["jobId"];
            Assert((string)(await Poll(runtime, cookie, exportId))["state"] == "succeeded", "export task completes before download");
            Assert((await Send("GET", "/api/downloads/" + exportId)).Status == 200, "owner may download completed bytes");
            var stealDownload = Request("GET", "/api/downloads/" + exportId); stealDownload.Cookie = otherCookie;
            Assert((await runtime.HandleAsync(stealDownload)).Status == 404, "cross-session download rejected");
            Assert((await Send("DELETE", "/api/tasks/" + exportId, token: "bad")).Status == 403, "cancel requires CSRF");
            var sqlExport = await Send("POST", "/api/admin/exportSql", "sql-export-01");
            var sqlExportId = (string)Read(sqlExport)["jobId"];
            Assert((string)(await Poll(runtime, cookie, sqlExportId))["state"] == "succeeded", "SQL export task completes");
            var sqlDownload = await Send("GET", "/api/downloads/" + sqlExportId);
            Assert(sqlDownload.Status == 200 && sqlDownload.ContentType == "text/plain; charset=utf-8" && sqlDownload.Headers["Content-Disposition"].Contains(".txt"), "SQL download is a text attachment");
            Assert(sqlDownload.Headers["Content-Disposition"] == "attachment; filename=ReportDesk-SQL.txt", "full export keeps generic filename");
            async Task<string> SqlFileHeader(string reportId)
            {
                var request = await Send("POST", "/api/admin/exportSql", "sql-name-" + reportId, new { reportId, name = "untrusted-name" });
                var id = (string)Read(request)["jobId"];
                Assert((string)(await Poll(runtime, cookie, id))["state"] == "succeeded", "named SQL export completes " + reportId);
                return (await Send("GET", "/api/downloads/" + id)).Headers["Content-Disposition"];
            }
            var chinese = await SqlFileHeader("single");
            Assert(chinese.StartsWith("attachment; filename=ReportDesk-SQL.txt; filename*=UTF-8''", StringComparison.Ordinal) && Uri.UnescapeDataString(chinese.Substring(chinese.IndexOf("UTF-8''", StringComparison.Ordinal) + 7)) == "门诊报表.txt", "single SQL filename uses backend report name and UTF-8");
            var unsafeName = await SqlFileHeader("unsafe");
            Assert(!unsafeName.Contains("\r") && !unsafeName.Contains("\n") && Uri.UnescapeDataString(unsafeName.Substring(unsafeName.IndexOf("UTF-8''", StringComparison.Ordinal) + 7)) == "报表___________.txt", "unsafe filename characters cannot inject headers or paths");
            var reservedName = await SqlFileHeader("reserved");
            Assert(Uri.UnescapeDataString(reservedName.Substring(reservedName.IndexOf("UTF-8''", StringComparison.Ordinal) + 7)) == "_CON.txt", "reserved Windows filename is escaped");
            Assert((await Send("GET", "/api/downloads/" + sqlExportId, address: "192.0.2.50")).Status == 404, "SQL download retains maintenance address check");
            var stealSql = Request("GET", "/api/downloads/" + sqlExportId); stealSql.Cookie = otherCookie;
            Assert((await runtime.HandleAsync(stealSql)).Status == 404, "SQL download retains session isolation");
            var adminJob = await Send("POST", "/api/admin/settings", "admin-tab-01");
            var adminId = (string)Read(adminJob)["jobId"];
            await Poll(runtime, cookie, adminId);
            Assert((await Send("GET", "/api/tasks/" + adminId, address: "192.0.2.50")).Status == 404, "maintenance task cannot follow a session to an unapproved IP");
            Assert((await Send("GET", "/admin", address: "192.0.2.50")).Status == 403, "maintenance page denied server-side");
            var failureJob = await Send("POST", "/api/query", "failure-tab-01", new { fail = true });
            var failed = await Poll(runtime, cookie, (string)Read(failureJob)["jobId"]);
            Assert((string)failed["state"] == "failed" && !Json.Serialize(failed).Contains("INTERNAL_TEST_MARKER"), "raw exception details never reach the browser");
            var malformed = Request("POST", "/api/query"); malformed.Cookie = cookie; malformed.Csrf = csrf; malformed.Body = "{";
            Assert((await runtime.HandleAsync(malformed)).Status == 400, "malformed JSON safely rejected");
            var virtualSession = Request("GET", "/api/session"); virtualSession.BasePath = "/Reports/";
            Assert((await runtime.HandleAsync(virtualSession)).Headers["Set-Cookie"].Contains("Path=/Reports/"), "cookie scoped to IIS virtual application");
            bool rejected = false; try { options.ResolveReportPath("../secret.xml"); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "report root containment");
            Directory.CreateDirectory(Path.Combine(options.SiteDirectory, "UI"));
            File.WriteAllText(Path.Combine(options.SiteDirectory, "UI", "index.html"), "<html>ok</html>");
            File.WriteAllText(Path.Combine(options.SiteDirectory, "UI", "web.css"), "body{}");
            var open = Options(Path.Combine(root, "open"), options.SiteDirectory); open.MaintenanceAddresses = Array.Empty<string>();
            using var openRuntime = new WebRuntime(open, Scheduler, new StateBackend());
            WebRequestData HttpReq(string method, string path, string ip)
            {
                var request = Request(method, path); request.Secure = false; request.RemoteAddress = ip;
                request.Authority = "http://reportdesk.test"; request.Origin = "http://reportdesk.test"; return request;
            }
            var remoteHttp = await openRuntime.HandleAsync(HttpReq("GET", "/api/session", "192.0.2.8"));
            Assert(remoteHttp.Status == 200, "remote HTTP session allowed");
            Assert((bool)Read(remoteHttp)["admin"] == true, "empty maintenance list allows all functions");
            Assert(remoteHttp.Headers["Set-Cookie"].Contains("HttpOnly") && remoteHttp.Headers["Set-Cookie"].Contains("SameSite=Strict") &&
                !remoteHttp.Headers["Set-Cookie"].Split(';').Any(part => part.Trim().Equals("Secure", StringComparison.OrdinalIgnoreCase)), "HTTP session cookie omits Secure");
            Assert((await openRuntime.HandleAsync(HttpReq("GET", "/", "192.0.2.8"))).Status == 200, "remote HTTP page");
            Assert((await openRuntime.HandleAsync(HttpReq("GET", "/assets/web.css", "192.0.2.8"))).Status == 200, "remote HTTP static asset");
            Assert((await openRuntime.HandleAsync(HttpReq("GET", "/admin", "192.0.2.8"))).Status == 200, "remote HTTP maintenance page");
            string httpCookie = remoteHttp.Headers["Set-Cookie"].Split(';')[0].Split('=')[1], httpCsrf = (string)Read(remoteHttp)["csrf"];
            async Task<WebResponseData> OpenSend(string method, string path, string tab, string? token = null)
            {
                var request = HttpReq(method, path, "192.0.2.8"); request.Cookie = httpCookie; request.Csrf = token ?? httpCsrf;
                request.Body = Json.Serialize(new { tabId = tab, args = new { } }); return await openRuntime.HandleAsync(request);
            }
            Assert((await OpenSend("POST", "/api/admin/settings", "open-admin-01")).Status == 202, "remote HTTP may use maintenance API");
            Assert((await OpenSend("POST", "/api/query", "open-query-01")).Status == 202, "remote HTTP may use query API");
            Assert((await OpenSend("POST", "/api/query", "open-query-02", "bad")).Status == 403, "HTTP CSRF still required");
            var httpOrigin = HttpReq("POST", "/api/query", "192.0.2.8"); httpOrigin.Cookie = httpCookie; httpOrigin.Csrf = httpCsrf;
            httpOrigin.Origin = "http://attacker.invalid"; httpOrigin.Body = Json.Serialize(new { tabId = "test-tab-001", args = new { } });
            Assert((await openRuntime.HandleAsync(httpOrigin)).Status == 403, "HTTP origin check retained");
            var browseOpts = Options(Path.Combine(root, "browse-real"), options.SiteDirectory);
            browseOpts.MaintenanceAddresses = Array.Empty<string>();
            using var browseRuntime = new WebRuntime(browseOpts, Scheduler);
            Directory.CreateDirectory(Path.Combine(browseOpts.DataDirectory, "Reports", "科室"));
            File.WriteAllText(Path.Combine(browseOpts.DataDirectory, "Reports", "sample.xml"), "<ReportQueryInfo/>");
            var browseSession = await browseRuntime.HandleAsync(HttpReq("GET", "/api/session", "192.0.2.8"));
            string browseCookie = browseSession.Headers["Set-Cookie"].Split(';')[0].Split('=')[1], browseCsrf = (string)Read(browseSession)["csrf"];
            async Task<WebResponseData> Browse(string relative)
            {
                var request = HttpReq("POST", "/api/admin/browseReports", "192.0.2.8");
                request.Cookie = browseCookie; request.Csrf = browseCsrf;
                request.Body = Json.Serialize(new { tabId = "open-browse-01", args = new { path = relative } });
                return await browseRuntime.HandleAsync(request);
            }
            var listing = Read(await Browse(""));
            Assert((bool)listing["ok"] && listing["data"] is Dictionary<string, object>, "browse reports directory");
            var data = (Dictionary<string, object>)listing["data"];
            var names = ((System.Collections.IEnumerable)data["files"]).Cast<object>().OfType<Dictionary<string, object>>().Select(item => (string)item["name"]).ToArray();
            Assert(names.Contains("sample.xml"), "browse lists XML");
            Assert((await Browse("..")).Status == 400, "browse rejects parent path");
            Assert((await Browse("C:\\Windows")).Status == 400, "browse rejects absolute path");
            async Task<WebResponseData> Upload(object payload)
            {
                var request = HttpReq("POST", "/api/admin/uploadImport", "192.0.2.8");
                request.Cookie = browseCookie; request.Csrf = browseCsrf;
                request.Body = Json.Serialize(new { tabId = "open-upload-01", args = payload });
                return await browseRuntime.HandleAsync(request);
            }
            var uploaded = await Upload(new { folder = false, files = new[] { new { path = "picked.xml", text = "<ReportQueryInfo/>" } } });
            var uploadedBody = Read(uploaded);
            Assert(uploaded.Status == 200 && (bool)uploadedBody["ok"] && (string)((Dictionary<string, object>)uploadedBody["data"])["path"] == "picked.xml", "upload returns relative path");
            Assert(File.Exists(Path.Combine(browseOpts.DataDirectory, "Reports", "picked.xml")), "upload writes into Reports");
            Assert((await Upload(new { folder = false, files = new[] { new { path = "../escape.xml", text = "<x/>" } } })).Status == 400, "upload rejects parent path");
            var customRoot = Path.Combine(root, "server-lib"); Directory.CreateDirectory(customRoot);
            var custom = Options(Path.Combine(root, "custom-data"), options.SiteDirectory); custom.ReportsDirectory = customRoot;
            using (var customRuntime = new WebRuntime(custom, Scheduler))
            {
                var customSession = Read(await customRuntime.HandleAsync(Request("GET", "/api/session")));
                Assert((string)customSession["reportsDirectory"] == customRoot, "maintenance sees explicit LIB root");
                var publicRequest = Request("GET", "/api/session"); publicRequest.RemoteAddress = "192.0.2.99";
                Assert(Read(await customRuntime.HandleAsync(publicRequest))["reportsDirectory"] == null, "public session hides server root");
                Assert(!Directory.Exists(Path.Combine(custom.DataDirectory, "Reports")), "explicit root does not create legacy Reports directory");
                Assert(Directory.GetDirectories(Path.Combine(custom.DataDirectory, "SyncState")).Length == 1, "explicit root has isolated synchronization state");
            }
            foreach (var invalid in new[] { "relative-lib", Path.GetPathRoot(root)!, options.SiteDirectory, Path.Combine(custom.DataDirectory, "config"), custom.DataDirectory, Path.Combine(root, "missing-lib") })
            {
                var invalidOptions = Options(custom.DataDirectory, options.SiteDirectory); invalidOptions.ReportsDirectory = invalid;
                bool invalidRejected = false; try { invalidOptions.Validate(); } catch (InvalidOperationException) { invalidRejected = true; }
                Assert(invalidRejected, "invalid or missing explicit report root rejected: " + Path.GetFileName(invalid));
            }
            Console.WriteLine("HTTP/security state checks: " + checks + ". No Oracle connection or patient-result fixture.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static WebOptions Options(string data, string site) => new() { DataDirectory = data, SiteDirectory = site, Offline = true, ServerReportsEnabled = true,
        MaintenanceAddresses = new[] { "127.0.0.1", "::1" }, AllowLoopbackHttp = true };
    private static WebRequestData Request(string method, string path) => new() { Method = method, Path = path, Secure = true,
        RemoteAddress = "127.0.0.1", Authority = "https://reportdesk.test", Origin = "https://reportdesk.test", ContentType = "application/json" };
    private static async Task<Dictionary<string, object>> Poll(WebRuntime runtime, string cookie, string id, int attempts = 300)
    {
        for (int i = 0; i < attempts; i++)
        {
            var request = Request("GET", "/api/tasks/" + id); request.Cookie = cookie;
            var data = Read(await runtime.HandleAsync(request));
            if ((string)data["state"] != "running") return data;
            await Task.Delay(10);
        }
        throw new Exception("Task did not terminate during test.");
    }

    // State-only substitute tests HTTP ownership/cancellation. It is not an Oracle/result simulator.
    private sealed class StateBackend : IWebBackend
    {
        public string CatalogVersion => "state-version";
        private sealed class State : IDisposable { public string Owner = Guid.NewGuid().ToString("N"); public void Dispose() { } }
        public IDisposable CreateContext() => new State();
        public object Call(IDisposable context, string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
        { if (args.ContainsKey("fail")) throw new InvalidOperationException("INTERNAL_TEST_MARKER"); if (args.ContainsKey("wait")) { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); } return new { owner = ((State)context).Owner }; }
        public byte[] Export(IDisposable context, Dictionary<string, object> args, CancellationToken token) => Array.Empty<byte>();
        public SqlExportResult ExportSql(IDisposable context, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
        {
            var id = args.TryGetValue("reportId", out var raw) ? Convert.ToString(raw) : "";
            var name = id == "single" ? "门诊报表" : id == "unsafe" ? "报表\r\n\\/:*?\"<>|" : id == "reserved" ? "CON" : "";
            return new SqlExportResult(Encoding.UTF8.GetBytes("SQL_CHECK"), name);
        }
        public object SyncStatus() => new { enabled = false };
        public object Synchronize(CancellationToken token, Action<string> progress) => SyncStatus();
        public object BrowseReports(string relative) => new { path = relative ?? "", parent = "", folders = Array.Empty<object>(), files = Array.Empty<object>() };
        public object UploadImport(Dictionary<string, object> args) => new { path = "uploaded.xml" };
        public void Dispose() { }
    }

#if !REPORTDESK_LEGACY_CORE
    private static async Task CheckContext(string lib)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "web-context-checks", Guid.NewGuid().ToString("N")));
        var reports = Path.Combine(root, "reports"); Directory.CreateDirectory(reports);
        var names = new[] { "各科室门诊人次统计查询设置.xml", "住院各科有效收入统计查询设置.xml" };
        var hashes = new Dictionary<string, byte[]>();
        foreach (var name in names) { var source = Path.Combine(lib, "Config", "Xml", name); hashes[source] = File.ReadAllBytes(source); File.WriteAllBytes(Path.Combine(reports, name), hashes[source]); }
        var options = Options(Path.Combine(root, "data"), Path.Combine(root, "site")); Directory.CreateDirectory(options.SiteDirectory); options.ReportsDirectory = reports;
        using var runtime = new WebRuntime(options, Scheduler);
        var session = await runtime.HandleAsync(Request("GET", "/api/session"));
        var cookie = session.Headers["Set-Cookie"].Split(';')[0].Split('=')[1];
        async Task<Dictionary<string, object>> Call(string method, object args)
        {
            var request = Request("POST", "/api/" + method); request.Cookie = cookie; request.Csrf = (string)Read(session)["csrf"];
            request.Body = Json.Serialize(new { tabId = "context-check", args });
            var submitted = await runtime.HandleAsync(request); Assert(submitted.Status == 202, "context request accepted: " + method);
            return await Poll(runtime, cookie, (string)Read(submitted)["jobId"], 6000);
        }
        foreach (var name in names)
        {
            var report = ReportImporter.ImportFile(Path.Combine(reports, name))!;
            var current = report.Parameters.Single(p => ParameterOptions.IsCurrentDepartment(p));
            var optionSql = ParameterOptions.LookupSql(current);
            Assert(SqlTemplate.Compile(optionSql).RequiredNames.Count == 0 && optionSql.Contains("COM_BD_DEPARTMENT") && optionSql.Contains("VALID_FLAG = '1'"), "context dictionary requires no login context");
            var lower = new ParameterDefinition { Name = "currentDeptID", Implicit = true };
            Assert(ParameterOptions.IsCurrentDepartment(lower) && !ParameterOptions.IsCurrentDepartment(new ParameterDefinition { Name = "CurrentOperID", Implicit = true }) && !ParameterOptions.IsCurrentDepartment(new ParameterDefinition { Name = "CurrentDeptID" }), "only implicit department context recognized case-insensitively");
            var selected = await Call("select", new { reportId = report.Id }); Assert((string)selected["state"] == "succeeded", "real report metadata available");
            var detail = (Dictionary<string, object>)selected["data"];
            var parameters = ((System.Collections.IEnumerable)detail["parameters"]).Cast<Dictionary<string, object>>().ToArray();
            Assert((string)parameters[0]["name"] == current.Name && (string)parameters[0]["kind"] == "ComboBoxType" && (bool)parameters[0]["lookup"] && (bool)parameters[0]["contextDepartment"] && !(bool)parameters[0]["hasAll"], "current department first, single picker, no ALL default");
            var lookup = await Call("lookup", new { reportId = report.Id, parameter = current.Name, definitionHash = report.SourceHash, values = new { } });
            Assert((string)lookup["state"] == "failed" && ((string)lookup["error"]).Contains("离线"), "department picker reaches offline guard without needing itself");
            if (name.StartsWith("各科室", StringComparison.Ordinal))
            {
                foreach (var target in new[] { "dtDeptId", "deptid" })
                {
                    var field = parameters.Single(p => (string)p["name"] == target);
                    Assert(((System.Collections.IEnumerable)field["lookupDependencies"]).Cast<string>().Contains(current.Name), "real dependent option declares CurrentDeptID: " + target);
                    var missing = await Call("lookup", new { reportId = report.Id, parameter = target, definitionHash = report.SourceHash, values = new Dictionary<string, object> { [current.Name] = "" } });
                    Assert((string)missing["state"] == "failed" && ((string)missing["error"]).Contains(current.Name), "missing context names the required field");
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [current.Name] = "OFFLINE-USER-SELECTION" };
                    var bound = SqlTemplate.Compile(report.Parameters.Single(p => p.Name == target).LookupSql, values);
                    Assert(bound.Values.Values.Contains(values[current.Name]) && !bound.Sql.Contains(values[current.Name]) && bound.Sql.Contains("TARGET_DEPT") && bound.Sql.Contains("TARGET_DEPT_ID"), "original dependent option restrictions and bound values preserved");
                    var supplied = await Call("lookup", new { reportId = report.Id, parameter = target, definitionHash = report.SourceHash, values });
                    Assert((string)supplied["state"] == "failed" && ((string)supplied["error"]).Contains("离线"), "selected context proceeds to offline database guard");
                }
                File.WriteAllText(Path.Combine(root, "context-metadata.json"), Json.Serialize(detail), new UTF8Encoding(false));
            }
        }
        Assert(hashes.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "source LIB unchanged");
        Console.WriteLine("Context evidence: " + root + "; Oracle NOT RUN.");
    }

#endif
    // Pass an isolated copy of the real LIB: production startup creates directory lock files.
    private static async Task CheckStartup(string site, string data, string reports, int expectedCount)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToBase64String(sha.ComputeHash(stream)); }
        Console.WriteLine("Hashing isolated LIB XML before full startup...");
        var hashes = Directory.EnumerateFiles(reports, "*.xml", SearchOption.AllDirectories).ToDictionary(p => p, Hash);
        Console.WriteLine("Captured " + hashes.Count + " XML hashes.");
        for (int pass = 0; pass < 2; pass++)
        {
            var options = Options(Path.GetFullPath(data), Path.GetFullPath(site)); options.ReportsDirectory = Path.GetFullPath(reports);
            using var runtime = new WebRuntime(options, Scheduler);
            Assert((await runtime.HandleAsync(Request("GET", "/"))).Status == 200, "real LIB startup serves page, pass " + pass);
            var session = await runtime.HandleAsync(Request("GET", "/api/session"));
            var cookie = session.Headers["Set-Cookie"].Split(';')[0].Split('=')[1];
            async Task<Dictionary<string, object>> Call(string path)
            {
                var request = Request("POST", path); request.Cookie = cookie; request.Csrf = (string)Read(session)["csrf"];
                request.Body = Json.Serialize(new { tabId = "startup-check", args = new { } });
                var submitted = await runtime.HandleAsync(request); Assert(submitted.Status == 202, "startup request accepted: " + path);
                var result = await Poll(runtime, cookie, (string)Read(submitted)["jobId"], attempts: 6000);
                Assert((string)result["state"] == "succeeded", "startup task succeeds: " + path); return result;
            }
            var list = (System.Collections.ICollection)(await Call("/api/list"))["data"];
            Assert(list.Count == expectedCount, "real LIB catalog count " + list.Count);
            var syncStatus = (Dictionary<string, object>)(await Call("/api/admin/syncStatus"))["data"];
            Assert(!(bool)syncStatus["enabled"] && (bool)syncStatus["offline"], "offline startup cannot download");
        }
        Assert(hashes.All(p => Hash(p.Key) == p.Value), "all " + hashes.Count + " real XML hashes unchanged after startup and restart");
        Console.WriteLine("Full BackendAdapter startup and restart passed. Oracle/IIS NOT RUN.");
    }

    // Loopback-only offline preview shares the production dispatcher, not IIS hosting.
    private static async Task Serve(string site, string data, string reports)
    {
        var options = Options(Path.GetFullPath(data), Path.GetFullPath(site));
        options.ServerReportsEnabled = false;
        options.ReportsDirectory = reports;
        using var runtime = new WebRuntime(options, Scheduler);
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:18765/"); listener.Start();
        runtime.StartBackground();
        Console.WriteLine("OFFLINE preview http://localhost:18765/ and /admin (not IIS; no Oracle).");
        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(async () => {
                try
                {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                    var response = await runtime.HandleAsync(new WebRequestData {
                        Method = context.Request.HttpMethod, Path = context.Request.Url.AbsolutePath, Authority = context.Request.Url.GetLeftPart(UriPartial.Authority),
                        Origin = context.Request.Headers["Origin"] ?? "", RemoteAddress = context.Request.RemoteEndPoint.Address.ToString(), Secure = false,
                        Body = body, ContentType = context.Request.ContentType ?? "", Cookie = context.Request.Cookies[WebRuntime.CookieName]?.Value ?? "",
                        Csrf = context.Request.Headers["X-ReportDesk-CSRF"] ?? ""
                    });
                    context.Response.StatusCode = response.Status; context.Response.ContentType = response.ContentType;
                    foreach (var pair in response.Headers) context.Response.Headers[pair.Key] = pair.Value;
                    context.Response.ContentLength64 = response.Body.Length;
                    await context.Response.OutputStream.WriteAsync(response.Body, 0, response.Body.Length);
                }
                catch { context.Response.StatusCode = 500; }
                finally { context.Response.Close(); }
            });
        }
    }
}
