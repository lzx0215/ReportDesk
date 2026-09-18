using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Host;

namespace ReportDesk.Web.Services;

// Server-only snapshot. Never serialize this object in an HTTP response.
internal sealed class WebConnectionSnapshot
{
    public ConnectionSettings Settings { get; }
    public string Password { get; }
    public bool Offline { get; }
    internal WebConnectionSnapshot(ConnectionSettings settings, string password, bool offline)
    { Settings = settings; Password = password; Offline = offline; }
}

internal sealed class WebContext : IDisposable
{
    internal readonly object Gate = new();
    internal readonly WebBackend Owner;
    internal readonly Service Service;
    internal long Version;
    internal bool Disposed;
    internal WebContext(WebBackend owner, Service service, long version)
    { Owner = owner; Service = service; Version = version; }
    public void Dispose()
    {
        lock (Gate)
        {
            if (Disposed) return;
            Disposed = true;
            Service.Dispose();
        }
    }
}

internal sealed class WebBackend : IDisposable
{
    private readonly Service shared;
    private readonly string dataDirectory, reportRoot;
    private long version;
    private bool disposed;
    public object FileGate { get; } = new();

    public WebBackend(string dataDirectory, string reportRoot, bool offline = true)
    {
        this.dataDirectory = Path.GetFullPath(Path.Combine(dataDirectory, "config"));
        this.reportRoot = Path.GetFullPath(reportRoot);
        if (!Directory.Exists(this.dataDirectory)) throw new InvalidOperationException("服务端配置目录不存在。");
        ValidatePath(this.reportRoot, this.reportRoot);
        ValidatePath(this.dataDirectory, this.dataDirectory);
        ValidateConfigFiles();
        shared = new Service(this.dataDirectory, this.dataDirectory, offline, allReports: true);
        shared.WebInitialize(this.reportRoot, ReportPath);
    }

    public WebContext CreateSession()
    {
        lock (FileGate)
        {
            ThrowIfDisposed();
            return new WebContext(this, shared.WebCreateContext(), version);
        }
    }

    public object Call(WebContext context, string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress, bool admin)
    {
        if (context == null || context.Owner != this) throw new InvalidOperationException("查询上下文无效。");
        if (args == null) throw new ArgumentNullException(nameof(args));
        progress ??= _ => { };
        lock (context.Gate)
        {
            CheckContext(context);
            token.ThrowIfCancellationRequested();
            // Query/lookup execution happens AFTER releasing FileGate.
            if (IsOrdinary(method) && !(admin && method == "relatedFiles"))
            {
                lock (FileGate)
                {
                    Refresh(context);
                    if (method == "select" || method == "definition" || method == "relatedFiles" || method == "query" || method == "lookup")
                        context.Service.WebValidateReport(args, ReportPath);
                }
                return context.Service.WebHandle(method, args, token, progress);
            }
            if (!admin || !IsAdmin(method)) throw new InvalidOperationException("不允许调用此维护操作。");
            lock (FileGate)
            {
                Refresh(context);
                ValidateAdminArguments(context.Service, method, args);
                bool global = IsGlobalAdmin(method);
                var service = global ? shared : context.Service;
                try { return service.WebAdminHandle(method, args, token, progress); }
                catch (InvalidOperationException ex) { throw service.WebSafeAdminError(ex, args); }
                finally
                {
                    // Publish failure blockers too: a file saved but not reloaded
                    // must not allow another tab to execute its former definition.
                    // File edits publish only from the transaction reload callback.
                    if (method != "sqlEditorSave" && method != "layoutSave")
                    {
                        if (!global) shared.WebPublishDefinitions(service);
                        version++;
                    }
                }
            }
        }
    }

    public byte[] Export(WebContext context, Dictionary<string, object> args, CancellationToken token)
    {
        if (context == null || context.Owner != this) throw new InvalidOperationException("查询上下文无效。");
        lock (context.Gate)
        {
            CheckContext(context);
            token.ThrowIfCancellationRequested();
            return context.Service.WebExport(args, token);
        }
    }

    // Trusted server injection only; this method is deliberately not a Call verb.
    public WebConnectionSnapshot GetConnection()
    {
        lock (FileGate) { ThrowIfDisposed(); return shared.WebGetConnection(); }
    }

    public string GetConnectionString()
    {
        var connection = GetConnection();
        if (connection.Offline) throw new ReportDesk.Web.WebUserException("离线模式禁止真实数据库连接。");
        return OracleQueryService.ConnectionString(connection.Settings, connection.Password);
    }

    // Trusted server injection only; this method is deliberately not a Call verb.
    public void SetConnectionSettings(ConnectionSettings settings, string password)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings.Mode == ConnectionMode.Tns) ValidatePath(settings.TnsFile, dataDirectory);
        lock (FileGate)
        {
            ThrowIfDisposed();
            shared.WebInjectConnection(settings, password);
            version++;
        }
    }

    public object ReloadChangedReports(IEnumerable<string> paths, CancellationToken token = default)
        => ReloadChangedReportsStrict(paths, token);

    public object ReloadChangedReportsStrict(IEnumerable<string> paths, CancellationToken token = default)
    {
        var changed = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in changed) ReportPath(path);
        lock (FileGate)
        {
            ThrowIfDisposed();
            using var staged = shared.WebCreateContext();
            var result = staged.WebReloadChanged(changed, reportRoot, token);
            token.ThrowIfCancellationRequested();
            shared.WebPublishDefinitions(staged);
            version++;
            return result;
        }
    }

    public IReadOnlyList<string> GetEditPaths(WebContext context, Dictionary<string, object> args, bool layout)
    {
        if (context == null || context.Owner != this) throw new InvalidOperationException("查询上下文无效。");
        lock (context.Gate)
        {
            CheckContext(context);
            lock (FileGate)
            {
                Refresh(context);
                try { return context.Service.WebEditPaths(args, layout); }
                catch (InvalidOperationException ex) { throw context.Service.WebSafeAdminError(ex, args); }
            }
        }
    }

    private void Refresh(WebContext context)
    {
        ThrowIfDisposed();
        if (context.Version == version) return;
        context.Service.WebRefresh(shared);
        context.Version = version;
    }

    private void CheckContext(WebContext context)
    {
        ThrowIfDisposed();
        if (context.Disposed) throw new ObjectDisposedException(nameof(WebContext));
    }

    private void ThrowIfDisposed()
    { if (disposed) throw new ObjectDisposedException(nameof(WebBackend)); }

    private static bool IsOrdinary(string method)
    {
        switch (method)
        {
            case "bootstrap": case "list": case "select": case "definition": case "query": case "view": case "page":
            case "lookup": case "lookupPage": case "lookupAll": case "clear": case "relatedFiles": return true;
            default: return false;
        }
    }

    private static bool IsAdmin(string method)
    {
        switch (method)
        {
            case "settings": case "saveSettings": case "testConnection": case "discoverTns": case "tnsAliases":
            case "import": case "checkNewReports": case "recheck": case "relatedFiles": case "sqlEditorOpen": case "sqlEditorCheck":
            case "sqlEditorSave": case "sqlEditorReveal": case "reloadReport": case "layoutPreview": case "layoutSave": return true;
            default: return false;
        }
    }

    private static bool IsGlobalAdmin(string method)
    {
        switch (method)
        {
            case "settings": case "saveSettings": case "testConnection": case "discoverTns": case "tnsAliases":
            case "import": case "checkNewReports": case "recheck": return true;
            default: return false;
        }
    }

    private void ValidateAdminArguments(Service service, string method, Dictionary<string, object> args)
    {
        ValidateConfigFiles();
        switch (method)
        {
            case "import": ReportPath(Service.Text(args, "path")); break;
            case "checkNewReports": shared.WebValidateRememberedSources(ReportPath); break;
            case "recheck": shared.WebValidateAllReports(ReportPath); break;
            case "tnsAliases": ValidatePath(Service.Text(args, "path"), dataDirectory); break;
            case "saveSettings": case "testConnection":
                if (Service.Number(args, "mode") == (int)ConnectionMode.Tns) ValidatePath(Service.Text(args, "tnsFile"), dataDirectory);
                break;
            case "relatedFiles": case "sqlEditorOpen": case "sqlEditorCheck": case "sqlEditorSave":
            case "sqlEditorReveal": case "reloadReport": case "layoutPreview": case "layoutSave": service.WebValidateReport(args, ReportPath); break;
        }
    }

    private void ReportPath(string path) => ValidatePath(path, reportRoot);

    private void ValidateConfigFiles()
    {
        foreach (var name in new[] { "connection.json", "catalog.json", "import-sources.json", ReportLocations.FileName })
            ValidatePath(Path.Combine(dataDirectory, name), dataDirectory);
    }

    internal static void ValidatePath(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("必须使用受控目录内的服务器绝对路径。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.Equals(allowed, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文件不在受控目录内。");
        if (full.IndexOf(':', 2) >= 0) throw new InvalidOperationException("不允许使用文件流路径。");
        // Check ancestors too, including the configured root. Reject junctions and
        // symlinks rather than trusting a lexical prefix to establish containment.
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("受控文件路径不允许经过目录链接。");
        }
    }

    public void Dispose()
    {
        lock (FileGate)
        {
            if (disposed) return;
            disposed = true;
            shared.Dispose();
        }
    }
}
