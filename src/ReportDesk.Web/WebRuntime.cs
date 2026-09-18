using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReportDesk.Core;

namespace ReportDesk.Web;

public sealed class WebRuntime : IDisposable
{
    public const string CookieName = "ReportDeskSession";
    private readonly WebOptions options;
    private readonly IWebBackend backend;
    private readonly Action<Func<CancellationToken, Task>> schedule;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim workers;
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private bool disposed;
    private int backgroundStarted;
    private static readonly HashSet<string> PublicMethods = new(StringComparer.Ordinal) {
        "bootstrap", "list", "select", "definition", "relatedFiles", "query", "view", "page", "lookup", "lookupPage", "lookupAll", "clear", "export"
    };
    private static readonly HashSet<string> AdminMethods = new(StringComparer.Ordinal) {
        "settings", "saveSettings", "testConnection", "import", "browseReports", "uploadImport", "checkNewReports", "recheck", "reloadReport",
        "sqlEditorOpen", "sqlEditorCheck", "sqlEditorSave", "layoutPreview", "layoutSave", "sqlEditorReveal",
        "discoverTns", "tnsAliases", "syncStatus", "syncNow", "resolveSyncConflict", "openLogs", "relatedFiles"
    };
    private static readonly HashSet<string> Assets = new(StringComparer.Ordinal) {
        "index.html", "styles.css", "execution.css", "sql-editor.css", "query-form.js", "renderer.js", "sql-editor.js",
        "close-emblem.svg", "transport.js", "web.css", "web.js"
    };
    private sealed class Session
    {
        public readonly string Id = JsonCodec.Token(), Csrf = JsonCodec.Token();
        public DateTime Seen = DateTime.UtcNow;
        public readonly Dictionary<string, Tab> Tabs = new(StringComparer.Ordinal);
    }
    private sealed class Tab
    {
        public IDisposable? Context;
        public readonly Dictionary<string, Job> Jobs = new(StringComparer.Ordinal);
        public Job? Active;
    }
    private sealed class Job
    {
        public readonly string Id = JsonCodec.Token();
        public readonly CancellationTokenSource Cancel = new();
        public string State = "running", Error = "";
        public volatile string Message = "正在处理…";
        public bool Maintenance;
        public object? Data;
        public byte[]? Download;
    }

    public WebRuntime(WebOptions options, Action<Func<CancellationToken, Task>> schedule, IWebBackend? backend = null)
    {
        options.Validate(); this.options = options; this.schedule = schedule;
        ErrorLog.Initialize(Path.Combine(options.DataDirectory, "Logs"));
        this.backend = backend ?? new BackendAdapter(options);
        workers = new SemaphoreSlim(options.MaxConcurrentOperations);
    }

    public void StartBackground()
    {
        if (Interlocked.Exchange(ref backgroundStarted, 1) != 0) return;
        schedule(async hostToken => {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, stopping.Token);
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    if (options.SyncEnabled && !options.Offline && workers.Wait(0))
                    {
                        try { backend.Synchronize(linked.Token, _ => { }); }
                        finally { workers.Release(); }
                    }
                    lock (gate) Prune();
                    await Task.Delay(TimeSpan.FromSeconds(options.SyncSeconds), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    ErrorLog.Write("WebBackground", ex, includeMessage: false);
                    try { await Task.Delay(TimeSpan.FromSeconds(options.SyncSeconds), linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        });
    }

    public Task<WebResponseData> HandleAsync(WebRequestData request)
    {
        try { return Task.FromResult(Handle(request)); }
        catch (Exception ex)
        {
            ErrorLog.Write("WebRequest", ex, includeMessage: false);
            return Task.FromResult(WebResponseData.Error(400, "请求无效或无法处理，请检查输入后重试。"));
        }
    }

    private WebResponseData Handle(WebRequestData request)
    {
        if (disposed) return WebResponseData.Error(503, "程序正在重启，请稍后刷新；未完成的查询需要重新执行。");
        var path = request.Path;
        if (path.Contains("%") || path.Contains("\\") || path.Contains("..") || path.Contains("//")) return WebResponseData.Error(404, "路径不存在。");
        bool maintenance = options.IsMaintenance(request.RemoteAddress);
        if ((path == "/admin" || path.StartsWith("/admin/", StringComparison.Ordinal) || path.StartsWith("/api/admin/", StringComparison.Ordinal)) && !maintenance)
            return WebResponseData.Error(403, "本机不在维护访问清单中。");

        if (path == "/api/health" && request.Method == "GET") return WebResponseData.Json(new { ok = true, offline = options.Offline });
        if (path == "/api/session" && request.Method == "GET")
        {
            lock (gate)
            {
                Prune();
                if (!sessions.TryGetValue(request.Cookie, out var session))
                {
                    if (sessions.Count >= options.MaxSessions) return WebResponseData.Error(503, "当前使用会话较多，请稍后再试。");
                    session = new Session(); sessions.Add(session.Id, session);
                }
                session.Seen = DateTime.UtcNow;
                var response = WebResponseData.Json(new { csrf = session.Csrf, admin = maintenance, offline = options.Offline, idleMinutes = options.IdleMinutes });
                response.Headers["Set-Cookie"] = CookieName + "=" + session.Id + "; Path=" + SafeBase(request.BasePath) + "; HttpOnly; SameSite=Strict" + (request.Secure ? "; Secure" : "");
                return response;
            }
        }
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return Static(request);
        lock (gate)
        {
            Prune();
            if (!sessions.TryGetValue(request.Cookie, out var session)) return WebResponseData.Error(401, "会话已结束，请刷新页面后重查。");
            session.Seen = DateTime.UtcNow;
            if (request.Method != "GET" && (!JsonCodec.Equal(session.Csrf, request.Csrf) ||
                (request.Origin.Length > 0 && !string.Equals(request.Origin, request.Authority, StringComparison.OrdinalIgnoreCase))))
                return WebResponseData.Error(403, "请求校验失败，请刷新页面。");

            if (path.StartsWith("/api/tasks/", StringComparison.Ordinal) || path.StartsWith("/api/downloads/", StringComparison.Ordinal))
            {
                var id = path.Substring(path.LastIndexOf('/') + 1);
                var job = session.Tabs.Values.SelectMany(t => t.Jobs.Values).FirstOrDefault(j => j.Id == id);
                if (job == null || (job.Maintenance && !maintenance)) return WebResponseData.Error(404, "任务不存在或已释放。");
                if (path.StartsWith("/api/downloads/", StringComparison.Ordinal))
                {
                    if (request.Method != "GET") return WebResponseData.Error(405, "请求方式不支持。");
                    if (job.State != "succeeded" || job.Download == null) return WebResponseData.Error(409, "导出未完成或已释放。");
                    var download = new WebResponseData { Body = job.Download, ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
                    download.Headers["Content-Disposition"] = "attachment; filename=ReportDesk.xlsx";
                    return download;
                }
                if (request.Method == "DELETE") { if (job.State == "running") { job.Cancel.Cancel(); job.Message = "正在请求取消，请等待执行端释放资源…"; } return WebResponseData.Json(new { ok = true, data = new { } }); }
                if (request.Method != "GET") return WebResponseData.Error(405, "请求方式不支持。");
                return WebResponseData.Json(new { state = job.State, message = job.Message, data = job.Data, error = job.Error, cancelled = job.State == "cancelled" });
            }
            if (request.Method != "POST") return WebResponseData.Error(405, "请求方式不支持。");
            var adminRoute = path.StartsWith("/api/admin/", StringComparison.Ordinal);
            var method = path.Substring(adminRoute ? 11 : 5);
            if (!(adminRoute ? AdminMethods : PublicMethods).Contains(method)) return WebResponseData.Error(404, "此接口不存在。");
            if (!request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || Encoding.UTF8.GetByteCount(request.Body) > WebOptions.MaxRequestBytes)
                return WebResponseData.Error(400, "请求格式或大小无效。");
            var envelope = JsonCodec.Object(request.Body);
            if (!envelope.TryGetValue("tabId", out var rawTab) || !(rawTab is string tabId) || !Regex.IsMatch(tabId, "\\A[a-zA-Z0-9_-]{8,80}\\z"))
                return WebResponseData.Error(400, "页面任务标识无效。");
            var args = envelope.TryGetValue("args", out var rawArgs) && rawArgs is Dictionary<string, object> dict ? dict : new Dictionary<string, object>();
            if (adminRoute && method == "browseReports")
            {
                var relative = args.TryGetValue("path", out var rawPath) ? Convert.ToString(rawPath) ?? "" : "";
                try { return WebResponseData.Json(new { ok = true, data = backend.BrowseReports(relative) }); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is WebUserException)
                { return WebResponseData.Error(400, ex.Message); }
            }
            if (adminRoute && method == "uploadImport")
            {
                try { return WebResponseData.Json(new { ok = true, data = backend.UploadImport(args) }); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is WebUserException)
                { return WebResponseData.Error(400, ex.Message); }
            }
            if (!session.Tabs.TryGetValue(tabId, out var tab))
            {
                if (session.Tabs.Count >= 16) return WebResponseData.Error(429, "打开的任务页较多，请关闭并释放不用的页面。");
                tab = new Tab(); session.Tabs.Add(tabId, tab);
            }
            if (tab.Active != null) return WebResponseData.Error(409, "当前页面有操作未结束，请等待或取消。");
            if (!workers.Wait(0)) return WebResponseData.Error(429, "服务器正在处理其他操作，请稍后重试。");
            foreach (var old in tab.Jobs.Values.Where(j => j.State != "running").ToArray()) { old.Cancel.Dispose(); tab.Jobs.Remove(old.Id); }
            var next = new Job { Maintenance = adminRoute };
            tab.Active = next; tab.Jobs.Add(next.Id, next);
            try { schedule(host => Execute(session, tab, next, method, args, adminRoute, SafeBase(request.BasePath), host)); }
            catch { tab.Active = null; tab.Jobs.Remove(next.Id); next.Cancel.Dispose(); workers.Release(); throw; }
            return WebResponseData.Json(new { ok = true, jobId = next.Id }, 202);
        }
    }

    private Task Execute(Session session, Tab tab, Job job, string method, Dictionary<string, object> args, bool admin, string basePath, CancellationToken host)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(host, stopping.Token, job.Cancel.Token);
            linked.Token.ThrowIfCancellationRequested();
            // Catalog snapshots may wait for maintenance. Do not create them while
            // holding the HTTP gate, otherwise other tabs cannot poll or cancel.
            tab.Context ??= backend.CreateContext();
            linked.Token.ThrowIfCancellationRequested();
            // Backend progress may run under its catalog lock; never take the HTTP gate here.
            void Progress(string message) { job.Message = message; }
            object data;
            if (method == "export")
            {
                var bytes = backend.Export(tab.Context, args, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                lock (gate) job.Download = bytes;
                data = new { downloadUrl = basePath + "api/downloads/" + job.Id };
            }
            else if (method == "syncStatus") data = backend.SyncStatus();
            else if (method == "syncNow") data = backend.Synchronize(linked.Token, Progress);
            else if (method == "openLogs") data = ReadLog();
            else if (method == "discoverTns") data = options.TnsFiles.Where(File.Exists).ToArray();
            else
            {
                ValidateArgs(method, args);
                data = backend.Call(tab.Context, method, args, admin, linked.Token, Progress);
            }
            // A completed file/config save must not be reported as cancelled after it committed.
            lock (gate) { job.Data = data; job.State = "succeeded"; job.Message = "操作完成。"; }
        }
        catch (OperationCanceledException) { lock (gate) { job.State = "cancelled"; job.Message = "操作已取消。"; job.Download = null; } }
        catch (Exception ex)
        {
            var id = Guid.NewGuid().ToString("N");
            ErrorLog.Write("WebOperation-" + id, ex, includeMessage: false);
            // Backend wrapper returns deliberately safe messages. Never return raw Oracle/IO errors.
            var message = ex is WebUserException ? ex.Message : "操作未完成，请检查条件、连接或文件状态。关联号：" + id;
            lock (gate) { job.State = "failed"; job.Error = message; job.Message = message; job.Download = null; }
        }
        finally
        {
            lock (gate) { tab.Active = null; session.Seen = DateTime.UtcNow; if (disposed) tab.Context?.Dispose(); }
            workers.Release();
        }
        return Task.CompletedTask;
    }

    private void ValidateArgs(string method, Dictionary<string, object> args)
    {
        if (method == "import")
        {
            var relative = args.TryGetValue("path", out var value) ? Convert.ToString(value) ?? "" : "";
            args["path"] = options.ResolveReportPath(relative);
            args["folder"] = Directory.Exists((string)args["path"]);
        }
        if (method == "tnsAliases" || method == "saveSettings" || method == "testConnection")
        {
            var key = method == "tnsAliases" ? "path" : "tnsFile";
            if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
            {
                var path = Path.GetFullPath(Convert.ToString(value));
                if (!options.TnsFiles.Any(file => string.Equals(Path.GetFullPath(file), path, StringComparison.OrdinalIgnoreCase)))
                    throw new WebUserException("TNS 文件不在服务器批准清单中。");
                WebOptions.EnsureNoLinks(path);
            }
        }
    }

    private object ReadLog()
    {
        var root = Path.Combine(options.DataDirectory, "Logs");
        var files = Directory.Exists(root) ? Directory.GetFiles(root, "*.log").OrderByDescending(p => p, StringComparer.Ordinal).Take(3).ToArray() : Array.Empty<string>();
        // Fixed log directory only; no user supplied filename. No request or query data is logged.
        var text = new StringBuilder();
        foreach (var file in files)
        {
            WebOptions.EnsureNoLinks(file);
            text.AppendLine(Path.GetFileName(file));
            foreach (var line in File.ReadLines(file).Reverse().Take(100).Reverse()) text.AppendLine(line);
        }
        return new { title = "最近脱敏日志", text = text.Length > 0 ? text.ToString() : "暂无错误日志。" };
    }

    private WebResponseData Static(WebRequestData request)
    {
        if (request.Method != "GET") return WebResponseData.Error(405, "请求方式不支持。");
        var name = request.Path.TrimStart('/');
        if (name == "" || name == "admin" || name == "admin/") name = "index.html";
        if (name.StartsWith("admin/", StringComparison.Ordinal)) name = name.Substring(6);
        if (name.StartsWith("assets/", StringComparison.Ordinal)) name = name.Substring(7);
        if (!Assets.Contains(name)) return WebResponseData.Error(404, "文件不存在。");
        var path = Path.Combine(options.SiteDirectory, "UI", name);
        if (!File.Exists(path)) return WebResponseData.Error(404, "页面资源未打包。");
        WebOptions.EnsureNoLinks(path);
        var type = name.EndsWith(".js") ? "application/javascript" : name.EndsWith(".css") ? "text/css" : name.EndsWith(".svg") ? "image/svg+xml" : "text/html";
        var bytes = name == "index.html" ? Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8).Replace("\"/assets/", "\"" + SafeBase(request.BasePath) + "assets/")) : File.ReadAllBytes(path);
        return new WebResponseData { ContentType = type + "; charset=utf-8", Body = bytes };
    }
    private static string SafeBase(string value) => Regex.IsMatch(value, "\\A/[a-zA-Z0-9_/-]*\\z") && !value.Contains("//") ? value.TrimEnd('/') + "/" : "/";
    private void Prune()
    {
        foreach (var session in sessions.Values.Where(s => s.Seen < DateTime.UtcNow.AddMinutes(-options.IdleMinutes) && s.Tabs.Values.All(t => t.Active == null)).ToArray())
        {
            foreach (var tab in session.Tabs.Values) { tab.Context?.Dispose(); foreach (var job in tab.Jobs.Values) job.Cancel.Dispose(); }
            sessions.Remove(session.Id);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; stopping.Cancel();
            foreach (var session in sessions.Values)
                foreach (var tab in session.Tabs.Values) { if (tab.Active == null) tab.Context?.Dispose(); else tab.Active.Cancel.Cancel(); }
            // Active operations own their contexts until their cancellation path ends.
        }
    }
}

public sealed class WebUserException : Exception { public WebUserException(string message) : base(message) { } }
