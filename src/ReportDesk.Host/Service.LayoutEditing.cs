using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ReportDesk.Core;

namespace ReportDesk.Host;

internal sealed partial class Service
{
    private LayoutPlan? layoutPlan;
    private string layoutToken = "";
    private void ClearLayoutPlan() { layoutPlan = null; layoutToken = ""; }

    private object PreviewLayout(Dictionary<string, object> a, CancellationToken token, Action<string> progress)
    {
        ClearLayoutPlan();
        var file = EditorFile(a);
        int index = Number(a, "sourceIndex", -1);
        ReportLayoutEditor.ValidateSource(file, index);
        var fresh = ReportImporter.ImportFile(file.Path);
        if (fresh == null || fresh.SourceHash != file.Hash) throw new InvalidOperationException("查询 XML 已改变，请保留草稿并重新读取。");
        var source = file.Sources.Single(s => s.Index == index);
        string sql = Text(a, "sql");
        if (sql.Length > SqlXmlEditor.MaxSqlCharacters) throw new InvalidOperationException("SQL 超过编辑大小限制。");
        // Compile both with real bound conditions; never guess parameter values or split SQL on commas.
        var oldValues = Values(fresh, source.Sql, a); var newValues = Values(fresh, sql, a);
        if (offline) throw new InvalidOperationException("离线模式不能识别数据库字段；请连接本机测试数据库后重试。");
        if (password.Length == 0) throw new InvalidOperationException("请先连接数据库，再同步报表列。只读取字段信息，不读取明细行。");
        progress("正在定位对应模板并读取数据库字段信息，不读取明细行…");
        string root = importRoots.TryGetValue(fresh.Id, out var remembered) ? remembered : ReportFileDiscovery.InferRoot(file.Path);
        var inventory = ReportFileDiscovery.Scan(root, token);
        string role = source.Kind == "MainReportUsing" ? "主表版式" : "明细版式";
        var matches = ReportFileDiscovery.Match(file.Path, inventory, token).Where(m => m.Role.StartsWith(role, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].Paths.Count != 1 || (matches[0].Status != "Matched" && matches[0].Status != "Candidate"))
            throw new InvalidOperationException("未找到唯一对应的报表模板。请导入完整报表目录并核对关联文件；未修改原文件。");
        string[] oldFields, newFields;
        try
        {
            oldFields = OracleQueryService.DescribeColumns(catalog.Connection, password, source.Sql, oldValues, token, oldValues.Multiple);
            newFields = OracleQueryService.DescribeColumns(catalog.Connection, password, sql, newValues, token, newValues.Multiple);
        }
        catch (Oracle.ManagedDataAccess.Client.OracleException ex)
        {
            ErrorLog.Write("DescribeLayoutColumns", ex, includeMessage: false);
            throw new InvalidOperationException("数据库字段识别失败（Oracle 错误码 " + ex.Number + "）。请检查原 SQL、新 SQL、查询条件和数据库权限。未修改任何 XML。");
        }
        var plan = ReportLayoutEditor.Preview(file, index, sql, matches[0].Paths[0], oldFields, newFields);
        token.ThrowIfCancellationRequested();
        layoutPlan = plan; layoutToken = Guid.NewGuid().ToString("N");
        return new { previewToken = layoutToken, queryPath = file.Path, layoutPath = plan.Path, reconciled = plan.Reconciled,
            matchNotice = matches[0].Status == "Candidate" ? "按文件名找到候选模板，并已核对数据源标记及列数。请确认下面的模板路径确属此报表。" : "已按查询 XML 的明细引用定位模板，并核对数据源标记及列数。",
            columns = plan.Columns.Select(c => new { index = c.Index, originalIndex = c.OriginalIndex, field = c.Field, header = c.Header, width = c.Width, hidden = c.Hidden, added = c.OriginalIndex < 0 }).ToArray(),
            message = (plan.Reconciled ? "SQL 已保存、模板待补齐：已按原表头与字段名逐列匹配。请核对原列位置及新增列；不会自动修改 SQL 统计逻辑。\n" : "") +
                "位置按 SQL 返回字段顺序确定；保留原有隐藏状态。这里只是列结构预览，不是 HIS 打印效果。确认后保存 SQL 与模板，内容未变的文件不重写，不生成备份。" };
    }

    private object SaveLayout(Dictionary<string, object> a, CancellationToken token, Action<string> progress)
    {
        var file = EditorFile(a); var report = Report(a); var plan = layoutPlan;
        if (plan == null || layoutToken.Length == 0 || Text(a, "previewToken") != layoutToken ||
            plan.QueryHash != file.Hash || plan.SourceIndex != Number(a, "sourceIndex", -1) || plan.Sql != Text(a, "sql"))
            throw new InvalidOperationException("列预览已失效，请重新同步报表列。");
        if (!a.TryGetValue("columns", out var raw) || raw is string || !(raw is IEnumerable entries)) throw new InvalidOperationException("缺少确认后的列设置。");
        var edits = new List<LayoutColumn>();
        foreach (var entry in entries)
        {
            if (!(entry is Dictionary<string, object> row) || Number(row, "index", -1) != edits.Count) throw new InvalidOperationException("列顺序不一致，请重新预览。");
            edits.Add(new LayoutColumn { Header = Text(row, "header"), Width = Number(row, "width", -1) });
        }
        progress("正在校验并覆盖查询 XML 与报表模板…");
        SqlXmlSaveResult saved;
        try { saved = ReportLayoutEditor.Save(plan, edits, token); }
        catch { reloadRequired.Add(report.Id); Clear(); ClearLayoutPlan(); throw; }
        ClearLayoutPlan(); sqlEditorFile = saved.Snapshot; sqlEditorToken = Guid.NewGuid().ToString("N");
        bool reloaded = false;
        try { report = ReloadSingle(report, CancellationToken.None, saved.Snapshot.Hash); reloaded = true; }
        catch (Exception ex) { ErrorLog.Write("ReloadLayoutSavedReport", ex, includeMessage: false); }
        return new { saved = true, changed = saved.Changed, reloaded, savedPath = saved.Snapshot.Path, layoutPath = plan.Path,
            message = "查询 XML 与报表模板已保存，并已回读核对；未执行数据查询。" +
                (reloaded ? "已重新加载当前报表。请在 HIS 核对数据对应、明细跳转和打印效果。" : "重新加载失败，已禁止使用旧定义，请重新加载当前报表。"),
            editor = EditorData(report, saved.Snapshot) };
    }
}
