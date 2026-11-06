using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Host;
using ReportDesk.Web.Services;
using ReportDesk.Web.Sync;

namespace ReportDesk.Web;

// Only BackendAdapter creates these contexts. A client-supplied tab/workspace ID
// is never a filesystem path, and a context belongs to exactly one adapter.
internal sealed class LocalReportContext : IDisposable
{
    internal readonly BackendAdapter Owner;
    internal readonly WebContext Server;
    internal volatile LocalReportWorkspace? Active;
    internal volatile LocalReportWorkspace? Staging;
    internal bool Disposed;
    internal LocalReportContext(BackendAdapter owner, WebContext server) { Owner = owner; Server = server; }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Active?.Dispose(); Active = null;
        Staging?.Dispose(); Staging = null;
        Server.Dispose();
    }
}

// An exclusive owner lease makes IIS overlap/recycle cleanup safe: a new worker
// only removes marked, abandoned directories, not another worker's live data.
internal sealed class LocalWorkspaceStore : IDisposable
{
    private const string Marker = "ReportDesk.LocalWorkspaces.v1";
    private readonly string root, ownerRoot;
    private readonly object gate = new();
    private readonly HashSet<string> retired = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? lease;
    internal LocalWorkspaceStore(string dataDirectory)
    {
        root = Path.GetFullPath(Path.Combine(dataDirectory, "LocalWorkspaces"));
        WebOptions.EnsureNoLinks(root); Directory.CreateDirectory(root);
        ownerRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownerRoot);
        lease = new FileStream(Path.Combine(ownerRoot, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(ownerRoot, "owner.kind"), Marker);
        Sweep();
    }
    internal void Sweep()
    {
        lock (gate)
        {
            if (lease == null) return;
            WebOptions.EnsureNoLinks(root);
            foreach (var path in retired.ToArray())
            {
                try { DeleteOwned(ownerRoot, path); retired.Remove(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
            foreach (var old in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(old, ownerRoot, StringComparison.OrdinalIgnoreCase) || !IsId(Path.GetFileName(old))) continue;
                try
                {
                    DeleteOwner(old);
                }
                catch (IOException) { /* Another live worker or a locked file: retry later. */ }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
        }
    }
    internal LocalReportWorkspace Create(string name, WebBackend connectionOwner)
    {
        lock (gate)
        {
            if (lease == null) throw new ObjectDisposedException(nameof(LocalWorkspaceStore));
            return new LocalReportWorkspace(ownerRoot, name, connectionOwner, Retire);
        }
    }
    private void Retire(string path)
    {
        lock (gate)
        {
            // Only a workspace created by this store can supply this callback.
            retired.Add(path);
            try { DeleteOwned(ownerRoot, path); retired.Remove(path); }
            catch (Exception ex) { ErrorLog.Write("LocalWorkspaceCleanup", ex, includeMessage: false); }
        }
    }
    private static bool IsId(string value) => value.Length == 32 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    private static string OwnedPath(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd('\\', '/');
        var fullChild = Path.GetFullPath(child).TrimEnd('\\', '/');
        if (!string.Equals(Path.GetDirectoryName(fullChild), fullParent, StringComparison.OrdinalIgnoreCase) || !IsId(Path.GetFileName(fullChild)))
            throw new InvalidOperationException("临时目录边界无效。");
        return fullChild;
    }
    internal static void DeleteOwned(string parent, string child)
    {
        var fullChild = OwnedPath(parent, child);
        if (!Directory.Exists(fullChild)) return;
        CheckTree(fullChild);
        Directory.Delete(fullChild, true);
    }
    private void DeleteOwner(string path)
    {
        var owner = OwnedPath(root, path);
        if (!Directory.Exists(owner)) return;
        WebOptions.EnsureNoLinks(owner);
        var marker = Path.Combine(owner, "owner.kind");
        var ownerLease = Path.Combine(owner, "owner.lock");
        WebOptions.EnsureNoLinks(marker); WebOptions.EnsureNoLinks(ownerLease);
        if (!File.Exists(marker) || File.ReadAllText(marker) != Marker) return;
        using (new FileStream(ownerLease, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Keep ownership evidence until every content file has gone. A
            // locked child must not turn the leftover into an unknown directory.
            CheckTree(owner);
            foreach (var entry in Directory.EnumerateFileSystemEntries(owner))
            {
                if (string.Equals(entry, marker, StringComparison.OrdinalIgnoreCase) || string.Equals(entry, ownerLease, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
        }
        try
        {
            File.Delete(ownerLease); File.Delete(marker); Directory.Delete(owner);
        }
        catch
        {
            // A final directory/metadata lock can fail after content cleanup.
            // Preserve the marker so the next sweep can safely retry this owner.
            if (Directory.Exists(owner))
            {
                WebOptions.EnsureNoLinks(owner); WebOptions.EnsureNoLinks(marker); WebOptions.EnsureNoLinks(ownerLease);
                if (!File.Exists(marker)) File.WriteAllText(marker, Marker);
                if (!File.Exists(ownerLease)) using (new FileStream(ownerLease, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            }
            throw;
        }
    }
    private static void CheckTree(string path)
    {
        WebOptions.EnsureNoLinks(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            WebOptions.EnsureNoLinks(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (lease == null) return;
            lease.Dispose(); lease = null;
            try { DeleteOwner(ownerRoot); }
            catch (Exception ex) { ErrorLog.Write("LocalWorkspaceCleanup", ex, includeMessage: false); }
        }
    }
}

internal sealed class LocalReportWorkspace : IDisposable
{
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private const long MaxDirectoryBytes = 256L * 1024 * 1024;
    private const int MaxFiles = 20000, MaxChunkBytes = 1024 * 1024;
    private readonly string parent, directory, root;
    private readonly Action<string> retire;
    private readonly WebBackend connectionOwner;
    private readonly Dictionary<string, Upload> uploads = new(StringComparer.OrdinalIgnoreCase);
    private long reservedBytes;
    private WebBackend? backend;
    private WebContext? context;
    private PendingEdit? pending;
    private string committedId = "";
    private object? committedResult;
    private bool invalidated, disposed;
    internal string Name { get; }
    internal bool HasPending => pending != null;
    internal string Version => backend?.CatalogVersion ?? "uploading";
    private sealed class Upload
    {
        internal string Path = "";
        internal long Total, Received;
    }
    private sealed class Change
    {
        internal string Path = "", OldHash = "", NewHash = "";
        internal byte[] OldBytes = Array.Empty<byte>(), NewBytes = Array.Empty<byte>();
    }
    private sealed class PendingEdit
    {
        internal string Id = Guid.NewGuid().ToString("N");
        internal Change[] Files = Array.Empty<Change>();
        internal Dictionary<string, string> Checks = new(StringComparer.OrdinalIgnoreCase);
        internal object Result = new { };
        internal object Envelope => new {
            localSave = true, saveId = Id,
            checks = Checks.Select(p => new { path = p.Key, hash = p.Value }).ToArray(),
            files = Files.Select(f => new { path = f.Path, oldHash = f.OldHash, newHash = f.NewHash, content = Convert.ToBase64String(f.NewBytes) }).ToArray()
        };
    }
    internal LocalReportWorkspace(string parent, string name, WebBackend connectionOwner, Action<string> retire)
    {
        this.parent = parent; this.connectionOwner = connectionOwner; this.retire = retire;
        Name = name.Length > 120 ? name.Substring(0, 120) : name;
        directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        root = Path.Combine(directory, "Reports");
        WebOptions.EnsureNoLinks(root); Directory.CreateDirectory(root);
    }
    internal object UploadChunk(Dictionary<string, object> args, CancellationToken token)
    {
        ThrowIfDisposed();
        if (backend != null) throw new WebUserException("目录已经启用，请重新扫描后上传。");
        var relative = RelativeInput(Service.Text(args, "path"));
        long offset = Integer(args, "offset"), total = Integer(args, "total");
        if (total < 0 || total > MaxFileBytes || offset < 0 || offset > total)
            throw new WebUserException("XML 文件大小或上传位置无效；单个 XML 最大 32 MiB。");
        var content = Service.Text(args, "content");
        if (content.Length > ((MaxChunkBytes + 2) / 3) * 4) throw new WebUserException("上传分块过大。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(content); }
        catch (FormatException) { throw new WebUserException("XML 上传内容编码无效。"); }
        if (bytes.Length > MaxChunkBytes || offset + bytes.Length > total || (bytes.Length == 0 && total != 0))
            throw new WebUserException("XML 上传分块无效。");
        token.ThrowIfCancellationRequested();
        if (!uploads.TryGetValue(relative, out var entry))
        {
            if (offset != 0) throw new WebUserException("XML 上传缺少起始分块，请重新扫描。");
            if (uploads.Count >= MaxFiles || reservedBytes + total > MaxDirectoryBytes)
                throw new WebUserException("XML 目录超过 20,000 个文件或 256 MiB，请选择较小的报表子目录；未截断导入。");
            entry = new Upload { Path = relative, Total = total };
            uploads.Add(relative, entry); reservedBytes += total;
            var newPath = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!); WebOptions.EnsureNoLinks(newPath);
            using (new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        if (entry.Total != total || !string.Equals(entry.Path, relative, StringComparison.Ordinal))
            throw new WebUserException("目录存在大小写冲突、重复文件或上传内容已变化，请重新扫描。");
        var full = Resolve(relative);
        // A retry after a lost response is allowed only for byte-identical chunks.
        if (offset < entry.Received)
        {
            if (offset + bytes.Length > entry.Received) throw new WebUserException("XML 分块顺序无效。");
            using var check = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            check.Position = offset; var read = new byte[bytes.Length];
            if (check.Read(read, 0, read.Length) != read.Length || !read.SequenceEqual(bytes))
                throw new WebUserException("重复上传内容不一致，请重新扫描。");
        }
        else
        {
            if (offset != entry.Received) throw new WebUserException("XML 分块顺序无效。");
            using var output = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.None);
            output.Position = offset; output.Write(bytes, 0, bytes.Length); output.Flush(true);
            entry.Received += bytes.Length;
        }
        return new { path = relative, received = entry.Received, total, complete = entry.Received == total };
    }
    internal object Activate(CancellationToken token)
    {
        ThrowIfDisposed();
        if (uploads.Count == 0 || uploads.Values.Any(f => f.Total != f.Received)) throw new WebUserException("目录为空或仍有文件未上传完成。");
        token.ThrowIfCancellationRequested();
        backend ??= connectionOwner.CreateLocal(root, token);
        context ??= backend.CreateSession();
        RefreshConnection();
        var data = JsonCodec.Object(JsonCodec.Serialize(backend.Call(context, "bootstrap", new Dictionary<string, object>(), token, _ => { }, false)));
        // Only the local maintenance endpoint exposes these paths. They are
        // relative to the browser-selected root and identify the actual imported
        // definition, even when several reports share the same displayed name.
        var paths = new List<object>();
        if (data.TryGetValue("reports", out var reports) && reports is IEnumerable items)
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var report = JsonCodec.Object(JsonCodec.Serialize(item));
                var id = Service.Text(report, "id");
                var source = backend.GetPublishSource(context, new Dictionary<string, object> { ["reportId"] = id });
                paths.Add(new { id, path = Relative(source.Path) });
            }
        }
        data["queryPaths"] = paths;
        data["name"] = Name; data["local"] = true;
        return data;
    }
    internal object Status() => new { local = true, name = Name, pending = pending?.Envelope, requiresRescan = invalidated, ready = backend != null };
    internal object Call(string method, Dictionary<string, object> args, bool maintenance, CancellationToken token, Action<string> progress)
    {
        ThrowIfDisposed();
        if (method == "localStatus") return Status();
        if (method == "localCommit") return Commit(args, token);
        if (method == "localAbort") return Abort(args);
        EnsureReady();
        if (method == "import" || method == "uploadImport" || method == "checkNewReports" || method == "syncNow" || method == "syncStatus" || method == "resolveSyncConflict" || method == "publishPreview")
            throw new WebUserException("本机目录不使用服务器导入、同步或发布；请在“报表来源 → 打开本机目录”中重新扫描本机目录。");
        RefreshConnection();
        if (maintenance && (method == "sqlEditorSave" || method == "xmlEditorSave" || method == "layoutSave")) return Prepare(method, args, token, progress);
        if (method == "sqlEditorReveal")
        {
            if (!maintenance) throw new WebUserException("当前没有本机目录维护权限。");
            var source = backend!.GetPublishSource(context!, args);
            return new { title = source.Name + " · 本机来源", text = "本机目录：" + Name + "\n查询 XML：" + Relative(source.Path) };
        }
        var response = backend!.Call(context!, method, args, token, progress, maintenance);
        if (method == "sqlEditorOpen" || method == "xmlEditorOpen" || method == "select")
        {
            var dto = JsonCodec.Object(JsonCodec.Serialize(response));
            var checkedPaths = backend.GetReportFiles(context!, args).ToList();
            if (method != "select") checkedPaths.Add(Service.Text(dto, "path"));
            dto["checks"] = checkedPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new { path = Relative(p), hash = ReportWorkDirectory.HashFile(p) }).ToArray();
            response = dto;
        }
        else if (method == "layoutPreview")
        {
            var dto = JsonCodec.Object(JsonCodec.Serialize(response));
            dto["message"] = Service.Text(dto, "message").Replace("不生成备份", "本机保存前会创建备份");
            response = dto;
        }
        return Project(response);
    }
    internal byte[] Export(Dictionary<string, object> args, CancellationToken token)
    { EnsureReady(); return backend!.Export(context!, args, token); }
    internal SqlExportResult ExportSql(Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    { EnsureReady(); return backend!.ExportSql(context!, args, token, progress); }
    private void RefreshConnection()
    {
        var connection = connectionOwner.GetConnection();
        backend!.SetConnectionSettings(connection.Settings, connection.Password);
    }
    private object Prepare(string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        var editPaths = method == "xmlEditorSave" ? backend!.GetXmlEditPlan(context!, args).Paths : backend!.GetEditPaths(context!, args, method == "layoutSave");
        var source = backend!.GetPublishSource(context!, args).Path;
        var paths = editPaths.Concat(new[] { source }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var originals = paths.ToDictionary(Relative, p => File.ReadAllBytes(Resolve(Relative(p))), StringComparer.OrdinalIgnoreCase);
        var prepared = new PendingEdit { Checks = originals.ToDictionary(p => p.Key, p => ReportWorkDirectory.Hash(p.Value), StringComparer.OrdinalIgnoreCase) };
        bool restored = false;
        try
        {
            progress("正在校验并准备本机修改文件；尚未写入本机…");
            prepared.Result = Project(backend.Call(context!, method, args, token, _ => { }, true));
            // Match the public transaction's strict reload validation before any
            // candidate can reach the browser (e.g. reject an empty query root).
            backend.ReloadChangedReportsStrict(paths, token);
            prepared.Files = paths.Select(p => new Change { Path = Relative(p), OldBytes = originals[Relative(p)], NewBytes = File.ReadAllBytes(p) })
                .Where(c => !c.OldBytes.SequenceEqual(c.NewBytes)).ToArray();
            foreach (var item in prepared.Files) { item.OldHash = ReportWorkDirectory.Hash(item.OldBytes); item.NewHash = ReportWorkDirectory.Hash(item.NewBytes); }
        }
        finally
        {
            // The private confirmed mirror must remain the old version while the
            // browser is writing. It is never a public source or the user's file.
            try
            {
                foreach (var original in originals) ReportWorkDirectory.AtomicWrite(Resolve(original.Key), original.Value);
                backend.ReloadChangedReportsStrict(paths, CancellationToken.None);
                restored = true;
            }
            finally { if (!restored) invalidated = true; }
        }
        pending = prepared; committedId = ""; committedResult = null;
        return prepared.Envelope;
    }
    private object Commit(Dictionary<string, object> args, CancellationToken token)
    {
        ThrowIfDisposed();
        var id = Service.Text(args, "saveId");
        if (pending == null && id.Length > 0 && id == committedId) return committedResult!;
        if (pending == null || id != pending.Id) throw new WebUserException("本机保存确认已失效，请核对文件后重新扫描目录。");
        var edit = pending;
        if (!args.TryGetValue("files", out var raw) || !(raw is IEnumerable list)) throw new WebUserException("缺少本机回读校验结果。");
        var confirmed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            if (!(item is Dictionary<string, object> row)) throw new WebUserException("本机回读校验结果无效。");
            var path = RelativeInput(Service.Text(row, "path"));
            if (confirmed.ContainsKey(path)) throw new WebUserException("本机回读校验结果重复。");
            confirmed.Add(path, Service.Text(row, "hash"));
        }
        if (confirmed.Count != edit.Files.Length || edit.Files.Any(f => !confirmed.TryGetValue(f.Path, out var hash) || hash != f.NewHash))
            throw new WebUserException("本机文件未全部回读校验成功；服务器未启用修改后的定义。");
        token.ThrowIfCancellationRequested();
        try
        {
            foreach (var file in edit.Files) ReportWorkDirectory.AtomicWrite(Resolve(file.Path), file.NewBytes);
            if (edit.Files.Length > 0) backend!.ReloadChangedReportsStrict(edit.Files.Select(f => Resolve(f.Path)), CancellationToken.None);
            var confirmedResult = JsonCodec.Object(JsonCodec.Serialize(edit.Result));
            confirmedResult["saved"] = true; confirmedResult["reloaded"] = true; confirmedResult["changed"] = edit.Files.Length > 0;
            confirmedResult["message"] = edit.Files.Length > 0
                ? "本机文件已回读确认，个人报表副本已更新；未执行数据查询，未验证 HIS 显示或打印效果。"
                : "本机文件内容一致，无需重写；个人报表副本已确认。";
            committedResult = confirmedResult; committedId = edit.Id; pending = null;
            return committedResult;
        }
        catch
        {
            // The browser may already have committed. Keep this edit pending so
            // a retry can repair/confirm the private mirror without another local write.
            throw new WebUserException("本机文件可能已保存，但服务器确认未完成；请使用继续确认，勿重复覆盖本机文件。");
        }
    }
    private object Abort(Dictionary<string, object> args)
    {
        if (pending == null || Service.Text(args, "saveId") != pending.Id) throw new WebUserException("本机保存记录已失效，请重新扫描目录。");
        pending = null; invalidated = true;
        context?.Dispose(); context = null;
        return new { aborted = true, requiresRescan = true };
    }
    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (pending != null) throw new WebUserException("本机保存尚未确认，请先完成保存确认或恢复，再继续操作。");
        if (invalidated) throw new WebUserException("本机目录需要重新扫描；未继续使用旧报表定义。");
        if (backend == null || context == null) throw new WebUserException("本机目录尚未加载完成。");
    }
    private string Resolve(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, RelativeInput(relative).Replace('/', Path.DirectorySeparatorChar)));
        WebBackend.ValidatePath(path, root);
        return path;
    }
    private string Relative(string path)
    {
        WebBackend.ValidatePath(path, root);
        return path.Substring(root.Length + 1).Replace('\\', '/');
    }
    private static string RelativeInput(string path)
    {
        string normalized;
        try { normalized = ReportWorkDirectory.Normalize(path); }
        catch (SyncSafetyException) { throw new WebUserException("本机 XML 相对路径无效。"); }
        if (normalized.Length > 220 || !normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new WebUserException("仅支持长度适当的 XML 相对路径。");
        return normalized;
    }
    private static long Integer(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || !long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var result))
            throw new WebUserException("上传文件位置或大小无效。");
        return result;
    }
    private object Project(object value)
    {
        // Only path-shaped metadata is rewritten. SQL/XML content stays byte/text-identical.
        object? Walk(object? current, string key = "")
        {
            if (current is IDictionary<string, object> map) return map.ToDictionary(p => p.Key, p => Walk(p.Value, p.Key));
            if (current is string text)
            {
                bool pathMetadata = key.EndsWith("path", StringComparison.OrdinalIgnoreCase) || key.Equals("paths", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("root", StringComparison.OrdinalIgnoreCase) || key.Equals("query", StringComparison.OrdinalIgnoreCase);
                if (pathMetadata && string.Equals(text, root, StringComparison.OrdinalIgnoreCase)) return Name;
                if (pathMetadata && text.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return text.Substring(root.Length + 1).Replace('\\', '/');
                if (key.Equals("warnings", StringComparison.OrdinalIgnoreCase) || key.Equals("errors", StringComparison.OrdinalIgnoreCase) || key.Equals("message", StringComparison.OrdinalIgnoreCase))
                    return text.Replace(root + Path.DirectorySeparatorChar, Name + "/").Replace(root, Name);
                return text;
            }
            if (current is IEnumerable values) return values.Cast<object>().Select(v => Walk(v, key)).ToArray();
            return current;
        }
        return Walk(JsonCodec.Create().DeserializeObject(JsonCodec.Serialize(value)))!;
    }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(LocalReportWorkspace)); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        context?.Dispose(); backend?.Dispose(); pending = null;
        retire(directory);
    }
}
