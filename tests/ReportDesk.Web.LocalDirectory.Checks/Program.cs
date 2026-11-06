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
using ReportDesk.Core;
using ReportDesk.Web;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static int checks;
    private const string QueryName = "抗菌药物消耗情况查询设置.xml";
    private const string LayoutName = "抗菌药物消耗情况报表设置.xml";
    private const string QueryPath = "Config/Xml/" + QueryName;
    private const string LayoutPath = "Config/Xml/" + LayoutName;

    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    private static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static Dictionary<string, object> Object(object value) => (Dictionary<string, object>)value;
    private static Dictionary<string, object> Read(WebResponseData response) => Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(response.Body));
    private static Dictionary<string, object>[] Items(object value) => ((IEnumerable)value).Cast<object>().Select(Object).ToArray();
    private static WebRequestData Request(string method, string path) => new() { Method = method, Path = path,
        Secure = true, RemoteAddress = "127.0.0.1", Authority = "https://local-directory.test", Origin = "https://local-directory.test", ContentType = "application/json" };

    private sealed class Client
    {
        private readonly WebRuntime runtime;
        public readonly string Cookie, Csrf, Tab;
        public Client(WebRuntime runtime, string cookie, string csrf, string tab)
        { this.runtime = runtime; Cookie = cookie; Csrf = csrf; Tab = tab; }
        public Client OtherTab(string tab) => new(runtime, Cookie, Csrf, tab);
        public async Task<WebResponseData> Submit(string method, object? args = null, bool admin = true, bool secure = true, string? csrf = null, string? origin = null, string? address = null, string? authority = null, bool localExpected = false)
        {
            var request = Request("POST", (admin ? "/api/admin/" : "/api/") + method);
            request.Cookie = Cookie; request.Csrf = csrf ?? Csrf; request.Secure = secure;
            if (address != null) request.RemoteAddress = address;
            if (authority != null) { request.Authority = authority; request.Origin = authority; }
            if (origin != null) request.Origin = origin;
            request.Body = Json.Serialize(new { tabId = Tab, localExpected, args = args ?? new { } });
            return await runtime.HandleAsync(request);
        }
        public Task<WebResponseData> Lifecycle(string action, string? csrf = null, string? address = null)
            => Submit("tab-lifecycle", new { action }, admin: false, csrf: csrf, address: address);
        public async Task<Dictionary<string, object>> Result(string method, object? args = null, bool admin = true)
        {
            var submitted = await Submit(method, args, admin);
            return await Complete(submitted, method);
        }
        public async Task<Dictionary<string, object>> Complete(WebResponseData submitted, string method)
        {
            if (submitted.Status != 202) throw new Exception(method + " unexpected HTTP " + submitted.Status + " " + Encoding.UTF8.GetString(submitted.Body));
            var id = (string)Read(submitted)["jobId"];
            for (int i = 0; i < 3000; i++)
            {
                var poll = Request("GET", "/api/tasks/" + id); poll.Cookie = Cookie;
                var response = Read(await runtime.HandleAsync(poll));
                if ((string)response["state"] != "running") return response;
                await Task.Delay(10);
            }
            throw new Exception("Timed out: " + method);
        }
        public async Task<Dictionary<string, object>> Call(string method, object? args = null, bool admin = true)
        {
            var result = await Result(method, args, admin);
            if ((string)result["state"] != "succeeded") throw new Exception(method + " failed: " + result["error"]);
            return Object(result["data"]);
        }
        public async Task Fail(string method, object? args, string name, bool admin = true)
        { var result = await Result(method, args, admin); Check((string)result["state"] == "failed", name); }
        public async Task<string> ReportId()
        { var bootstrap = await Call("bootstrap", admin: false); return (string)Items(bootstrap["reports"]).Single()["id"]; }
        public async Task Upload(string path, byte[] bytes, int chunk = 1024 * 1024)
        {
            for (int offset = 0; offset < bytes.Length; offset += chunk)
                await Call("localUpload", new { path, offset, total = bytes.Length, content = Convert.ToBase64String(bytes, offset, Math.Min(chunk, bytes.Length - offset)) });
        }
        public async Task<string> Activate(byte[] query, byte[] layout)
        {
            await Call("localBegin", new { name = "LIB副本" });
            await Upload(QueryPath, query, 1024);
            await Upload(LayoutPath, layout, 1024);
            await Call("localActivate");
            return await ReportId();
        }
    }

    private static async Task<Client> Connect(WebRuntime runtime, string tab)
    {
        var response = await runtime.HandleAsync(Request("GET", "/api/session"));
        Check(response.Status == 200, "open HTTPS session " + tab);
        return new Client(runtime, response.Headers["Set-Cookie"].Split(';')[0].Split('=')[1], (string)Read(response)["csrf"], tab);
    }
    private static object Confirm(Dictionary<string, object> candidate) => new {
        saveId = (string)candidate["saveId"],
        files = Items(candidate["files"]).Select(f => new { path = (string)f["path"], hash = Hash(Convert.FromBase64String((string)f["content"])) }).ToArray()
    };
    private static string ChangedXml(string text, string marker) => text.Replace("</ReportQueryInfo>", "<!-- " + marker + " -->\r\n</ReportQueryInfo>");
    private static string FindReportFile(string dataRoot, string reportId) => Directory.EnumerateFiles(Path.Combine(dataRoot, "LocalWorkspaces"), QueryName, SearchOption.AllDirectories)
        .Single(path => string.Equals(Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())), reportId, StringComparison.OrdinalIgnoreCase));
    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value)!;
    private static IDictionary Tabs(WebRuntime runtime, Client client) => (IDictionary)Field(((IDictionary)Field(runtime, "sessions"))[client.Cookie]!, "Tabs");
    private static void AgeTab(WebRuntime runtime, Client client)
    {
        lock (Field(runtime, "gate"))
            Tabs(runtime, client)[client.Tab]!.GetType().GetField("Seen")!.SetValue(Tabs(runtime, client)[client.Tab], DateTime.UtcNow.AddHours(-2));
    }
    private static void Sweep(WebRuntime runtime)
    {
        var backend = Field(runtime, "backend");
        backend.GetType().GetMethod("SweepLocalWorkspaces", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(backend, Array.Empty<object>());
    }

    private static async Task<int> Main(string[] args)
    {
        var preferred = @"D:\系统知识库\00_Inbox\yljhis\LIB";
        var retained = Path.GetFullPath("artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml");
        var source = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.Exists(preferred) ? preferred : retained;
        var querySource = Directory.Exists(source) ? Directory.EnumerateFiles(source, QueryName, SearchOption.AllDirectories).FirstOrDefault() : null;
        var layoutSource = Directory.Exists(source) ? Directory.EnumerateFiles(source, LayoutName, SearchOption.AllDirectories).FirstOrDefault() : null;
        if (querySource == null || layoutSource == null)
        { Console.Error.WriteLine("Required real HIS XML fixture is unavailable. No generated report fixture is substituted."); return 1; }
        var queryBytes = File.ReadAllBytes(querySource); var layoutBytes = File.ReadAllBytes(layoutSource);
        var sourceHashes = new[] { Hash(queryBytes), Hash(layoutBytes) };
        var root = Path.GetFullPath(Path.Combine("artifacts", "verification", "local-directory-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
        var reports = Path.Combine(root, "server-reports"); var data = Path.Combine(root, "data"); var site = Path.Combine(root, "site");
        Directory.CreateDirectory(reports); Directory.CreateDirectory(data); Directory.CreateDirectory(site);
        var globalQuery = Path.Combine(reports, QueryName); var globalLayout = Path.Combine(reports, LayoutName);
        File.WriteAllBytes(globalQuery, queryBytes); File.WriteAllBytes(globalLayout, layoutBytes);
        // Only a copied genuine report is edited. No database call or result row is generated.
        var originalText = FullXmlEditor.Read(globalQuery, "ReportQueryInfo").Text;
        var utf16 = new UnicodeEncoding(false, true, true);
        var secondText = ChangedXml(originalText, "local-second-copy");
        if (secondText.StartsWith("<?xml", StringComparison.Ordinal)) secondText = secondText.Substring(secondText.IndexOf("?>", StringComparison.Ordinal) + 2);
        var secondBytes = utf16.GetPreamble().Concat(utf16.GetBytes("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n" + secondText)).ToArray();
        var options = new WebOptions { SiteDirectory = site, DataDirectory = data, ReportsDirectory = reports, Offline = true, ServerReportsEnabled = true,
            MaintenanceAddresses = new[] { "127.0.0.1", "192.0.2.10" }, AllowLoopbackHttp = true };
        try
        {
            var overlapReports = Path.Combine(data, "LocalWorkspaces", "configured-report-root");
            Directory.CreateDirectory(overlapReports);
            var overlapRejected = false;
            try
            {
                using var invalid = new WebRuntime(new WebOptions { SiteDirectory = site, DataDirectory = data, ReportsDirectory = overlapReports, Offline = true, ServerReportsEnabled = true }, action => { _ = Task.Run(() => action(CancellationToken.None)); });
            }
            catch (InvalidOperationException error) { overlapRejected = error.Message.Contains("重叠"); }
            Check(overlapRejected, "configured report root cannot overlap private LocalWorkspaces storage");
            using (var runtime = new WebRuntime(options, action => { _ = Task.Run(() => action(CancellationToken.None)); }))
            {
                var first = await Connect(runtime, "local-first-tab"); var second = await Connect(runtime, "local-first-tab");
                var sibling = first.OtherTab("local-sibling-tab");
                var globalId = await sibling.ReportId();
                var settingsBefore = Json.Serialize(await first.Call("settings"));
                var lookupArgs = new { items = new[] { new { path = "目录/查询设置.xml", hash = new string('a', 64) } } };
                Check((await first.Submit("localLocations", lookupArgs, csrf: "wrong")).Status == 403, "local location lookup requires CSRF");
                Check((await first.Submit("localLocations", lookupArgs, admin: false)).Status == 404, "local location lookup unavailable on public route");
                Check((await first.Submit("localLocations", lookupArgs, address: "192.0.2.55")).Status == 403, "local location lookup requires maintenance address");
                Check((await first.Submit("localLocations", lookupArgs, secure: false, authority: "http://local-directory.test")).Status == 403, "local location lookup requires secure local origin");
                var locationJob = await first.Submit("localLocations", lookupArgs);
                Check(locationJob.Status == 202 && (string)(await first.Complete(locationJob, "location lookup"))["state"] == "succeeded", "local location lookup works before workspace activation");
                Check((await first.Submit("localBegin", new { name = "LIB" }, csrf: "wrong")).Status == 403, "local directory operations require CSRF");
                Check((await first.Submit("localBegin", new { name = "LIB" }, origin: "https://other.invalid")).Status == 403, "cross-origin local directory operation rejected");
                Check((await first.Submit("localBegin", new { name = "LIB" }, admin: false)).Status == 404, "local directory operations unavailable on public route");
                Check((await first.Submit("localBegin", new { name = "LIB" }, address: "192.0.2.55")).Status == 403, "local directory operations preserve maintenance address restrictions");
                Check((await first.Submit("localBegin", new { name = "LIB" }, secure: false, authority: "http://local-directory.test")).Status == 403, "HTTP non-loopback website cannot use local directory operations");
                Check((await first.Submit("localBegin", new { name = "LIB" }, secure: false, address: "192.0.2.10", authority: "http://localhost:8443")).Status == 403, "remote client cannot spoof loopback through Host header");
                var oldHttpSettings = await first.Complete(await first.Submit("settings", secure: false, address: "192.0.2.10", authority: "http://local-directory.test"), "HTTP settings");
                Check((string)oldHttpSettings["state"] == "succeeded", "existing HTTP database settings flow remains available");
                var loopbackRequest = await first.Submit("localBegin", new { name = "LIB" }, secure: false, authority: "http://localhost:8443");
                Check(loopbackRequest.Status == 202, "HTTP localhost test harness is allowed for actual loopback client");
                Check((string)(await first.Complete(loopbackRequest, "localhost localBegin"))["state"] == "succeeded", "localhost upload staging completes");

                var firstId = await first.Activate(queryBytes, layoutBytes);
                var secondId = await second.Activate(secondBytes, layoutBytes);
                var firstConfirmedFile = FindReportFile(data, firstId);
                var workspaces = Path.Combine(data, "LocalWorkspaces");
                var abandoned = Path.Combine(workspaces, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(abandoned);
                File.WriteAllText(Path.Combine(abandoned, "owner.kind"), "ReportDesk.LocalWorkspaces.v1");
                File.WriteAllBytes(Path.Combine(abandoned, "owner.lock"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(abandoned, QueryName), queryBytes);
                var unowned = Path.Combine(workspaces, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(unowned); File.WriteAllText(Path.Combine(unowned, "must-stay.txt"), "Not a ReportDesk-owned workspace");
                using (var overlap = new WebRuntime(options, action => { _ = Task.Run(() => action(CancellationToken.None)); }))
                {
                    Check(File.Exists(firstConfirmedFile) && Hash(File.ReadAllBytes(firstConfirmedFile)) == sourceHashes[0], "overlapping runtime preserves another live worker's private workspace");
                    Check(!Directory.Exists(abandoned), "new worker reclaims marked abandoned workspace after lease release");
                    Check(File.Exists(Path.Combine(unowned, "must-stay.txt")), "startup cleanup preserves unmarked directories");
                }
                Check(firstId != secondId && firstId != globalId, "same filenames have workspace-specific report identities");
                Check(await sibling.ReportId() == globalId, "other tab remains on original server catalog");
                Check(Json.Serialize(await first.Call("settings")) == settingsBefore, "directory selection preserves original database settings");
                Check(Items((await first.Call("bootstrap", admin: false))["reports"]).Length == 1, "template XML is not listed as an independent query");
                var relatedFiles = await first.Call("relatedFiles", new { reportId = firstId });
                Check((string)relatedFiles["root"] == "LIB副本" && (string)relatedFiles["query"] == QueryPath && !Json.Serialize(relatedFiles).Contains("LocalWorkspaces"), "related file metadata exposes local relative paths without server temporary paths");
                await first.Fail("select", new { reportId = secondId }, "other workspace report id rejected", false);
                await first.Fail("import", new { path = globalQuery, folder = false }, "local mode cannot import a server path");

                var opened = await first.Call("xmlEditorOpen", new { reportId = firstId, kind = "query" });
                var oldToken = (string)opened["token"];
                var changed = ChangedXml((string)opened["text"], "local-first-saved");
                await first.Fail("xmlEditorSave", new { reportId = firstId, token = oldToken, text = "<ReportQueryInfo>" }, "invalid XML cannot become a save candidate");
                await first.Fail("xmlEditorSave", new { reportId = firstId, token = oldToken, text = "<ReportQueryInfo/>" }, "query without report SQL cannot become a save candidate");
                opened = await first.Call("xmlEditorOpen", new { reportId = firstId, kind = "query" });
                oldToken = (string)opened["token"];
                var pending = await first.Call("xmlEditorSave", new { reportId = firstId, token = oldToken, text = changed });
                Check((bool)pending["localSave"] && Items(pending["files"]).Length == 1, "XML save prepares a local write instead of reporting saved");
                var file = Items(pending["files"]).Single();
                Check((string)file["path"] == QueryPath && string.Equals((string)file["oldHash"], sourceHashes[0], StringComparison.OrdinalIgnoreCase), "candidate uses relative path and hash of original raw bytes");
                var candidateBytes = Convert.FromBase64String((string)file["content"]);
                Check(Hash(candidateBytes) == ((string)file["newHash"]).ToLowerInvariant(), "candidate bytes match declared new hash");
                Check(Hash(File.ReadAllBytes(globalQuery)) == sourceHashes[0], "preparing local edit does not change shared server XML");
                Check(Hash(File.ReadAllBytes(firstConfirmedFile)) == sourceHashes[0], "preparing local edit leaves confirmed private disk snapshot unchanged");
                await first.Fail("select", new { reportId = firstId }, "pending local save blocks using an unconfirmed definition", false);
                await second.Fail("localCommit", Confirm(pending), "other browser session cannot acknowledge another save");
                await sibling.Fail("localCommit", Confirm(pending), "same session different tab cannot acknowledge another save");
                await first.Fail("localCommit", new { saveId = pending["saveId"], files = new[] { new { path = QueryPath, hash = new string('0', 64) } } }, "wrong saved content hash cannot commit");
                var secondOpen = await second.Call("xmlEditorOpen", new { reportId = secondId, kind = "query" });
                Check(((string)secondOpen["text"]).Contains("local-second-copy") && !((string)secondOpen["text"]).Contains("local-first-saved"), "independent workspace remains usable during pending save");

                // Simulated browser acknowledgement only, not a claim of native browser file write verification.
                var committed = await first.Call("localCommit", Confirm(pending));
                Check((bool)committed["saved"], "confirmed local save returns original successful editor response");
                Check(Hash(File.ReadAllBytes(firstConfirmedFile)) == Hash(candidateBytes), "private disk snapshot changes only after successful local acknowledgement");
                var repeated = await first.Call("localCommit", Confirm(pending));
                Check((bool)repeated["saved"], "repeated acknowledgement is idempotent after response loss");
                var committedEditor = Object(committed["editor"]);
                var noChange = await first.Call("xmlEditorSave", new { reportId = firstId, token = committedEditor["token"], text = committedEditor["text"] });
                Check(Items(noChange["files"]).Length == 0, "returned committed editor token remains valid for the next save");
                Check((bool)(await first.Call("localCommit", Confirm(noChange)))["saved"], "unchanged edit confirms without requiring any browser file write");
                var persisted = await first.Call("xmlEditorOpen", new { reportId = firstId, kind = "query" });
                Check(((string)persisted["text"]).Contains("local-first-saved"), "confirmed snapshot is visible when reopening editor");

                var pendingAbort = await first.Call("xmlEditorSave", new { reportId = firstId, token = persisted["token"], text = ChangedXml((string)persisted["text"], "must-not-commit") });
                await first.Call("localAbort", new { saveId = pendingAbort["saveId"] });
                Check((bool)(await first.Call("localStatus"))["requiresRescan"], "aborted save requires rescan to reconcile actual local disk state");
                firstId = await first.Activate(candidateBytes, layoutBytes);
                var afterAbort = await first.Call("xmlEditorOpen", new { reportId = firstId, kind = "query" });
                Check(!((string)afterAbort["text"]).Contains("must-not-commit") && ((string)afterAbort["text"]).Contains("local-first-saved"), "aborted candidate leaves confirmed snapshot intact");

                var encoded = await second.Call("xmlEditorSave", new { reportId = secondId, token = secondOpen["token"], text = ChangedXml((string)secondOpen["text"], "utf16-save") });
                var encodedFile = Items(encoded["files"]).Single();
                var encodedBytes = Convert.FromBase64String((string)encodedFile["content"]);
                Check(encodedBytes.Take(2).SequenceEqual(new byte[] { 0xff, 0xfe }) && utf16.GetString(encodedBytes, 2, encodedBytes.Length - 2).Contains("utf16-save"), "UTF-16 BOM and encoded XML are preserved through upload and prepare");
                Check(string.Equals((string)encodedFile["oldHash"], Hash(secondBytes), StringComparison.OrdinalIgnoreCase), "UTF-16 source hash covers original bytes rather than UTF-8 conversion");
                await second.Call("localCommit", Confirm(encoded));

                var sqlOpened = await first.Call("sqlEditorOpen", new { reportId = firstId });
                var sqlSource = Items(sqlOpened["sources"]).First(s => (bool)s["editable"]);
                var changedSql = (string)sqlSource["sql"] + "\n-- local-copy-sql-save";
                var sqlPending = await first.Call("sqlEditorSave", new { reportId = firstId, token = sqlOpened["token"], sourceIndex = sqlSource["index"], sql = changedSql });
                Check((bool)sqlPending["localSave"], "SQL editor uses same two-phase local save protocol");
                await first.Call("localCommit", Confirm(sqlPending));
                var sqlAfter = await first.Call("sqlEditorOpen", new { reportId = firstId });
                Check(Items(sqlAfter["sources"]).Any(s => ((string)s["sql"]).Contains("local-copy-sql-save")), "SQL edit retained in only its own confirmed report");
                var layoutOpen = await first.Call("xmlEditorOpen", new { reportId = firstId, kind = "layout" });
                var layoutPending = await first.Call("xmlEditorSave", new { reportId = firstId, token = layoutOpen["token"], text = ((string)layoutOpen["text"]).Replace("药物名称", "药品名称（本机测试）") });
                Check(Items(layoutPending["files"]).Single()["path"].Equals(LayoutPath), "related layout edit writes correct relative companion path");
                await first.Call("localCommit", Confirm(layoutPending));

                var upload = first.OtherTab("local-upload-guards");
                foreach (var path in new[] { "../outside.xml", "/absolute.xml", "C:/outside.xml", "a/../outside.xml", "file.xml:stream", "not-xml.exe" })
                {
                    await upload.Call("localBegin", new { name = "LIB" });
                    await upload.Fail("localUpload", new { path, offset = 0, total = queryBytes.Length, content = Convert.ToBase64String(queryBytes) }, "unsafe upload path rejected " + path);
                }
                await upload.Call("localBegin", new { name = "LIB" });
                await upload.Fail("localUpload", new { path = QueryPath, offset = 7, total = queryBytes.Length, content = Convert.ToBase64String(queryBytes, 0, 10) }, "out-of-order first chunk rejected");
                await upload.Call("localBegin", new { name = "LIB" });
                await upload.Call("localUpload", new { path = QueryPath, offset = 0, total = queryBytes.Length, content = Convert.ToBase64String(queryBytes, 0, 10) });
                await upload.Fail("localActivate", null, "incomplete upload cannot activate partial report library");
                await upload.Fail("bootstrap", null, "incomplete local staging does not silently fall back to server catalog", false);
                Check(await sibling.ReportId() == globalId, "incomplete staging does not disturb another tab's server catalog");
                var repeatedChunk = await upload.Call("localUpload", new { path = QueryPath, offset = 0, total = queryBytes.Length, content = Convert.ToBase64String(queryBytes, 0, 10) });
                Check(Convert.ToInt64(repeatedChunk["received"]) == 10, "identical chunk replay is idempotent after response loss");
                var wrongChunk = queryBytes.Take(10).ToArray(); wrongChunk[0] ^= 1;
                await upload.Fail("localUpload", new { path = QueryPath, offset = 0, total = queryBytes.Length, content = Convert.ToBase64String(wrongChunk) }, "changed duplicate chunk cannot corrupt staging");

                await first.Call("localUseServer");
                Check(await first.ReportId() == globalId && await second.ReportId() == secondId, "return to server mode changes only current tab");
                Check(Hash(File.ReadAllBytes(globalQuery)) == sourceHashes[0] && Hash(File.ReadAllBytes(globalLayout)) == sourceHashes[1], "all local edit paths leave original server report XML unchanged");
                Check(Json.Serialize(await first.Call("settings")) == settingsBefore, "SQL/XML saves and mode switch preserve database connection settings");
            }
            var privateRoot = Path.Combine(data, "LocalWorkspaces");
            Check(!Directory.Exists(privateRoot) || !Directory.EnumerateFiles(privateRoot, "*.xml", SearchOption.AllDirectories).Any(), "runtime disposal removes private uploaded XML workspaces");
            await LifecycleChecks(root, queryBytes, layoutBytes);
            await ActiveDisposeChecks(root, queryBytes, layoutBytes);
            Check(Hash(File.ReadAllBytes(querySource)) == sourceHashes[0] && Hash(File.ReadAllBytes(layoutSource)) == sourceHashes[1], "retained real HIS source bytes unchanged");
            File.WriteAllText(Path.Combine(root, "PASS.txt"), "PASS " + checks + " offline local-directory checks. Real source XML copied and hashes unchanged. Native browser writes, real Oracle, IIS and HIS engine NOT RUN.\r\nSource: " + source + "\r\n", Encoding.UTF8);
            Console.WriteLine("PASS " + checks + " checks; evidence: " + root);
            Console.WriteLine("Offline service protocol validation only; native browser writes/Oracle/IIS/HIS NOT RUN.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Evidence retained: " + root); return 1; }
    }

    private static async Task LifecycleChecks(string root, byte[] query, byte[] layout)
    {
        var data = Path.Combine(root, "lifecycle-data");
        var options = new WebOptions { SiteDirectory = Path.Combine(root, "lifecycle-site"), DataDirectory = data,
            ReportsDirectory = "ignored\0old-LIB", Offline = true, SyncEnabled = true, SyncSeconds = -1,
            MaxConcurrentOperations = 1, MaintenanceAddresses = new[] { "127.0.0.1" } };
        Action<Func<CancellationToken, Task>> scheduler = action => { _ = Task.Run(() => action(CancellationToken.None)); };
        var runtime = new WebRuntime(options, scheduler);
        string ownerRoot;
        try
        {
            var first = await Connect(runtime, "lifecycle-first"); var sibling = first.OtherTab("lifecycle-sibling");
            var missing = first.OtherTab("lifecycle-missing");
            var absent = Object(Read(await missing.Lifecycle("touch"))["data"]);
            Check(!(bool)absent["alive"] && Tabs(runtime, first).Count == 0, "lifecycle touch does not recreate a missing tab or context");
            Check((await first.Lifecycle("touch", csrf: "wrong")).Status == 403, "lifecycle requires CSRF");
            var firstId = await first.Activate(query, layout); var siblingId = await sibling.Activate(query, layout);
            Check(firstId != siblingId, "consecutive small upload chunks complete without busy or capacity errors with one worker");
            string firstFile = FindReportFile(data, firstId), siblingFile = FindReportFile(data, siblingId);
            ownerRoot = Directory.GetDirectories(Path.Combine(data, "LocalWorkspaces")).Single();
            Check((await first.Lifecycle("release", address: "192.0.2.55")).Status == 403 && File.Exists(firstFile), "lifecycle cannot release local workspace from unapproved address");
            var tab = Tabs(runtime, first)[first.Tab]!;
            tab.GetType().GetField("Seen")!.SetValue(tab, DateTime.UtcNow.AddMinutes(-2));
            var touched = Object(Read(await first.Lifecycle("touch"))["data"]);
            Check((bool)touched["alive"] && (bool)touched["local"] && (DateTime)Field(tab, "Seen") > DateTime.UtcNow.AddSeconds(-10), "lifecycle touch renews existing local tab activity");
            AgeTab(runtime, first);
            await sibling.Lifecycle("touch");
            Check(!File.Exists(firstFile) && File.Exists(siblingFile) && !Tabs(runtime, first).Contains(first.Tab), "idle tab expires while sibling tab and session stay active");
            Check((await first.Submit("bootstrap", admin: false, localExpected: true)).Status == 410 && !Tabs(runtime, first).Contains(first.Tab), "stale localExpected request returns 410 without server fallback or context creation");
            Check((bool)Object(Read(await sibling.Lifecycle("touch"))["data"])["alive"], "active sibling remains alive after idle tab cleanup");
            var editor = await sibling.Call("xmlEditorOpen", new { reportId = siblingId, kind = "query" });
            var pending = await sibling.Call("xmlEditorSave", new { reportId = siblingId, token = editor["token"], text = editor["text"] });
            var protectedSave = Object(Read(await sibling.Lifecycle("release"))["data"]);
            Check((bool)protectedSave["busy"] && !(bool)protectedSave["released"] && File.Exists(siblingFile), "lifecycle release preserves unconfirmed local save");
            await sibling.Call("localCommit", Confirm(pending));

            // Windows open handle prevents recursive delete; the owner queues this exact workspace for retry.
            using (var held = new FileStream(siblingFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var released = Object(Read(await sibling.Lifecycle("release"))["data"]);
                Check((bool)released["released"] && !Tabs(runtime, sibling).Contains(sibling.Tab) && File.Exists(siblingFile), "release detaches tab even when a locked XML requires cleanup retry");
                Sweep(runtime);
                Check(File.Exists(siblingFile), "sweep leaves temporarily locked retired workspace for another retry");
            }
            Sweep(runtime);
            Check(!File.Exists(siblingFile), "sweep removes retired workspace after file handle closes");

            var live = first.OtherTab("lifecycle-live-worker");
            var liveId = await live.Activate(query, layout); var liveFile = FindReportFile(data, liveId);
            var auto = first.OtherTab("lifecycle-background");
            var autoId = await auto.Activate(query, layout); var autoFile = FindReportFile(data, autoId);
            AgeTab(runtime, auto); runtime.StartBackground();
            for (int i = 0; i < 100 && File.Exists(autoFile); i++) await Task.Delay(10);
            Check(!File.Exists(autoFile) && File.Exists(liveFile), "background cleanup expires idle tab without HTTP activity or valid legacy sync settings");
            var unknown = Path.Combine(data, "LocalWorkspaces", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unmarked directory");
            var abandoned = Path.Combine(data, "LocalWorkspaces", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(abandoned); File.WriteAllText(Path.Combine(abandoned, "owner.kind"), "ReportDesk.LocalWorkspaces.v1");
            File.WriteAllBytes(Path.Combine(abandoned, "owner.lock"), Array.Empty<byte>());
            var abandonedFile = Path.Combine(abandoned, "locked.xml"); File.WriteAllBytes(abandonedFile, query);
            using (var held = new FileStream(abandonedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var otherWorker = new WebRuntime(options, scheduler);
                Check(File.Exists(liveFile) && File.Exists(Path.Combine(unknown, "keep.txt")) && File.Exists(abandonedFile), "overlap worker preserves live owner unknown directory and locked abandoned data");
            }
            Sweep(runtime);
            Check(!Directory.Exists(abandoned) && File.Exists(liveFile) && File.Exists(Path.Combine(unknown, "keep.txt")), "sweep retries abandoned owner deletion and preserves live or unknown directories");
            runtime.Dispose();
            Check(!Directory.Exists(ownerRoot) && File.Exists(Path.Combine(unknown, "keep.txt")), "runtime dispose removes owned root and preserves unknown directories");
            Check(!Directory.Exists(Path.Combine(data, "Reports")) && !Directory.Exists(Path.Combine(data, "SyncState")), "lifecycle cleanup needs no server report or synchronization directory");
        }
        finally { runtime.Dispose(); }
    }

    private static async Task ActiveDisposeChecks(string root, byte[] query, byte[] layout)
    {
        var data = Path.Combine(root, "active-dispose-data");
        var options = new WebOptions { SiteDirectory = Path.Combine(root, "active-dispose-site"), DataDirectory = data, Offline = true, MaxConcurrentOperations = 1 };
        bool hold = false;
        Func<CancellationToken, Task>? queued = null;
        var runtime = new WebRuntime(options, action => { if (hold) queued = action; else _ = Task.Run(() => action(CancellationToken.None)); });
        try
        {
            var client = await Connect(runtime, "active-dispose-tab");
            var id = await client.Activate(query, layout); var file = FindReportFile(data, id);
            var owner = Directory.GetDirectories(Path.Combine(data, "LocalWorkspaces")).Single();
            hold = true;
            Check((await client.Submit("bootstrap", admin: false, localExpected: true)).Status == 202 && queued != null, "queued operation holds real local context as active");
            AgeTab(runtime, client);
            await client.OtherTab("active-dispose-sibling").Lifecycle("touch");
            Check(Tabs(runtime, client).Contains(client.Tab) && File.Exists(file), "idle pruning preserves real local workspace while server operation is queued");
            runtime.Dispose();
            Check(File.Exists(file) && Directory.Exists(owner) && !(bool)Field(runtime, "backendDisposed"), "runtime dispose waits for active operation before releasing context and owner lease");
            var finish = queued!; queued = null; await finish(CancellationToken.None);
            Check(!Directory.Exists(owner) && (bool)Field(runtime, "backendDisposed"), "cancelled queued operation releases context and backend only after completion");
        }
        finally
        {
            runtime.Dispose();
            if (queued != null) await queued(CancellationToken.None);
        }
    }
}
