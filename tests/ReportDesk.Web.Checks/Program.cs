using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ReportDesk.Web;

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
            if (args.Length == 3 && args[0] == "--serve") { await Serve(args[1], args[2]); return 0; }
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
            Assert((await Send("POST", "/api/admin/settings", address: "192.0.2.50")).Status == 403, "maintenance denied to non-allowlisted IP");
            Assert((await Send("POST", "/api/demo")).Status == 404, "production demo unavailable");
            Assert((await Send("GET", "/../Web.config")).Status == 404, "path traversal rejected");
            Assert((await Send("GET", "/Web.config")).Status == 404, "configuration never served");
            Assert((await Send("GET", "/api/query")).Status == 405, "GET cannot submit work");
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
            Assert((await Send("POST", "/api/page", "test-tab-003")).Status == 409, "busy tab cannot mutate its result");
            Assert((await Send("DELETE", "/api/tasks/" + cancelId)).Status == 200, "cancel accepted");
            Assert((string)(await Poll(runtime, cookie, cancelId))["state"] == "cancelled", "cancel waits for actual execution completion");
            var exported = await Send("POST", "/api/export", "export-tab-01");
            var exportId = (string)Read(exported)["jobId"];
            Assert((string)(await Poll(runtime, cookie, exportId))["state"] == "succeeded", "export task completes before download");
            Assert((await Send("GET", "/api/downloads/" + exportId)).Status == 200, "owner may download completed bytes");
            var stealDownload = Request("GET", "/api/downloads/" + exportId); stealDownload.Cookie = otherCookie;
            Assert((await runtime.HandleAsync(stealDownload)).Status == 404, "cross-session download rejected");
            Assert((await Send("DELETE", "/api/tasks/" + exportId, token: "bad")).Status == 403, "cancel requires CSRF");
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
            Console.WriteLine("HTTP/security state checks: " + checks + ". No Oracle connection or patient-result fixture.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static WebOptions Options(string data, string site) => new() { DataDirectory = data, SiteDirectory = site, Offline = true,
        MaintenanceAddresses = new[] { "127.0.0.1", "::1" }, AllowLoopbackHttp = true };
    private static WebRequestData Request(string method, string path) => new() { Method = method, Path = path, Secure = true,
        RemoteAddress = "127.0.0.1", Authority = "https://reportdesk.test", Origin = "https://reportdesk.test", ContentType = "application/json" };
    private static async Task<Dictionary<string, object>> Poll(WebRuntime runtime, string cookie, string id)
    {
        for (int i = 0; i < 300; i++)
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
        private sealed class State : IDisposable { public string Owner = Guid.NewGuid().ToString("N"); public void Dispose() { } }
        public IDisposable CreateContext() => new State();
        public object Call(IDisposable context, string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
        { if (args.ContainsKey("fail")) throw new InvalidOperationException("INTERNAL_TEST_MARKER"); if (args.ContainsKey("wait")) { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); } return new { owner = ((State)context).Owner }; }
        public byte[] Export(IDisposable context, Dictionary<string, object> args, CancellationToken token) => Array.Empty<byte>();
        public object SyncStatus() => new { enabled = false };
        public object Synchronize(CancellationToken token, Action<string> progress) => SyncStatus();
        public object BrowseReports(string relative) => new { path = relative ?? "", parent = "", folders = Array.Empty<object>(), files = Array.Empty<object>() };
        public object UploadImport(Dictionary<string, object> args) => new { path = "uploaded.xml" };
        public void Dispose() { }
    }

    // Loopback-only offline preview shares the production dispatcher, not IIS hosting.
    private static async Task Serve(string site, string data)
    {
        var options = Options(Path.GetFullPath(data), Path.GetFullPath(site));
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
