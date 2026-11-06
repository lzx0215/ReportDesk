using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Host;
using ReportDesk.Web.Services;
using ReportDesk.Web.Sync;
using ReportDesk.Web.Publish;

namespace ReportDesk.Web;

internal sealed class BackendAdapter : IWebBackend
{
    private readonly WebOptions options;
    private readonly WebBackend business;
    private readonly ReportWorkDirectory? work;
    private readonly ReportFileTransaction? files;
    private readonly SyncCoordinator? sync;
    private readonly LocalWorkspaceStore localWorkspaces;

    public BackendAdapter(WebOptions options)
    {
        this.options = options;
        var config = Path.Combine(options.DataDirectory, "config");
        WebOptions.EnsureNoLinks(config); Directory.CreateDirectory(config);
        if (options.ServerReportsEnabled)
        {
            var stateDirectory = Path.Combine(options.DataDirectory, "SyncState");
            var legacyRoot = Path.GetFullPath(Path.Combine(options.DataDirectory, "Reports")).TrimEnd('\\', '/');
            if (!string.Equals(options.ReportsDirectory.TrimEnd('\\', '/'), legacyRoot, StringComparison.OrdinalIgnoreCase))
                stateDirectory = Path.Combine(stateDirectory, ReportWorkDirectory.Hash(Encoding.UTF8.GetBytes(options.ReportsDirectory.ToUpperInvariant())));
            // A different root must never replay a former directory's recovery journal or baseline.
            work = new ReportWorkDirectory(options.ReportsDirectory, stateDirectory);
            files = new ReportFileTransaction(work);
            // Recover the on-disk batch BEFORE the catalog or allowlist sees any files.
            files.Recover(() => { });
            using (work.Acquire()) business = new WebBackend(options.DataDirectory, options.ReportsDirectory, options.Offline);
            var syncOptions = new SyncOptions { WorkingDirectory = work.Root, StateDirectory = work.StateRoot, Enabled = options.SyncEnabled && !options.Offline };
            var policy = ReportFilePolicy.FromBaseline(work);
            sync = new SyncCoordinator(syncOptions, new OracleReleaseReader(new ConnectionProvider(business), syncOptions, options.SyncSampleValidated), policy, new ReloadSink(business, work));
            sync.InitializeBaseline();
        }
        else business = new WebBackend(options.DataDirectory, "", options.Offline, serverReportsEnabled: false);
        business.ValidateApprovedTns = path => {
            if (!options.TnsFiles.Any(file => string.Equals(Path.GetFullPath(file), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new WebUserException("TNS 文件不在服务器批准清单中。");
            WebOptions.EnsureNoLinks(path);
        };
        localWorkspaces = new LocalWorkspaceStore(options.DataDirectory);
    }

    public IDisposable CreateContext() => new LocalReportContext(this, business.CreateSession());
    public string CatalogVersion => business.CatalogVersion;

    public bool IsLocalContext(IDisposable? context) => context is LocalReportContext tab && tab.Owner == this && (tab.Active != null || tab.Staging != null);
    internal bool HasPendingLocalSave(IDisposable? context) => context is LocalReportContext tab && tab.Owner == this && tab.Active?.HasPending == true;
    internal void SweepLocalWorkspaces() => localWorkspaces.Sweep();
    public string GetCatalogVersion(IDisposable? context) => context is LocalReportContext tab && tab.Owner == this && tab.Active != null ? tab.Active.Version : CatalogVersion;

    private LocalReportContext OwnContext(IDisposable context)
    {
        if (!(context is LocalReportContext tab) || tab.Owner != this || tab.Disposed) throw new WebUserException("页面报表会话无效，请重新打开页面。");
        return tab;
    }

    public object Call(IDisposable context, string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
    {
        var tab = OwnContext(context);
        if (method.StartsWith("local", StringComparison.Ordinal))
        {
            if (!maintenance) throw new WebUserException("当前没有本机目录维护权限。");
            switch (method)
            {
                case "localLocations":
                    return business.LocalLocations(tab.Server, LocalLocationItems(args, token));
                case "localStatus": return tab.Active?.Status() ?? new { local = tab.Staging != null, name = tab.Staging?.Name ?? "", pending = (object?)null, requiresRescan = false, ready = false };
                case "localBegin":
                    if (tab.Active?.HasPending == true) throw new WebUserException("请先完成本机保存确认或恢复，再更换目录。");
                    var name = Service.Text(args, "name").Trim();
                    if (name.Length == 0 || name.Any(char.IsControl)) throw new WebUserException("请选择有效的本机目录。");
                    var staging = localWorkspaces.Create(name, business);
                    tab.Staging?.Dispose(); tab.Staging = staging;
                    return new { begun = true, name = staging.Name };
                case "localUpload":
                    if (tab.Staging == null) throw new WebUserException("目录上传会话不存在，请重新选择目录。");
                    return tab.Staging.UploadChunk(args, token);
                case "localActivate":
                    if (tab.Active?.HasPending == true) throw new WebUserException("本机保存尚未确认。");
                    if (tab.Staging == null) throw new WebUserException("目录上传会话不存在，请重新扫描。");
                    var loaded = tab.Staging.Activate(token);
                    var previous = tab.Active; tab.Active = tab.Staging; tab.Staging = null; previous?.Dispose();
                    return loaded;
                case "localUseServer":
                    if (tab.Active?.HasPending == true) throw new WebUserException("请先完成本机保存确认或恢复，再切换目录。");
                    tab.Active?.Dispose(); tab.Active = null; tab.Staging?.Dispose(); tab.Staging = null;
                    return CallServer(tab.Server, "bootstrap", args, false, token, progress);
                case "localCommit": case "localAbort":
                    if (tab.Active == null) throw new WebUserException("本机报表会话已结束，请重新扫描目录并核对保存结果。");
                    return tab.Active.Call(method, args, true, token, progress);
                default: throw new WebUserException("不支持的本机目录操作。");
            }
        }
        // Connection settings remain global and keep the existing authorization,
        // DPAPI persistence and server-only credential handling unchanged.
        if (WebBackend.IsConnectionOperation(method))
            return CallServer(tab.Server, method, args, maintenance, token, progress);
        if (tab.Active != null) return tab.Active.Call(method, args, maintenance, token, progress);
        if (tab.Staging != null) throw new WebUserException("本机目录尚未加载完成，请完成扫描或关闭当前目录。");
        return CallServer(tab.Server, method, args, maintenance, token, progress);
    }

    private static IReadOnlyList<Tuple<string, string>> LocalLocationItems(Dictionary<string, object> args, CancellationToken token)
    {
        if (!args.TryGetValue("items", out var value) || !(value is IEnumerable rows) || value is string || value is IDictionary)
            throw new WebUserException("本机位置匹配请求无效。");
        var result = new List<Tuple<string, string>>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= 100 || !(row is Dictionary<string, object> item) ||
                !item.TryGetValue("path", out var pathValue) || !(pathValue is string path) ||
                !item.TryGetValue("hash", out var hashValue) || !(hashValue is string hash))
                throw new WebUserException("本机位置匹配每批最多 100 条，且必须提供路径和 SHA-256。");
            var segments = path.Split('/');
            if (path.Length == 0 || path.Length > 1024 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 ||
                path.Any(char.IsControl) || segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.EndsWith(".") || s.EndsWith(" ") || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || !seen.Add(path) ||
                hash.Length != 64 || hash.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))))
                throw new WebUserException("本机位置匹配路径或 SHA-256 无效。");
            result.Add(Tuple.Create(path, hash));
        }
        return result;
    }

    private object CallServer(WebContext tab, string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
    {
        try
        {
            if (!options.ServerReportsEnabled)
            {
                if (method != "bootstrap" && method != "list" && method != "clear" && !WebBackend.IsConnectionOperation(method))
                    options.RequireServerReports();
                return business.Call(tab, method, args, token, progress, maintenance);
            }
            if (files!.RequiresRecovery) throw new WebUserException("检测到未完成的报表更新，请重启应用完成恢复后再操作。");
            if (maintenance && method == "publishPreview")
            {
                var source = business.GetPublishSource(tab, args);
                var preview = ReportPublishPreparation.Preview(work!, Relative(source.Path), token);
                return new { title = source.Name,
                    files = preview.Files.Select(f => new { role = f.Role, path = f.RelativePath, status = f.Status, sha256 = f.Sha256 }).ToArray(),
                    warnings = preview.RequiresConfirmation ? new[] { "候选版式按文件名匹配，须与 HIS 实际使用的版式核对。" } : Array.Empty<string>() };
            }
            if (maintenance && method == "resolveSyncConflict")
            {
                if (!args.TryGetValue("releaseId", out var rawId) || !int.TryParse(Convert.ToString(rawId), out var releaseId)
                    || !args.TryGetValue("acceptHis", out var rawChoice) || !(rawChoice is bool acceptHis)
                    || !args.TryGetValue("fingerprint", out var rawFingerprint) || !(rawFingerprint is string fingerprint) || fingerprint.Length == 0)
                    throw new WebUserException("冲突确认信息不完整，请刷新同步状态后重试。");
                sync!.ResolveConflict(releaseId, fingerprint, acceptHis ? ConflictResolution.AcceptHis : ConflictResolution.KeepLocal, token);
                return Synchronize(token, progress);
            }
            if (maintenance && (method == "sqlEditorSave" || method == "layoutSave" || method == "xmlEditorSave"))
            {
                if (method == "xmlEditorSave")
                {
                    var edit = business.GetXmlEditPlan(tab, args);
                    var xmlPaths = edit.Paths.ToArray();
                    if (xmlPaths.Length != 1) throw new InvalidOperationException("XML 编辑目标无效。");
                    object? xmlResult = null;
                    files.ExecuteProtectedXmlEdit(Relative(xmlPaths[0]), edit.ExpectedHash,
                        () => xmlResult = business.Call(tab, method, args, token, progress, true),
                        () => business.ReloadChangedReportsStrict(xmlPaths), token);
                    return xmlResult!;
                }
                var paths = business.GetEditPaths(tab, args, method == "layoutSave").ToArray();
                object? result = null;
                files.ExecuteProtectedEdit(paths.Select(Relative),
                    () => result = business.Call(tab, method, args, token, progress, true),
                    () => business.ReloadChangedReportsStrict(paths), token);
                return result!;
            }
            if (maintenance)
            {
                object result;
                using (work!.Acquire(token)) result = business.Call(tab, method, args, token, progress, true);
                if (method == "import" || method == "checkNewReports") sync!.RegisterImportedBaseline(token);
                return result;
            }
            return business.Call(tab, method, args, token, progress, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (WebUserException) { throw; }
        catch (SyncSafetyException ex) { throw new WebUserException(SyncMessage(ex.Code)); }
        catch (InvalidOperationException ex)
        {
            var connection = business.GetConnection();
            throw new WebUserException(ErrorLog.Sanitize(ex.Message, ErrorLog.ConnectionValues(connection.Settings, connection.Password)));
        }
    }

    public byte[] Export(IDisposable context, Dictionary<string, object> args, CancellationToken token)
    {
        var tab = OwnContext(context);
        if (tab.Active != null) return tab.Active.Export(args, token);
        if (tab.Staging != null) throw new WebUserException("本机目录尚未加载完成。");
        options.RequireServerReports();
        return business.Export(tab.Server, args, token);
    }
    public SqlExportResult ExportSql(IDisposable context, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        var tab = OwnContext(context);
        if (tab.Active != null) return tab.Active.ExportSql(args, token, progress);
        if (tab.Staging != null) throw new WebUserException("本机目录尚未加载完成。");
        options.RequireServerReports();
        if (files!.RequiresRecovery) throw new WebUserException("检测到未完成的报表更新，请重启应用完成恢复后再操作。");
        using (work!.Acquire(token)) return business.ExportSql(tab.Server, args, token, progress);
    }
    public object BrowseReports(string relative)
    {
        options.RequireServerReports();
        var current = options.NormalizeReportRelative(relative);
        var full = options.ResolveBrowseDirectory(current);
        const int limit = 4000;
        var folders = new List<object>();
        var files = new List<object>();
        foreach (var directory in Directory.EnumerateDirectories(full).OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase))
        {
            if (folders.Count + files.Count >= limit) throw new WebUserException("此目录内项目较多，请进入子文件夹后再选择。");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var name = Path.GetFileName(directory);
            if (name.Length == 0 || name == "." || name == "..") continue;
            var child = current.Length == 0 ? name : current + "/" + name;
            folders.Add(new { name, path = child.Replace('\\', '/') });
        }
        foreach (var file in Directory.EnumerateFiles(full, "*.xml").OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase))
        {
            if (folders.Count + files.Count >= limit) throw new WebUserException("此目录内项目较多，请进入子文件夹后再选择。");
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            var child = current.Length == 0 ? name : current + "/" + name;
            files.Add(new { name, path = child.Replace('\\', '/') });
        }
        var slash = current.LastIndexOf('/');
        return new { path = current, parent = current.Length == 0 ? "" : slash < 0 ? "" : current.Substring(0, slash), folders, files };
    }

    public object UploadImport(Dictionary<string, object> args)
    {
        options.RequireServerReports();
        var folder = args.TryGetValue("folder", out var rawFolder) && rawFolder is bool isFolder && isFolder;
        if (!args.TryGetValue("files", out var rawFiles) || !(rawFiles is System.Collections.IEnumerable list))
            throw new WebUserException("请选择要导入的 XML 文件。");
        var items = new List<(string path, string text)>();
        var total = 0;
        foreach (var raw in list)
        {
            if (!(raw is Dictionary<string, object> item)) throw new WebUserException("上传文件格式无效。");
            var relative = options.NormalizeReportRelative(item.TryGetValue("path", out var rawPath) ? Convert.ToString(rawPath) ?? "" : "");
            var text = item.TryGetValue("text", out var rawText) ? Convert.ToString(rawText) ?? "" : "";
            var name = relative.Split('/').LastOrDefault() ?? "";
            if (relative.Length == 0 || name.Length == 0 || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                throw new WebUserException("只能上传 XML 报表文件。");
            if (!folder && relative.Contains("/")) throw new WebUserException("单文件导入请直接选择 XML 文件。");
            if (text.Length == 0) throw new WebUserException("所选 XML 是空文件：" + name);
            total += Encoding.UTF8.GetByteCount(text);
            if (items.Count >= 200 || total > 5 * 1024 * 1024) throw new WebUserException("所选文件过多或过大。整份 LIB 请放在页面显示的服务器报表目录，再点“检查新增”，无需浏览器上传。");
            items.Add((relative, text));
        }
        if (items.Count == 0) throw new WebUserException("请选择 XML 文件。");
        var root = folder ? items[0].path.Split('/')[0] : items[0].path;
        if (folder && items.Any(item => item.path != root && !item.path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
            throw new WebUserException("文件夹导入只能包含同一个所选目录中的 XML。");
        foreach (var item in items)
        {
            var dest = options.MapInsideReports(item.path);
            if (!dest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new WebUserException("只能上传 XML 报表文件。");
            var parent = Path.GetDirectoryName(dest) ?? options.ReportsDirectory;
            Directory.CreateDirectory(parent);
            WebOptions.EnsureNoLinks(parent);
            if (Directory.Exists(dest)) throw new WebUserException("不能覆盖同名目录。");
            File.WriteAllText(dest, item.text, new UTF8Encoding(false));
        }
        return new { path = root.Replace('\\', '/') };
    }

    public object SyncStatus()
    {
        if (!options.ServerReportsEnabled)
            return new { serverReportsEnabled = false, enabled = false, running = false, code = "ServerReportsDisabled",
                offline = options.Offline, configuredEnabled = options.SyncEnabled, sampleValidated = options.SyncSampleValidated,
                reportsDirectory = "", intervalSeconds = options.SyncSeconds, lastSuccessUtc = (string?)null,
                message = WebOptions.ServerReportsDisabledMessage, pending = 0, conflictCount = 0, releaseId = (int?)null,
                oracleValidated = false, requiresRecovery = false, conflicts = Array.Empty<object>() };
        var status = sync!.Status;
        return new { serverReportsEnabled = true, enabled = options.SyncEnabled && !options.Offline, running = status.Code == "Running", code = status.Code,
            offline = options.Offline, configuredEnabled = options.SyncEnabled, sampleValidated = options.SyncSampleValidated,
            reportsDirectory = options.ReportsDirectory, intervalSeconds = options.SyncSeconds,
            lastSuccessUtc = status.LastSuccessUtc?.ToString("o"), message = options.Offline ? "离线模式已开启，不会连接发布库或下载报表。" :
                !options.SyncEnabled ? "自动下载未启用，请配置 ReportDesk.SyncEnabled。" :
                !options.SyncSampleValidated ? "发布样本尚未验证，不会下载；请完成现场核对后配置 ReportDesk.SyncSampleValidated。" : SyncMessage(status.Code),
            pending = status.PendingReleases, conflictCount = status.ConflictReleases, releaseId = status.ReleaseId,
            oracleValidated = status.OracleValidated, requiresRecovery = status.RequiresRecovery,
            conflicts = sync.GetPendingConflicts().Select(p => new { releaseId = p.ReleaseId, code = p.Code,
                message = SyncMessage(p.Code), fingerprint = p.Fingerprint, canResolve = p.CanResolve }).ToArray() };
    }
    public object Synchronize(CancellationToken token, Action<string> progress)
    {
        if (!options.ServerReportsEnabled || options.Offline || !options.SyncEnabled) return SyncStatus();
        progress("正在检查 HIS 已发布报表；不会运行 HIP 或回写发布表…");
        sync!.RunOnce(token);
        token.ThrowIfCancellationRequested();
        return SyncStatus();
    }
    private string Relative(string path)
    {
        options.RequireServerReports();
        WebBackend.ValidatePath(path, work!.Root);
        return path.Substring(work.Root.Length + 1).Replace('\\', '/');
    }
    private static string SyncMessage(string code)
    {
        switch (code)
        {
            case "Disabled": return "自动同步未启用；离线模式不会连接 Oracle。";
            case "OracleNotValidated": return "尚未完成现场发布样本核对，自动同步保持关闭。";
            case "NotStarted": return "等待首次同步。";
            case "Running": return "正在读取和校验已发布报表。";
            case "Success": return "最近一次同步完成。";
            case "Busy": return "已有同步任务运行，本次未重复执行。";
            case "Pending": return "部分发布待处理，当前可用报表仍保留；请查看冲突或失败项。";
            case "LocalConflict": return "服务器报表已有本地修改，未自动覆盖。";
            case "Cancelled": return "同步已取消，下次将补查。";
            case "TransactionRolledBack": return "报表更新未完成，原文件未改变或已恢复；请检查目标 XML 的修改权限、占用状态及脱敏日志，重新打开编辑器核对。";
            case "RecoveryRequired": return "报表更新需要恢复，已停止进一步写入；请检查目录权限后重启。";
            case "DiscoveryIncomplete": return "报表目录扫描不完整，请检查 XML 文件、目录读取权限和目录链接后重试。";
            case "MainCompanionUnresolved": return "无法按命名约定确定主表版式，请核对查询设置和版式文件。";
            case "CompanionUnresolved": return "报表版式缺失、存在多个候选或引用路径不受支持，请先核对关联 XML。";
            case "ReportFileSizeRejected": return "报表文件为空或超过预览大小限制。";
            case "ReportFileUnavailable": return "无法读取报表文件，请检查文件和目录权限。";
            case "ReportTypeRejected": return "查询设置或版式 XML 类型不符合预览要求。";
            case "IncompleteQuery": return "查询设置缺少可识别的报表查询 SQL。";
            case "ReportXmlInvalid": return "报表 XML 无法解析，请检查文件内容。";
            case "PreviewChanged": return "预览期间报表文件发生变化，请重新预览。";
            default: return "同步未完成（" + code + "）；未将失败批次作为成功更新。";
        }
    }
    public void Dispose() { localWorkspaces.Dispose(); business.Dispose(); }
    private sealed class ConnectionProvider : IOracleSyncConnectionProvider
    {
        private readonly WebBackend business;
        public ConnectionProvider(WebBackend business) { this.business = business; }
        public string GetConnectionString() => business.GetConnectionString();
    }
    private sealed class ReloadSink : IReportReloadSink
    {
        private readonly WebBackend business;
        private readonly ReportWorkDirectory work;
        public ReloadSink(WebBackend business, ReportWorkDirectory work) { this.business = business; this.work = work; }
        public void Reload(IReadOnlyList<string> paths) => business.ReloadChangedReportsStrict(paths.Select(work.Resolve));
    }
}
