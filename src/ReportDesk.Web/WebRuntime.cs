using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    private bool backendDisposed;
    private int backgroundOperations;
    private int backgroundStarted;
    private static readonly HashSet<string> PublicMethods = new(StringComparer.Ordinal) {
        "bootstrap", "list", "select", "definition", "relatedFiles", "query", "view", "page", "lookup", "lookupPage", "lookupAll", "clear", "export"
    };
    private static readonly HashSet<string> AdminMethods = new(StringComparer.Ordinal) {
        "settings", "saveSettings", "testConnection", "activateConnection", "import", "browseReports", "uploadImport", "checkNewReports", "recheck", "reloadReport",
        "sqlEditorOpen", "sqlEditorCheck", "sqlEditorSave", "layoutPreview", "layoutSave", "sqlEditorReveal", "xmlEditorOpen", "xmlEditorCheck", "xmlEditorSave",
        "discoverTns", "tnsAliases", "syncStatus", "syncNow", "resolveSyncConflict", "openLogs", "relatedFiles", "exportSql", "publishPreview",
        "localBegin", "localUpload", "localActivate", "localUseServer", "localCommit", "localAbort", "localStatus", "localLocations"
    };
    private static readonly HashSet<string> Assets = new(StringComparer.Ordinal) {
        "index.html", "styles.css", "execution.css", "sql-editor.css", "query-form.js", "renderer.js", "sql-editor.js",
        "close-emblem.svg", "transport.js", "web.css", "web.js", "xml-editor.js", "local-directory.js"
    };
    private sealed class Session
    {
        public readonly string Id = JsonCodec.Token(), Csrf = JsonCodec.Token();
        public DateTime Seen = DateTime.UtcNow;
        public readonly Dictionary<string, Tab> Tabs = new(StringComparer.Ordinal);
    }
    private sealed class Tab
    {
        public DateTime Seen = DateTime.UtcNow;
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
        public bool SqlDownload;
        public string SqlReportName = "";
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
            var nextSync = DateTime.MinValue;
            var tickSeconds = options.ServerReportsEnabled ? Math.Min(options.SyncSeconds, 30) : 30;
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    bool runSync;
                    lock (gate)
                    {
                        runSync = !disposed && options.ServerReportsEnabled && options.SyncEnabled && !options.Offline && DateTime.UtcNow >= nextSync && workers.Wait(0);
                        if (runSync) Interlocked.Increment(ref backgroundOperations);
                    }
                    if (runSync)
                    {
                        try { backend.Synchronize(linked.Token, _ => { }); }
                        finally { nextSync = DateTime.UtcNow.AddSeconds(options.SyncSeconds); workers.Release(); Interlocked.Decrement(ref backgroundOperations); lock (gate) TryDisposeBackend(); }
                    }
                    lock (gate) Prune();
                    if (backend is BackendAdapter adapter && !disposed) adapter.SweepLocalWorkspaces();
                    await Task.Delay(TimeSpan.FromSeconds(tickSeconds), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    ErrorLog.Write("WebBackground", ex, includeMessage: false);
                    try { await Task.Delay(TimeSpan.FromSeconds(tickSeconds), linked.Token).ConfigureAwait(false); }
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
                var response = WebResponseData.Json(new { csrf = session.Csrf, admin = maintenance, offline = options.Offline, idleMinutes = options.IdleMinutes,
                    serverReportsEnabled = options.ServerReportsEnabled,
                    reportsDirectory = maintenance && options.ServerReportsEnabled ? options.ReportsDirectory : null,
                    syncReady = options.ServerReportsEnabled && !options.Offline && options.SyncEnabled && options.SyncSampleValidated, syncSeconds = options.SyncSeconds });
                response.Headers["Set-Cookie"] = CookieName + "=" + session.Id + "; Path=" + SafeBase(request.BasePath) + "; HttpOnly; SameSite=Strict" + (request.Secure ? "; Secure" : "");
                return response;
            }
        }
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return Static(request);
        lock (gate)
        {
            Prune();
            if (!sessions.TryGetValue(request.Cookie, out var session)) return WebResponseData.Error(401, "会话已结束，请刷新页面后重查。");
            if (path != "/api/catalog-status") session.Seen = DateTime.UtcNow;
            if (request.Method != "GET" && (!JsonCodec.Equal(session.Csrf, request.Csrf) ||
                (request.Origin.Length > 0 && !string.Equals(request.Origin, request.Authority, StringComparison.OrdinalIgnoreCase))))
                return WebResponseData.Error(403, "请求校验失败，请刷新页面。");

            // Lock-free version read; never wait for a catalog reload under the HTTP gate.
            if (path == "/api/catalog-status" && request.Method == "GET")
                return WebResponseData.Json(new { version = backend.CatalogVersion });

            if (path.StartsWith("/api/tasks/", StringComparison.Ordinal) || path.StartsWith("/api/downloads/", StringComparison.Ordinal))
            {
                var id = path.Substring(path.LastIndexOf('/') + 1);
                var jobTab = session.Tabs.Values.FirstOrDefault(t => t.Jobs.ContainsKey(id));
                var job = jobTab?.Jobs[id];
                if (job == null || (job.Maintenance && !maintenance)) return WebResponseData.Error(404, "任务不存在或已释放。");
                jobTab!.Seen = DateTime.UtcNow;
                if (path.StartsWith("/api/downloads/", StringComparison.Ordinal))
                {
                    if (request.Method != "GET") return WebResponseData.Error(405, "请求方式不支持。");
                    if (jobTab.Active == job || job.State != "succeeded" || job.Download == null) return WebResponseData.Error(409, "导出未完成或已释放。");
                    var download = new WebResponseData { Body = job.Download, ContentType = job.SqlDownload ? "text/plain; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
                    download.Headers["Content-Disposition"] = job.SqlDownload ? SqlContentDisposition(job.SqlReportName) : "attachment; filename=ReportDesk.xlsx";
                    return download;
                }
                if (request.Method == "DELETE") { if (job.State == "running") { job.Cancel.Cancel(); job.Message = "正在请求取消，请等待执行端释放资源…"; } return WebResponseData.Json(new { ok = true, data = new { } }); }
                if (request.Method != "GET") return WebResponseData.Error(405, "请求方式不支持。");
                // Completion is observable only after finally has released the
                // tab and worker slot. Chunk uploads may submit their next POST
                // immediately after seeing a terminal state.
                bool settled = jobTab.Active != job;
                return WebResponseData.Json(new { state = settled ? job.State : "running", message = job.Message,
                    data = settled ? job.Data : null, error = settled ? job.Error : "", cancelled = settled && job.State == "cancelled" });
            }
            if (request.Method != "POST") return WebResponseData.Error(405, "请求方式不支持。");
            var adminRoute = path.StartsWith("/api/admin/", StringComparison.Ordinal);
            var method = path.Substring(adminRoute ? 11 : 5);
            bool lifecycle = path == "/api/tab-lifecycle";
            if (!lifecycle && !(adminRoute ? AdminMethods : PublicMethods).Contains(method)) return WebResponseData.Error(404, "此接口不存在。");
            if (!request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) || Encoding.UTF8.GetByteCount(request.Body) > WebOptions.MaxRequestBytes)
                return WebResponseData.Error(400, "请求格式或大小无效。");
            var envelope = JsonCodec.Object(request.Body);
            if (!envelope.TryGetValue("tabId", out var rawTab) || !(rawTab is string tabId) || !Regex.IsMatch(tabId, "\\A[a-zA-Z0-9_-]{8,80}\\z"))
                return WebResponseData.Error(400, "页面任务标识无效。");
            var args = envelope.TryGetValue("args", out var rawArgs) && rawArgs is Dictionary<string, object> dict ? dict : new Dictionary<string, object>();
            if (lifecycle)
            {
                var action = args.TryGetValue("action", out var rawAction) ? rawAction as string : null;
                if (action != "touch" && action != "release") return WebResponseData.Error(400, "页面活动类型无效。");
                if (!session.Tabs.TryGetValue(tabId, out var existing))
                    return WebResponseData.Json(new { ok = true, data = new { alive = false, local = false, busy = false, released = false } });
                var isLocal = backend is BackendAdapter owner && owner.IsLocalContext(existing.Context);
                if (isLocal && (!maintenance || !AllowsLocalFiles(request))) return WebResponseData.Error(403, "当前无权访问本机报表工作区。");
                existing.Seen = DateTime.UtcNow;
                bool protectedSave = backend is BackendAdapter saving && saving.HasPendingLocalSave(existing.Context);
                if (action == "release" && existing.Active == null && !protectedSave)
                {
                    ReleaseTab(existing); session.Tabs.Remove(tabId);
                    return WebResponseData.Json(new { ok = true, data = new { alive = false, local = false, busy = false, released = true } });
                }
                return WebResponseData.Json(new { ok = true, data = new { alive = true, local = isLocal, busy = existing.Active != null || protectedSave, released = false } });
            }
            bool expectsLocal = envelope.TryGetValue("localExpected", out var expected) && expected is bool wasLocal && wasLocal;
            if (expectsLocal && (!session.Tabs.TryGetValue(tabId, out var expectedTab) || !(backend is BackendAdapter expectedOwner) || !expectedOwner.IsLocalContext(expectedTab.Context)))
                return WebResponseData.Error(410, "本页临时工作区已释放，请保留编辑草稿，在报表来源中核对保存状态后重新加载目录。");
            if (!session.Tabs.TryGetValue(tabId, out var tab))
            {
                if (session.Tabs.Count >= 16) return WebResponseData.Error(429, "打开的任务页较多，请关闭并释放不用的页面。");
                tab = new Tab(); session.Tabs.Add(tabId, tab);
            }
            tab.Seen = DateTime.UtcNow;
            if (tab.Active != null) return WebResponseData.Error(409, "当前页面有操作未结束，请等待或取消。");
            bool localMode = backend is BackendAdapter adapter && adapter.IsLocalContext(tab.Context);
            if ((method.StartsWith("local", StringComparison.Ordinal) || localMode) && !AllowsLocalFiles(request))
                return WebResponseData.Error(403, "本机报表目录功能需要通过可信 HTTPS 地址访问。");
            if (localMode && (method == "browseReports" || method == "uploadImport" || method == "syncStatus" || method == "syncNow"))
                return WebResponseData.Error(409, "当前页面使用本机目录，不能操作服务器公共报表目录或后台同步。请在“报表来源 → 导入到服务器”中切回服务器目录。");
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
            if (!workers.Wait(0)) return WebResponseData.Error(429, "服务器正在处理其他操作，请稍后重试。");
            foreach (var old in tab.Jobs.Values.Where(j => j.State != "running").ToArray()) { old.Cancel.Dispose(); tab.Jobs.Remove(old.Id); }
            var next = new Job { Maintenance = adminRoute };
            tab.Active = next; tab.Jobs.Add(next.Id, next);
            try { schedule(host => Execute(session, tab, next, method, args, adminRoute, SafeBase(request.BasePath), host)); }
            catch { tab.Active = null; tab.Jobs.Remove(next.Id); next.Cancel.Dispose(); workers.Release(); throw; }
            return WebResponseData.Json(new { ok = true, jobId = next.Id }, 202);
        }
    }

    private static bool AllowsLocalFiles(WebRequestData request)
    {
        if (request.Secure) return true;
        // Browser secure-context loopback exception is for local verification only.
        // Both the TCP peer and URL must be loopback; a forged Host cannot enable LAN HTTP.
        if (!IPAddress.TryParse(request.RemoteAddress, out var peer) || !IPAddress.IsLoopback(peer) ||
            !Uri.TryCreate(request.Authority, UriKind.Absolute, out var address)) return false;
        return address.Scheme == "http" && (address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(address.Host.Trim('[', ']'), out var host) && IPAddress.IsLoopback(host)));
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
            if (method == "export" || method == "exportSql")
            {
                var sql = method == "exportSql" ? backend.ExportSql(tab.Context, args, linked.Token, Progress) : null;
                var bytes = sql != null ? sql.Content : backend.Export(tab.Context, args, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                lock (gate) { job.Download = bytes; job.SqlDownload = sql != null; job.SqlReportName = sql?.ReportName ?? ""; }
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
            lock (gate)
            {
                tab.Active = null; tab.Seen = session.Seen = DateTime.UtcNow; workers.Release();
                if (disposed) { ReleaseTab(tab); TryDisposeBackend(); }
            }
        }
        return Task.CompletedTask;
    }

    private static string SqlContentDisposition(string reportName)
    {
        const string fallback = "attachment; filename=ReportDesk-SQL.txt";
        if (reportName.Length == 0) return fallback;
        var name = new StringBuilder();
        foreach (char c in reportName)
        {
            if (name.Length >= 120) break;
            name.Append(char.IsControl(c) || "\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
        }
        if (name.Length > 0 && char.IsHighSurrogate(name[name.Length - 1])) name.Length--;
        var safe = name.ToString().Trim().TrimEnd(' ', '.');
        if (safe.Length == 0) safe = "ReportDesk-SQL";
        var stem = safe.Split('.')[0];
        if (Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) safe = "_" + safe;
        var encoded = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(safe + ".txt"))
        {
            if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b == '-' || b == '.' || b == '_' || b == '~') encoded.Append((char)b);
            else encoded.Append('%').Append(b.ToString("X2"));
        }
        return fallback + "; filename*=UTF-8''" + encoded;
    }

    private void ValidateArgs(string method, Dictionary<string, object> args)
    {
        if (method == "import")
        {
            options.RequireServerReports();
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
        var cutoff = DateTime.UtcNow.AddMinutes(-options.IdleMinutes);
        foreach (var session in sessions.Values.ToArray())
        {
            // Tabs expire independently: using another page cannot retain an old
            // upload forever. A running server operation has no idle deadline.
            foreach (var pair in session.Tabs.Where(p => p.Value.Seen < cutoff && p.Value.Active == null).ToArray())
            { ReleaseTab(pair.Value); session.Tabs.Remove(pair.Key); }
            if (session.Seen < cutoff && session.Tabs.Count == 0) sessions.Remove(session.Id);
        }
    }
    private static void ReleaseTab(Tab tab)
    {
        tab.Context?.Dispose(); tab.Context = null;
        foreach (var job in tab.Jobs.Values) job.Cancel.Dispose();
        tab.Jobs.Clear();
    }
    private void TryDisposeBackend()
    {
        if (!disposed || backendDisposed || Volatile.Read(ref backgroundOperations) != 0 || sessions.Values.Any(s => s.Tabs.Values.Any(t => t.Active != null))) return;
        backendDisposed = true;
        backend.Dispose();
        sessions.Clear();
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; stopping.Cancel();
            foreach (var session in sessions.Values)
                foreach (var tab in session.Tabs.Values) { if (tab.Active == null) ReleaseTab(tab); else tab.Active.Cancel.Cancel(); }
            // Active operations own their contexts until their cancellation path ends.
            TryDisposeBackend();
        }
    }
}

public sealed class WebUserException : Exception { public WebUserException(string message) : base(message) { } }
