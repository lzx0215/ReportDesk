using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Web.Services;
using ReportDesk.Web.Sync;

namespace ReportDesk.Web;

internal sealed class BackendAdapter : IWebBackend
{
    private readonly WebOptions options;
    private readonly WebBackend business;
    private readonly ReportWorkDirectory work;
    private readonly ReportFileTransaction files;
    private readonly SyncCoordinator sync;

    public BackendAdapter(WebOptions options)
    {
        this.options = options;
        var config = Path.Combine(options.DataDirectory, "config");
        WebOptions.EnsureNoLinks(config); Directory.CreateDirectory(config);
        work = new ReportWorkDirectory(options.ReportsDirectory, Path.Combine(options.DataDirectory, "SyncState"));
        files = new ReportFileTransaction(work);
        // Recover the on-disk batch BEFORE the catalog or allowlist sees any files.
        files.Recover(() => { });
        using (work.Acquire()) business = new WebBackend(options.DataDirectory, options.ReportsDirectory, options.Offline);
        var syncOptions = new SyncOptions { WorkingDirectory = work.Root, StateDirectory = work.StateRoot, Enabled = options.SyncEnabled && !options.Offline };
        var policy = ReportFilePolicy.FromBaseline(work);
        sync = new SyncCoordinator(syncOptions, new OracleReleaseReader(new ConnectionProvider(business), syncOptions, options.SyncSampleValidated), policy, new ReloadSink(business, work));
        sync.InitializeBaseline();
    }

    public IDisposable CreateContext() => business.CreateSession();

    public object Call(IDisposable context, string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
    {
        var tab = (WebContext)context;
        try
        {
            if (files.RequiresRecovery) throw new WebUserException("检测到未完成的报表更新，请重启应用完成恢复后再操作。");
            if (maintenance && method == "resolveSyncConflict")
            {
                if (!args.TryGetValue("releaseId", out var rawId) || !int.TryParse(Convert.ToString(rawId), out var releaseId)
                    || !args.TryGetValue("acceptHis", out var rawChoice) || !(rawChoice is bool acceptHis)
                    || !args.TryGetValue("fingerprint", out var rawFingerprint) || !(rawFingerprint is string fingerprint) || fingerprint.Length == 0)
                    throw new WebUserException("冲突确认信息不完整，请刷新同步状态后重试。");
                sync.ResolveConflict(releaseId, fingerprint, acceptHis ? ConflictResolution.AcceptHis : ConflictResolution.KeepLocal, token);
                return Synchronize(token, progress);
            }
            if (maintenance && (method == "sqlEditorSave" || method == "layoutSave"))
            {
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
                using (work.Acquire(token)) result = business.Call(tab, method, args, token, progress, true);
                if (method == "import" || method == "checkNewReports") sync.RegisterImportedBaseline(token);
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

    public byte[] Export(IDisposable context, Dictionary<string, object> args, CancellationToken token) => business.Export((WebContext)context, args, token);
    public object BrowseReports(string relative)
    {
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
            if (items.Count >= 200 || total > 5 * 1024 * 1024) throw new WebUserException("所选文件过多或过大。请选择单个报表或较小文件夹；整份 HIS 目录请先复制到服务器 Reports 后再导入。");
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
        var status = sync.Status;
        return new { enabled = options.SyncEnabled && !options.Offline, running = status.Code == "Running", code = status.Code,
            lastSuccessUtc = status.LastSuccessUtc?.ToString("o"), message = SyncMessage(status.Code),
            pending = status.PendingReleases, conflictCount = status.ConflictReleases, releaseId = status.ReleaseId,
            oracleValidated = status.OracleValidated, requiresRecovery = status.RequiresRecovery,
            conflicts = sync.GetPendingConflicts().Select(p => new { releaseId = p.ReleaseId, code = p.Code,
                message = SyncMessage(p.Code), fingerprint = p.Fingerprint, canResolve = p.CanResolve }).ToArray() };
    }
    public object Synchronize(CancellationToken token, Action<string> progress)
    {
        if (options.Offline || !options.SyncEnabled) return SyncStatus();
        progress("正在检查 HIS 已发布报表；不会运行 HIP 或回写发布表…");
        sync.RunOnce(token);
        token.ThrowIfCancellationRequested();
        return SyncStatus();
    }
    private string Relative(string path)
    {
        WebBackend.ValidatePath(path, work.Root);
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
            case "TransactionRolledBack": return "报表更新未完成，已恢复原文件；请重新打开编辑器核对。";
            case "RecoveryRequired": return "报表更新需要恢复，已停止进一步写入；请检查目录权限后重启。";
            default: return "同步未完成（" + code + "）；未将失败批次作为成功更新。";
        }
    }
    public void Dispose() => business.Dispose();
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
