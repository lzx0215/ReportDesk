using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReportDesk.Core;

namespace ReportDesk.Host;

internal sealed partial class Service
{
    private SqlXmlSnapshot? sqlEditorFile;
    private string sqlEditorToken = "", sqlEditorReportId = "";
    private readonly HashSet<string> reloadRequired = new(StringComparer.OrdinalIgnoreCase);
    private const string ReloadRequiredMessage = "原 XML 与内存定义未同步，请重新加载当前报表后再查询。";

    private object EditorData(ReportDefinition r, SqlXmlSnapshot file) => new {
        reportId = r.Id, title = r.Name, path = file.Path, hash = file.Hash, token = sqlEditorToken,
        stale = file.Hash != r.SourceHash, demo = false,
        sources = file.Sources.Select(s => new { index = s.Index, queryIndex = s.QueryIndex,
            name = s.Name, kind = s.Kind, sql = s.Sql, editable = s.Editable }).ToArray()
    };

    private SqlXmlSnapshot EditorFile(Dictionary<string, object> a)
    {
        var report = Report(a); // Recheck visibility; never accept a renderer-supplied file path.
        if (sqlEditorFile == null || sqlEditorToken.Length == 0 || Text(a, "token") != sqlEditorToken ||
            report.Id != sqlEditorReportId || report.SourcePath != sqlEditorFile.Path)
            throw new InvalidOperationException("SQL 编辑会话已失效，请保留草稿并重新打开编辑窗口。");
        return sqlEditorFile;
    }

    private ReportDefinition ReloadSingle(ReportDefinition previous, CancellationToken token, string? expectedHash = null)
    {
        reloadRequired.Add(previous.Id);
        Clear(); // Do not leave an old result/export usable with the new definition.
        token.ThrowIfCancellationRequested();
        var fresh = ReportImporter.ImportFile(previous.SourcePath);
        if (fresh == null || !ReportClassification.IsStandalone(fresh))
            throw new InvalidOperationException("来源已不是有效的独立报表定义，已禁止继续执行旧 SQL。");
        if (fresh.Id != previous.Id || (expectedHash != null && fresh.SourceHash != expectedHash))
            throw new InvalidOperationException("文件在保存后再次发生变化，请重新加载当前报表。");
        token.ThrowIfCancellationRequested();
        var imported = new ImportSummary(); imported.Reports.Add(fresh);
        ReportImporter.Merge(catalog, imported);
        reloadRequired.Remove(previous.Id);
        return fresh;
    }

    private object HandleSqlEditing(string method, Dictionary<string, object> a, CancellationToken token, Action<string> progress)
    {
        try
        {
            switch (method)
            {
                case "sqlEditorOpen":
                    var r = Report(a);
                    if (r.IsDemo)
                    {
                        sqlEditorFile = null; sqlEditorToken = ""; sqlEditorReportId = "";
                        return new { reportId = r.Id, title = r.Name, path = "内置演示（只读）", hash = "", token = "", stale = false, demo = true,
                            sources = r.Queries.Select((q, i) => new { index = i, queryIndex = i, name = q.Name, kind = q.Kind, sql = q.Sql, editable = false }).ToArray() };
                    }
                    var opened = SqlXmlEditor.Read(r.SourcePath);
                    token.ThrowIfCancellationRequested();
                    sqlEditorFile = opened; sqlEditorReportId = r.Id; sqlEditorToken = Guid.NewGuid().ToString("N");
                    if (opened.Hash != r.SourceHash) { reloadRequired.Add(r.Id); Clear(); }
                    return EditorData(r, opened);
                case "sqlEditorCheck":
                    var checkFile = EditorFile(a);
                    var original = checkFile.Sources.SingleOrDefault(s => s.Index == Number(a, "sourceIndex", -1));
                    if (original == null) throw new InvalidOperationException("请选择有效的数据源。");
                    var sql = Text(a, "sql");
                    if (sql.Length > SqlXmlEditor.MaxSqlCharacters) throw new InvalidOperationException("SQL 超过编辑大小限制。");
                    var fresh = ReportImporter.ImportFile(checkFile.Path);
                    if (fresh == null || fresh.SourceHash != checkFile.Hash)
                        throw new InvalidOperationException("原 XML 已改变，请保留草稿并重新读取后检查。");
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var compiled = SqlTemplate.Compile(sql, null, ReportImporter.MultipleNames(fresh));
                        var missing = compiled.RequiredNames.Select(ReportImporter.ParameterName).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(n => !fresh.Parameters.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToArray();
                        return new { passed = true, required = compiled.RequiredNames.ToArray(), missing,
                            message = "只读模板静态检查通过；未连接数据库，不代表 Oracle 执行成功或 HIS 版式已匹配。" };
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new { passed = false, required = Array.Empty<string>(), missing = Array.Empty<string>(),
                            message = ErrorLog.Sanitize(ex.Message, new[] { sql }) + "\n此检查不阻止保存文件；原有查询执行保护保持不变。" };
                    }
                case "sqlEditorSave":
                    var file = EditorFile(a);
                    var report = Report(a);
                    progress("正在覆盖原 XML 中的当前 SQL，未执行数据库查询…");
                    SqlXmlSaveResult saved;
                    try { saved = SqlXmlEditor.Save(file, Number(a, "sourceIndex", -1), Text(a, "sql"), token); }
                    catch (SqlXmlVerificationException)
                    {
                        reloadRequired.Add(report.Id); Clear();
                        throw;
                    }
                    sqlEditorFile = saved.Snapshot; sqlEditorToken = Guid.NewGuid().ToString("N");
                    // After file replacement, cancellation or reload failure must not masquerade as a failed save.
                    bool reloaded = false;
                    string message;
                    try
                    {
                        report = ReloadSingle(report, CancellationToken.None, saved.Snapshot.Hash);
                        reloaded = true;
                        message = saved.Changed ? "原 XML 已保存，磁盘内容已回读核对，并重新加载；未执行 SQL，请核对条件后手动查询。" : "SQL 与磁盘文件一致，无需写入；已重新加载当前报表。";
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Write("ReloadSavedReport", ex, includeMessage: false);
                        message = "原 XML 已保存，但重新加载失败。已禁止执行旧定义；请修复来源后点击重新加载。";
                    }
                    return new { saved = true, changed = saved.Changed, reloaded, savedPath = saved.Snapshot.Path,
                        message, editor = EditorData(report, saved.Snapshot) };
                case "reloadReport":
                    var previous = Report(a);
                    if (previous.IsDemo) throw new InvalidOperationException("内置演示没有外部文件需要重新加载。");
                    progress("正在重新加载当前报表，未扫描其他报表…");
                    var reloadedReport = ReloadSingle(previous, token);
                    sqlEditorFile = null; sqlEditorToken = ""; sqlEditorReportId = "";
                    return new { reportId = reloadedReport.Id, hash = reloadedReport.SourceHash, reloaded = true };
                default:
                    throw new InvalidOperationException("不支持的后台操作。");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            ErrorLog.Write("SqlXmlEditing", ex, includeMessage: false);
            throw new InvalidOperationException("XML 操作未完成。请检查文件、编码、占用状态及目录写入权限；保留草稿，并核对原文件后重试。");
        }
    }
}
