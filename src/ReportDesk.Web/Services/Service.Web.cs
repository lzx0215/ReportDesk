using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using ReportDesk.Core;

namespace ReportDesk.Host;

// Compiled into the Web assembly with the original Host Service and editing partials.
// No desktop IPC, process startup or per-tab disk configuration is involved.
internal sealed partial class Service
{
    private readonly Dictionary<string, List<RelatedXmlFile>> webRelated = new(StringComparer.OrdinalIgnoreCase);
    private Action<string>? webValidatePath;
    private string webRoot = "";

    partial void OnReportsMerged(ImportSummary summary)
    {
        foreach (var report in summary.Reports) webValidatePath?.Invoke(report.SourcePath);
        if (summary.Inventory != null) webValidatePath?.Invoke(summary.Inventory.Root);
        foreach (var pair in summary.RelatedFiles)
        {
            foreach (var path in pair.Value.SelectMany(f => f.Paths)) webValidatePath?.Invoke(path);
        }
        foreach (var report in summary.Reports) reloadRequired.Remove(report.Id);
        foreach (var pair in summary.RelatedFiles)
        {
            webRelated[pair.Key] = pair.Value;
        }
    }

    private Service(Service shared)
    {
        store = shared.store;
        sourcesStore = shared.sourcesStore;
        testConnection = shared.testConnection;
        visibility = shared.visibility;
        allReports = shared.allReports;
        locations = shared.locations;
        offline = shared.offline;
        configDirectory = shared.configDirectory;
        catalog = new Catalog();
        sourcesRestored = true;
        WebRefresh(shared);
    }

    internal Service WebCreateContext() => new(this);

    // Definitions are immutable once published. Merge replaces objects, never edits
    // published ones. Each Service owns its list and all query/lookup/editor state.
    internal void WebRefresh(Service shared)
    {
        catalog = new Catalog { Reports = new List<ReportDefinition>(shared.catalog.Reports), Connection = shared.catalog.Connection };
        password = shared.password;
        webValidatePath = shared.webValidatePath;
        webRoot = shared.webRoot;
        webRelated.Clear();
        foreach (var pair in shared.webRelated) webRelated.Add(pair.Key, pair.Value);
        importRoots.Clear();
        foreach (var pair in shared.importRoots) importRoots.Add(pair.Key, pair.Value);
        reloadRequired.Clear();
        reloadRequired.UnionWith(shared.reloadRequired);
        startupWarnings.Clear();
        startupWarnings.AddRange(shared.startupWarnings);
        // In particular, do NOT Clear(): already completed results remain usable.
    }

    internal void WebPublishDefinitions(Service changed)
    {
        catalog.Reports = new List<ReportDefinition>(changed.catalog.Reports);
        webRelated.Clear();
        foreach (var pair in changed.webRelated) webRelated.Add(pair.Key, pair.Value);
        importRoots.Clear();
        foreach (var pair in changed.importRoots) importRoots.Add(pair.Key, pair.Value);
        reloadRequired.Clear();
        reloadRequired.UnionWith(changed.reloadRequired);
    }

    internal void WebInitialize(string root, Action<string> checkPath)
    {
        webValidatePath = checkPath;
        webRoot = root;
        var sources = new List<ImportSource>();
        try { sources = sourcesStore.Load(); }
        catch (Exception ex)
        {
            ErrorLog.Write("WebReadSources", ex, includeMessage: false);
            startupWarnings.Add("已保存的导入来源无法读取，请在维护页检查配置。");
        }
        if (sources.Count == 0) sources.Add(new ImportSource { Path = root, Folder = true });
        foreach (var source in sources)
        {
            try
            {
                checkPath(source.Path);
                var imported = source.Folder ? ReportImporter.ImportFolder(source.Path) : ReportImporter.ImportWithRelated(source.Path);
                MergeImport(imported);
                if (imported.Errors.Count > 0) startupWarnings.Add("部分报表来源读取失败，请在维护页检查。");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("WebRestoreSource", ex, includeMessage: false);
                startupWarnings.Add("部分报表来源不可访问或不在受控目录内，已保留配置。");
            }
        }
        sourcesRestored = true;
    }

    internal void WebValidateRememberedSources(Action<string> checkPath)
    {
        foreach (var source in sourcesStore.Load()) checkPath(source.Path);
    }

    internal void WebValidateReport(Dictionary<string, object> args, Action<string> checkPath)
    {
        var r = Report(args);
        checkPath(r.SourcePath);
        if (importRoots.TryGetValue(r.Id, out var root)) checkPath(root);
        else importRoots[r.Id] = Path.GetDirectoryName(r.SourcePath)!;
        if (sqlEditorFile != null) checkPath(sqlEditorFile.Path);
        if (layoutPlan != null) checkPath(layoutPlan.Path);
    }

    internal void WebValidateAllReports(Action<string> checkPath)
    {
        foreach (var r in catalog.Reports.Where(r => !r.IsDemo)) checkPath(r.SourcePath);
    }

    internal void WebInjectConnection(ConnectionSettings settings, string secret)
    {
        // Never retain the caller's mutable configuration object.
        var json = new JavaScriptSerializer();
        catalog.Connection = json.Deserialize<ConnectionSettings>(json.Serialize(settings));
        catalog.Connection.ProtectedPassword = "";
        password = secret ?? "";
    }

    internal ReportDesk.Web.Services.WebConnectionSnapshot WebGetConnection()
    {
        var json = new JavaScriptSerializer();
        var settings = json.Deserialize<ConnectionSettings>(json.Serialize(catalog.Connection));
        settings.ProtectedPassword = "";
        return new ReportDesk.Web.Services.WebConnectionSnapshot(settings, password, offline);
    }

    internal IReadOnlyList<string> WebEditPaths(Dictionary<string, object> args, bool layout)
    {
        var file = EditorFile(args);
        webValidatePath?.Invoke(file.Path);
        if (!layout) return new[] { file.Path };
        var plan = layoutPlan;
        if (plan == null || layoutToken.Length == 0 || Text(args, "previewToken") != layoutToken ||
            plan.QueryHash != file.Hash || plan.SourceIndex != Number(args, "sourceIndex", -1) || plan.Sql != Text(args, "sql"))
            throw new InvalidOperationException("列预览已失效，请重新同步报表列。");
        webValidatePath?.Invoke(plan.Path);
        return new[] { file.Path, plan.Path };
    }

    internal Exception WebSafeAdminError(InvalidOperationException exception, Dictionary<string, object> args)
    {
        var message = WebMetadataText(ErrorLog.Sanitize(exception.Message, Secrets(args)));
        return new ReportDesk.Web.WebUserException(message);
    }

    private string WebMetadataText(string text)
    {
        // DTO projection prevents structural leaks; diagnostic prose also needs
        // filtering because importer errors may contain XML paths or SQL excerpts.
        text = Regex.Replace(text ?? "", @"(?i)(?:[a-z]:[\\/]|\\\\)[^\s\r\n\""<>|]*", "[服务器路径]");
        text = Regex.Replace(text, @"(?is)\bSELECT\s+.+?\bFROM\b.*", "[查询定义仅维护端可见]");
        return text;
    }

    private string[] WebIssues(IEnumerable<string> issues) => issues.Select(i => AdaptationGuidance.For(i).Title).Distinct().ToArray();

    private object WebList() => Visible().Select(r => new {
        id = r.Id, name = WebMetadataText(r.Name), aliases = WebMetadataText(r.Aliases), notes = WebMetadataText(r.Notes),
        locations = locations.For(r).Select(l => new { Path = WebMetadataText(l.Path), Evidence = WebMetadataText(l.Evidence), l.Match, l.Candidate, l.Active }).ToArray(),
        status = r.Status, demo = r.IsDemo, issues = WebIssues(r.Issues), verified = r.Verified
    }).ToArray();

    internal object WebBootstrap() => new { reports = WebList(), settings = new { hasPassword = password.Length > 0, offline }, offline, demoVisible = false, warnings = startupWarnings.Select(WebMetadataText).ToArray() };

    private object WebDetails(Dictionary<string, object> args)
    {
        // The original projection is the single source for parameter and source
        // selection semantics. Copy the DTO, explicitly remove private metadata.
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        var dto = json.Deserialize<Dictionary<string, object>>(json.Serialize(Details(args)));
        dto.Remove("path");
        var r = Report(args);
        dto["definitionHash"] = r.SourceHash;
        dto["issues"] = WebIssues(r.Issues);
        var index = Convert.ToInt32(dto["source"]);
        dto["selectedIssues"] = reloadRequired.Contains(r.Id) ? new[] { ReloadRequiredMessage } :
            WebIssues(r.Queries.Count > 0 ? ReportReadiness.IssuesFor(r, r.Queries[index]) : r.Issues);
        dto["sources"] = r.Queries.Select((q, i) => new { q, i }).Where(x => x.q.Kind != "ConditionUsing")
            .Select(x => new { index = x.i, name = WebMetadataText(x.q.ToString()) + (ReportReadiness.IssuesFor(r, x.q).Count > 0 ? " · 待适配" : ""),
                issues = WebIssues(ReportReadiness.IssuesFor(r, x.q)) }).ToArray();
        return WebCleanMetadata(dto)!;
    }

    private object? WebCleanMetadata(object? value)
    {
        if (value is string text) return WebMetadataText(text);
        if (value is IDictionary<string, object> dictionary)
            return dictionary.ToDictionary(p => p.Key, p => WebCleanMetadata(p.Value));
        if (value is IEnumerable list) return list.Cast<object>().Select(WebCleanMetadata).ToArray();
        return value;
    }

    internal object WebHandle(string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            // Explicit allow-list: never forward an arbitrary Host method.
            switch (method)
            {
                case "bootstrap": return WebBootstrap();
                case "list": return WebList();
                case "select": Clear(); return WebDetails(args);
                case "relatedFiles":
                    var related = Report(args);
                    var files = webRelated.TryGetValue(related.Id, out var matched) ? matched : new List<RelatedXmlFile>();
                    return new { title = WebMetadataText(related.Name), summaryOnly = true,
                        files = files.Select(f => new { Role = WebMetadataText(f.Role), f.Status, count = f.Paths.Count }).ToArray(),
                        warnings = Array.Empty<string>() };
                case "definition":
                    var r = Report(args);
                    var names = r.Queries.Select(q => q.Sql).Concat(r.Parameters.Select(p => p.LookupSql))
                        .SelectMany(sql => Regex.Matches(sql, @"&([\p{L}_][\p{L}\p{Nd}_]*)\.Text\b").Cast<Match>())
                        .Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return new { title = WebMetadataText(r.Name), text = "", textParameterNames = names };
                case "query": case "lookup":
                    var current = Report(args);
                    if (!string.Equals(Text(args, "definitionHash"), current.SourceHash, StringComparison.Ordinal) || current.SourceHash.Length == 0)
                        throw new InvalidOperationException("报表定义已变化或缺少版本标识，请重新选择报表并核对条件后查询。");
                    return Handle(method, args, token, message => progress(WebMetadataText(message)));
                case "view": case "page": case "lookupPage": case "lookupAll": case "clear":
                    return Handle(method, args, token, message => progress(WebMetadataText(message)));
                default: throw new InvalidOperationException("此操作不在普通查询接口白名单内。");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // SQL validator messages can contain fragments. Only controlled
            // messages are exposed; detailed failure diagnostics stay server-side.
            var message = WebMetadataText(ex.Message);
            if (message.Contains("SQL") || message.Contains("模板语法") || message.Contains("ORA-"))
                message = "查询定义检查或执行失败，请联系维护人员检查；未绕过原有执行规则。";
            throw new ReportDesk.Web.WebUserException(message);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("WebQuery", ex, includeMessage: false);
            throw new ReportDesk.Web.WebUserException("操作失败，请联系维护人员查看服务端日志。");
        }
    }

    internal byte[] WebExport(Dictionary<string, object> args, CancellationToken token)
    {
        try
        {
            var table = Current(args);
            using var snapshot = table.DefaultView.ToTable();
            using var stream = new MemoryStream();
            XlsxExporter.Export(snapshot, stream, context, token);
            return stream.ToArray();
        }
        catch (InvalidOperationException ex) { throw new ReportDesk.Web.WebUserException(WebMetadataText(ex.Message)); }
    }

    internal object WebAdminHandle(string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        switch (method)
        {
            case "checkNewReports":
                if (sourcesStore.Load().Count > 0) return Handle(method, args, token, progress);
                // The configured root is the default source even before an admin
                // has explicitly persisted any additional import sources.
                var discovered = ReportImporter.ImportFolder(webRoot, token, progress);
                var existing = new HashSet<string>(catalog.Reports.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                discovered.Reports.RemoveAll(r => !existing.Add(r.Id));
                token.ThrowIfCancellationRequested();
                MergeImport(discovered);
                return new { reports = List(), addedCount = discovered.Reports.Count, visibleAddedCount = discovered.Reports.Count,
                    sourceCount = 1, incomplete = discovered.IncompleteReports.Count, errors = discovered.Errors };
            case "recheck":
                var previous = Visible().ToArray();
                var checkedReports = ReportImporter.Recheck(previous, token, progress);
                token.ThrowIfCancellationRequested();
                reloadRequired.UnionWith(previous.Select(r => r.Id));
                MergeImport(checkedReports);
                Clear();
                return new { reports = List(), checkedCount = checkedReports.Reports.Count, errors = checkedReports.Errors,
                    incomplete = checkedReports.IncompleteReports.Count };
            case "sqlEditorReveal":
                var report = Report(args);
                return new { title = report.Name + " · 服务器来源", text = "查询 XML：" + report.SourcePath + "\n此文件位于服务器受控报表目录；浏览器不会打开服务器资源管理器。" };
            case "discoverTns":
                return TnsDiscovery.FindFiles(new[] { configDirectory, Path.Combine(configDirectory, "network", "admin") })
                    .Where(p => { try { ReportDesk.Web.Services.WebBackend.ValidatePath(p, configDirectory); return true; } catch { return false; } })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            case "settings": case "saveSettings": case "testConnection": case "tnsAliases":
            case "import": case "relatedFiles":
            case "sqlEditorOpen": case "sqlEditorCheck": case "sqlEditorSave": case "reloadReport": case "layoutPreview": case "layoutSave":
                return Handle(method, args, token, progress);
            default: throw new InvalidOperationException("此操作不在维护接口白名单内。");
        }
    }

    internal object WebReloadChanged(IReadOnlyCollection<string> paths, string root, CancellationToken token)
    {
        if (paths.Count == 0)
        {
            var all = ReportImporter.ImportFolder(root, token);
            if (all.Errors.Count > 0) throw new InvalidOperationException("报表目录未能完整重载，请保留事务恢复标记并检查文件。");
            var merged = new Catalog { Reports = new List<ReportDefinition>(catalog.Reports), Connection = catalog.Connection };
            ReportImporter.Merge(merged, all);
            var loadedIds = new HashSet<string>(all.Reports.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
            merged.Reports.RemoveAll(r => !loadedIds.Contains(r.Id));
            OnReportsMerged(all);
            catalog = merged;
            importRoots.Clear();
            foreach (var report in all.Reports) importRoots[report.Id] = root;
            reloadRequired.Clear();
            return new { reloaded = all.Reports.Count, failed = Array.Empty<string>() };
        }
        var names = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(catalog.Reports.Select(r => r.SourcePath), StringComparer.OrdinalIgnoreCase);
        // A template can influence adapter eligibility; conservatively reload all
        // known reports if the changed batch includes companion XML.
        bool companions = paths.Any(p => !known.Contains(p));
        var affected = catalog.Reports.Where(r => !r.IsDemo && (companions || names.Contains(r.SourcePath))).ToArray();
        int loaded = 0;
        var failed = new List<string>();
        reloadRequired.UnionWith(affected.Select(r => r.Id));
        foreach (var report in affected)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                webValidatePath?.Invoke(report.SourcePath);
                if (!File.Exists(report.SourcePath))
                {
                    catalog.Reports.Remove(report); webRelated.Remove(report.Id); importRoots.Remove(report.Id);
                    reloadRequired.Remove(report.Id); continue;
                }
                ReloadSingle(report, token); loaded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ErrorLog.Write("WebReloadChanged", ex, includeMessage: false); failed.Add(report.Id); }
        }
        foreach (var path in paths.Where(p => !known.Contains(p)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) continue;
                var fresh = ReportImporter.ImportFile(path);
                if (fresh == null || !ReportClassification.IsStandalone(fresh)) continue;
                var imported = new ImportSummary(); imported.Reports.Add(fresh);
                MergeImport(imported); importRoots[fresh.Id] = root; loaded++;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            { ErrorLog.Write("WebImportChanged", ex, includeMessage: false); failed.Add(Path.GetFileName(path)); }
        }
        if (failed.Count > 0) throw new InvalidOperationException("部分报表未能重载，整批目录快照未发布；请回滚文件后重试。");
        if (companions)
        {
            var inventory = ReportFileDiscovery.Scan(root, token);
            foreach (var report in catalog.Reports.Where(r => !r.IsDemo))
            {
                var links = ReportFileDiscovery.Match(report.SourcePath, inventory, token);
                foreach (var path in links.SelectMany(l => l.Paths)) webValidatePath?.Invoke(path);
                webRelated[report.Id] = links;
            }
        }
        return new { reloaded = loaded, failed = failed.ToArray() };
    }
}
