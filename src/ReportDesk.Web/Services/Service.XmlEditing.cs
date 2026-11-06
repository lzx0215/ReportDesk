using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReportDesk.Core;

namespace ReportDesk.Host;

internal sealed partial class Service
{
    private FullXmlSnapshot? webXmlFile;
    private string webXmlToken = "", webXmlReportId = "", webXmlKind = "", webXmlSourcePath = "", webXmlSourceHash = "", webXmlNotice = "";

    private object WebXmlEditorData(ReportDefinition report) => new {
        reportId = report.Id, title = report.Name, kind = webXmlKind, path = webXmlFile!.Path,
        token = webXmlToken, text = webXmlFile.Text, matchNotice = webXmlNotice
    };

    private FullXmlSnapshot WebXmlSession(Dictionary<string, object> args)
    {
        var report = Report(args);
        if (webXmlFile == null || webXmlToken.Length == 0 || Text(args, "token") != webXmlToken ||
            report.Id != webXmlReportId || report.SourcePath != webXmlSourcePath)
            throw new InvalidOperationException("XML 编辑会话已失效，请保留草稿并重新打开。");
        webValidatePath?.Invoke(report.SourcePath);
        webValidatePath?.Invoke(webXmlFile.Path);
        if (webXmlKind != "query" && SqlXmlEditor.Read(report.SourcePath).Hash != webXmlSourceHash)
            throw new InvalidOperationException("关联的查询 XML 已变化，请保留草稿并重新打开模板。");
        return webXmlFile;
    }

    internal IReadOnlyList<string> WebXmlEditPaths(Dictionary<string, object> args)
        => new[] { WebXmlSession(args).Path };

    internal string WebXmlEditExpectedHash(Dictionary<string, object> args)
        => FullXmlEditor.Prepare(WebXmlSession(args), Text(args, "text")).Hash;

    private object HandleWebXmlEditing(string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        switch (method)
        {
            case "xmlEditorOpen":
            {
                var report = Report(args);
                if (report.IsDemo) throw new InvalidOperationException("演示报表没有可编辑的服务器文件。");
                var kind = Text(args, "kind");
                if (kind != "query" && kind != "layout" && kind != "detail") throw new InvalidOperationException("请选择查询设置、主报表设置或明细报表设置。");
                webValidatePath?.Invoke(report.SourcePath);
                var sourceHash = SqlXmlEditor.Read(report.SourcePath).Hash;
                var path = report.SourcePath;
                var notice = "";
                if (kind != "query")
                {
                    if (!webRelated.TryGetValue(report.Id, out var related)) throw new InvalidOperationException("没有找到该报表的模板关联，请检查导入来源。");
                    var role = kind == "layout" ? "主表版式" : "明细版式";
                    var matches = related.Where(f => f.Role.StartsWith(role, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1 || matches[0].Paths.Count != 1 || (matches[0].Status != "Matched" && matches[0].Status != "Candidate"))
                        throw new InvalidOperationException("未找到唯一的" + (kind == "layout" ? "主报表" : "明细报表") + "模板，请核对报表关联后重试。");
                    path = matches[0].Paths[0];
                    notice = matches[0].Status == "Candidate" ? "这是按名称关联的候选模板，保存前必须核对它确属该报表。" : "模板根据报表关联定位，请核对保存路径。";
                }
                webValidatePath?.Invoke(path);
                var opened = FullXmlEditor.Read(path, kind == "query" ? "ReportQueryInfo" : "Spread");
                token.ThrowIfCancellationRequested();
                webXmlFile = opened; webXmlReportId = report.Id; webXmlKind = kind; webXmlSourcePath = report.SourcePath;
                webXmlSourceHash = sourceHash; webXmlNotice = notice; webXmlToken = Guid.NewGuid().ToString("N");
                return WebXmlEditorData(report);
            }
            case "xmlEditorCheck":
            {
                var file = WebXmlSession(args);
                FullXmlEditor.Validate(file, Text(args, "text"));
                return new { passed = true, message = "XML 格式和根元素检查通过；未验证报表业务逻辑、SQL 或 HIS 兼容性。" };
            }
            case "xmlEditorSave":
            {
                var file = WebXmlSession(args);
                var report = Report(args);
                progress("正在校验并保存服务器 XML，未执行查询…");
                var result = FullXmlEditor.Save(file, Text(args, "text"), token);
                webXmlFile = result.Snapshot;
                if (webXmlKind == "query") webXmlSourceHash = result.Snapshot.Hash;
                webXmlToken = Guid.NewGuid().ToString("N");
                return new { saved = true, changed = result.Changed, reloaded = true,
                    message = "XML 已回读校验；报表定义将在保存事务中重新加载。未验证 SQL 或 HIS 显示效果。", editor = WebXmlEditorData(report) };
            }
            default: throw new InvalidOperationException("不支持的 XML 编辑操作。");
        }
    }
}
