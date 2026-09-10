using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Oracle.ManagedDataAccess.Client;
using ReportDesk.Core;

namespace ReportDesk.App;

internal sealed class MainForm : Form
{
    private readonly CatalogStore store;
    private readonly Catalog catalog;
    private readonly ReportVisibility visibility;
    private string password = "";
    private readonly TextBox search = new(), filter = new();
    private readonly ListBox navigation = new() { BorderStyle = BorderStyle.None }, reports = new() { BorderStyle = BorderStyle.None };
    private readonly Label title = new(), description = new(), status = new(), count = new();
    private readonly ComboBox sources = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly FlowLayoutPanel parameters = new() { AutoScroll = true, WrapContents = true };
    private readonly DataGridView results = new();
    private readonly Dictionary<string, Func<string>> inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> inputControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Control> busyDisabled = new();
    private readonly Button run, cancel, export, star, metadata, sqlView;
    private CancellationTokenSource? active;
    private ReportDefinition? selected;
    private QueryResult? currentResult;
    private string resultContext = "";
    private bool refreshing;

    public MainForm(string? dataDirectory)
    {
        visibility = ReportVisibility.Load(Path.Combine(dataDirectory ?? AppDomain.CurrentDomain.BaseDirectory, ReportVisibility.FileName));
        store = new CatalogStore(dataDirectory); catalog = store.Load();
        try { password = CatalogStore.Unprotect(catalog.Connection.ProtectedPassword); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException || ex is FormatException)
        { ErrorLog.Write("DecryptPassword", ex, ErrorLog.ConnectionValues(catalog.Connection, password)); password = ""; }
        Text = "ReportDesk · 报表管理"; ClientSize = new Size(1280, 820); MinimumSize = new Size(1040, 720);
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.FromArgb(245, 247, 250); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(19, 38, 57), ColumnCount = 2, Padding = new Padding(16, 9, 16, 9) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "REPORTDESK   /   报表管理", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var importFile = Button("导入 XML", async () => await ImportAsync(false));
        var importFolder = Button("导入文件夹", async () => await ImportAsync(true));
        var connection = Button("连接设置", Connection);
        var demo = Button("体验演示", LoadDemo);
        demo.Visible = visibility.Includes(DemoData.Report());
        var logs = Button("打开日志", () =>
        {
            try { Directory.CreateDirectory(ErrorLog.DirectoryPath); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ErrorLog.DirectoryPath) { UseShellExecute = true }); }
            catch (Exception ex) { Error(ex); }
        });
        toolbar.Controls.AddRange(new Control[] { importFile, importFolder, connection, demo, logs }); header.Controls.Add(toolbar, 1, 0); root.Controls.Add(header, 0, 0);
        busyDisabled.AddRange(new Control[] { importFile, importFolder, connection, demo });

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(0, 8, 8, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 1);
        navigation.Dock = DockStyle.Fill; navigation.BackColor = BackColor; navigation.Font = new Font(Font.FontFamily, 11); navigation.ItemHeight = 40;
        navigation.DrawMode = DrawMode.OwnerDrawFixed;
        navigation.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var chosen = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(chosen ? Color.FromArgb(221, 241, 239) : BackColor);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, navigation.Items[e.Index].ToString(), navigation.Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 15, e.Bounds.Height), Color.FromArgb(24, 65, 71), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        navigation.SelectedIndexChanged += (_, _) => RefreshReports(); body.Controls.Add(navigation, 0, 0);
        var library = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        library.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); library.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); library.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        search.Dock = DockStyle.Fill; search.TextChanged += (_, _) => RefreshReports(); new ToolTip().SetToolTip(search, "搜索报表名称、别名、用途和来源");
        count.Dock = DockStyle.Fill; count.ForeColor = Color.DimGray;
        reports.Dock = DockStyle.Fill; reports.DrawMode = DrawMode.OwnerDrawFixed; reports.ItemHeight = 62;
        reports.DrawItem += DrawReport; reports.SelectedIndexChanged += (_, _) => SelectReport();
        library.Controls.Add(search, 0, 0); library.Controls.Add(count, 0, 1); library.Controls.Add(reports, 0, 2); body.Controls.Add(library, 1, 0);

        var work = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14, 0, 0, 0) };
        foreach (var size in new[] { 112, 40, 192, 45, 35 }) work.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        work.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.Controls.Add(work, 2, 0);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title.Font = new Font(Font.FontFamily, 15, FontStyle.Bold); title.Dock = DockStyle.Fill; title.AutoEllipsis = true;
        description.Dock = DockStyle.Fill; description.ForeColor = Color.DimGray; description.AutoEllipsis = true;
        heading.Controls.Add(title); heading.Controls.Add(description); work.Controls.Add(heading, 0, 0);
        sources.Dock = DockStyle.Fill; sources.DrawMode = DrawMode.OwnerDrawFixed; sources.ItemHeight = 23;
        sources.DrawItem += (_, e) =>
        {
            e.DrawBackground(); var item = e.Index >= 0 ? sources.Items[e.Index] : sources.SelectedItem;
            TextRenderer.DrawText(e.Graphics, item?.ToString() ?? "请选择数据源", Font, e.Bounds, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        sources.SelectedIndexChanged += (_, _) => { ClearResults(); BuildParameters(); };
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceRow.Controls.Add(new Label { Text = "数据源", AutoSize = true, Anchor = AnchorStyles.Left }); sourceRow.Controls.Add(sources); work.Controls.Add(sourceRow, 0, 1);
        parameters.Dock = DockStyle.Fill; parameters.BackColor = Color.White; parameters.Padding = new Padding(8); work.Controls.Add(parameters, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        run = Button("查询", async () => await RunAsync()); run.BackColor = Color.FromArgb(0, 119, 112); run.ForeColor = Color.White;
        cancel = Button("取消", () => active?.Cancel()); cancel.Enabled = false;
        export = Button("导出 Excel", async () => await ExportAsync());
        star = Button("收藏", ToggleFavorite); metadata = Button("说明", EditMetadata); sqlView = Button("查看 SQL", ShowSql);
        actions.Controls.AddRange(new Control[] { run, cancel, export, star, metadata, sqlView }); work.Controls.Add(actions, 0, 3);
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.Controls.Add(new Label { Text = "结果内查找", AutoSize = true }); filter.Dock = DockStyle.Fill;
        filter.TextChanged += (_, _) => FilterResults(); filterRow.Controls.Add(filter); work.Controls.Add(filterRow, 0, 4);
        results.Dock = DockStyle.Fill; results.ReadOnly = true; results.AllowUserToAddRows = false; results.AllowUserToDeleteRows = false;
        results.BackgroundColor = Color.White; results.BorderStyle = BorderStyle.None; results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        results.RowHeadersVisible = false; results.SelectionMode = DataGridViewSelectionMode.FullRowSelect; results.MultiSelect = true;
        results.AllowUserToOrderColumns = true; results.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
        results.EnableHeadersVisualStyles = false; results.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(230, 239, 241);
        results.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold); results.ColumnHeadersHeight = 38;
        results.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 251); results.RowTemplate.Height = 29;
        results.DataError += (_, e) => { e.ThrowException = false; status.Text = "结果表格显示失败。" + ErrorLog.Write("DisplayResult", e.Exception ?? new InvalidOperationException("表格显示失败"), includeMessage: false); };
        work.Controls.Add(results, 0, 5);
        status.Dock = DockStyle.Fill; status.Padding = new Padding(14, 6, 0, 0); status.ForeColor = Color.FromArgb(50, 75, 90); root.Controls.Add(status, 0, 2);
        busyDisabled.AddRange(new Control[] { search, navigation, reports, sources, parameters, run, export, star, metadata, sqlView, filter });
        RefreshNavigation(); RefreshReports();
        status.Text = "本地报表库 · 尚未连接数据库。";
        FormClosing += (_, e) => { if (active != null) { e.Cancel = true; active.Cancel(); status.Text = "正在取消当前操作，请结束后关闭窗口。"; } };
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Padding = new Padding(5, 0, 5, 0), Margin = new Padding(0, 4, 6, 3) };
        button.FlatAppearance.BorderColor = Color.FromArgb(209, 220, 228); button.Click += (_, _) => action(); return button;
    }
    private void Save() => store.Save(catalog);
    private IEnumerable<ReportDefinition> VisibleReports() => catalog.Reports.Where(visibility.Includes);
    private void RefreshNavigation()
    {
        var previous = navigation.SelectedItem as string; refreshing = true;
        navigation.Items.Clear(); navigation.Items.AddRange(new object[] { "全部报表", "我的收藏", "最近使用", "待适配" });
        foreach (var category in VisibleReports().Select(r => r.Category).Distinct().OrderBy(x => x)) navigation.Items.Add("分类 · " + category);
        navigation.SelectedItem = previous; if (navigation.SelectedIndex < 0) navigation.SelectedIndex = 0; refreshing = false;
    }
    private void RefreshReports()
    {
        if (refreshing) return;
        var id = selected?.Id; var mode = navigation.SelectedItem as string ?? "全部报表";
        IEnumerable<ReportDefinition> list = VisibleReports();
        if (mode == "我的收藏") list = list.Where(r => r.Favorite);
        else if (mode == "最近使用") list = list.Where(r => r.LastUsed.HasValue).OrderByDescending(r => r.LastUsed);
        else if (mode == "待适配") list = list.Where(r => r.Issues.Count > 0);
        else if (mode.StartsWith("分类 · ", StringComparison.Ordinal)) list = list.Where(r => r.Category == mode.Substring(5));
        var term = search.Text.Trim();
        if (term.Length > 0) list = list.Where(r => (r.Name + " " + r.Aliases + " " + r.Notes + " " + r.SourcePath).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        if (mode != "最近使用") list = list.OrderByDescending(r => r.Favorite).ThenBy(r => r.Name);
        refreshing = true; reports.Items.Clear(); foreach (var r in list) reports.Items.Add(r);
        count.Text = reports.Items.Count + " 张报表  /  搜索名称、别名、用途";
        for (var i = 0; i < reports.Items.Count; i++) if (((ReportDefinition)reports.Items[i]).Id == id) reports.SelectedIndex = i;
        if (reports.SelectedIndex < 0 && reports.Items.Count > 0) reports.SelectedIndex = 0;
        refreshing = false; SelectReport();
    }
    private void DrawReport(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return; var r = (ReportDefinition)reports.Items[e.Index];
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(isSelected ? Color.FromArgb(221, 241, 239) : Color.White); e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, (r.Favorite ? "★ " : "") + r.Name, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 7, e.Bounds.Width - 12, 25), Color.FromArgb(24, 43, 61), TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, r.Category + "  ·  " + r.Status, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 34, e.Bounds.Width - 12, 22), Color.DimGray, TextFormatFlags.EndEllipsis);
    }
    private void SelectReport()
    {
        if (refreshing) return; selected = reports.SelectedItem as ReportDefinition;
        if (selected != null && !visibility.Includes(selected)) selected = null;
        sources.Items.Clear(); ClearResults();
        title.Text = selected?.Name ?? "从一张报表开始";
        description.Text = selected == null ? "当前没有可显示的报表，请检查搜索条件或联系配置人员。" : selected.Status + "  |  " + selected.Notes + "\n" + (selected.IsDemo ? "模拟演示 · 不连接数据库" : selected.SourcePath);
        if (selected != null)
        {
            foreach (var q in selected.Queries.OrderBy(q => q.Kind == "DetailReportUsing" ? 1 : 0)) sources.Items.Add(q);
            if (sources.Items.Count > 0) sources.SelectedIndex = 0;
            if (selected.Issues.Count > 0) description.Text = "待适配：" + string.Join("；", selected.Issues) + "\n" + selected.SourcePath;
        }
        BuildParameters(); SetReady();
    }
    private void SetReady()
    {
        run.Enabled = active == null && selected != null && selected.Issues.Count == 0 && sources.SelectedItem != null;
        export.Enabled = active == null && currentResult != null;
        star.Enabled = metadata.Enabled = sqlView.Enabled = active == null && selected != null;
        star.Text = selected?.Favorite == true ? "已收藏" : "收藏";
    }
    private void BuildParameters()
    {
        foreach (Control child in parameters.Controls.Cast<Control>().ToArray()) child.Dispose();
        parameters.Controls.Clear(); inputs.Clear(); inputControls.Clear();
        if (selected == null || !(sources.SelectedItem is QueryDefinition query)) return;
        List<string> needed;
        try { needed = SqlTemplate.Compile(query.Sql).RequiredNames; }
        catch (InvalidOperationException) { return; }
        // Lookup SQL may need additional context beyond the selected result SQL.
        foreach (var p in selected.Parameters.Where(p => needed.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
            if (p.LookupSql.Length > 0)
                try { foreach (var n in SqlTemplate.Compile(p.LookupSql).RequiredNames) if (!needed.Contains(n, StringComparer.OrdinalIgnoreCase)) needed.Add(n); }
                catch (InvalidOperationException) { }
        foreach (var p in selected.Parameters.Where(p => needed.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var panel = new Panel { Width = 272, Height = p.Kind == "ComboBoxType" ? 89 : 68, Margin = new Padding(5, 2, 8, 3) };
            panel.Controls.Add(new Label { Text = p.Label.Length > 0 ? p.Label : p.Name, AutoEllipsis = true, Width = 270, Height = 23, ForeColor = p.Implicit ? Color.FromArgb(160, 85, 0) : ForeColor });
            Control control;
            if (p.Kind == "DateTimeType")
            {
                var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = string.IsNullOrWhiteSpace(p.Format) ? "yyyy-MM-dd HH:mm:ss" : p.Format };
                try { picker.Value = DateTime.Now.AddMonths(p.AddMonths).AddDays(p.AddDays); } catch (ArgumentOutOfRangeException) { }
                control = picker; inputs[p.Name] = () => picker.Value.ToString(picker.CustomFormat, CultureInfo.InvariantCulture);
            }
            else if (p.Kind == "ComboBoxType")
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Text" };
                if (p.HasAll) combo.Items.Add(new Choice(p.AllValue, "全部"));
                // No automatic selection: ALL can materially broaden the query.
                control = combo; inputs[p.Name] = () => (combo.SelectedItem as Choice)?.Value ?? throw new InvalidOperationException("请选择：" + p.Label);
                var lookup = Button("加载选项", async () => await LookupAsync(p, combo)); lookup.Location = new Point(0, 54); lookup.Height = 27; lookup.Enabled = p.LookupSql.Length > 0 && selected.Issues.Count == 0;
                panel.Controls.Add(lookup);
            }
            else
            {
                var text = new TextBox(); control = text; inputs[p.Name] = () => text.Text;
            }
            inputControls[p.Name] = control; control.Location = new Point(0, 24); control.Width = 264; panel.Controls.Add(control); parameters.Controls.Add(panel);
        }
    }
    private sealed class Choice
    {
        public string Value { get; } public string Text { get; }
        public Choice(string value, string text) { Value = value; Text = text; }
        public override string ToString() => Text;
    }
    private Dictionary<string, string> ReadValues(string sql)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SqlTemplate.Compile(sql).RequiredNames)
        {
            if (!inputs.TryGetValue(name, out var read)) throw new InvalidOperationException("缺少输入控件：" + name);
            var p = selected!.Parameters.First(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var value = read();
            if (p.Implicit && string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("请明确填写上下文/明细参数：" + name);
            data[name] = SqlTemplate.Transform(p, value);
        }
        return data;
    }
    private bool EnsureConnection()
    {
        bool Configured() => catalog.Connection.Mode == ConnectionMode.Tns
            ? !string.IsNullOrWhiteSpace(catalog.Connection.TnsFile) && !string.IsNullOrWhiteSpace(catalog.Connection.TnsAlias)
            : !string.IsNullOrWhiteSpace(catalog.Connection.Host);
        if (!Configured() || password.Length == 0) Connection();
        return Configured() && password.Length > 0;
    }
    private void Connection()
    {
        using var dialog = new ConnectionDialog(catalog.Connection, password);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        catalog.Connection = dialog.Settings; password = dialog.Password;
        try { Save(); status.Text = "连接设置已保存；报表查询将使用当前配置。"; }
        catch (Exception ex) { Error(ex); }
        ClearResults();
    }
    private async Task Busy(Func<CancellationToken, Task> operation)
    {
        if (active != null) return;
        active = new CancellationTokenSource(); foreach (var control in busyDisabled) control.Enabled = false; cancel.Enabled = true;
        try { await operation(active.Token); }
        catch (OperationCanceledException) { status.Text = "操作已取消，没有新结果。"; }
        catch (Exception ex) { Error(ex); }
        finally { active.Dispose(); active = null; foreach (var control in busyDisabled) control.Enabled = true; cancel.Enabled = false; SetReady(); }
    }
    private void Error(Exception ex)
    {
        var secrets = ErrorLog.ConnectionValues(catalog.Connection, password).ToList();
        foreach (var input in inputs.Values) { try { secrets.Add(input()); } catch { } }
        var notice = ErrorLog.Write("MainOperation", ex, secrets);
        var message = ErrorLog.Sanitize(ex.Message, secrets);
        status.Text = "操作未完成。"; MessageBox.Show(this, message + "\n\n" + notice, "ReportDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private async Task ImportAsync(bool folder)
    {
        string path;
        if (folder) { using var dialog = new FolderBrowserDialog { Description = "选择包含 HIS 查询设置 XML 的文件夹（含子目录）" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.SelectedPath; }
        else { using var dialog = new OpenFileDialog { Filter = "查询设置 XML|*.xml" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.FileName; }
        await Busy(async token =>
        {
            status.Text = "正在解析 XML；不执行其中的 SQL…";
            var imported = await Task.Run(() =>
            {
                if (folder) return ReportImporter.ImportFolder(path, token);
                var summary = new ImportSummary(); var item = ReportImporter.ImportFile(path);
                if (item == null) summary.Skipped++; else summary.Reports.Add(item); return summary;
            }, token);
            token.ThrowIfCancellationRequested(); ReportImporter.Merge(catalog, imported); Save(); RefreshNavigation(); RefreshReports();
            status.Text = $"导入 {imported.Reports.Count} 张；待适配 {imported.Reports.Count(r => r.Issues.Count > 0)} 张；跳过 {imported.Skipped} 个其他配置；失败 {imported.Errors.Count} 个。";
            if (imported.Errors.Count > 0) TextDialog.Show(this, "导入失败记录", string.Join(Environment.NewLine, imported.Errors));
        });
    }
    private async Task LookupAsync(ParameterDefinition p, ComboBox combo)
    {
        if (selected == null || !visibility.Includes(selected) || !selected.Parameters.Contains(p)) return;
        Dictionary<string, string> values;
        try { values = ReadValues(p.LookupSql); if (!EnsureConnection()) return; } catch (Exception ex) { Error(ex); return; }
        await Busy(async token =>
        {
            status.Text = "正在加载 " + p.Label + " 选项…";
            var result = await Task.Run(() => OracleQueryService.Execute(catalog.Connection, password, p.LookupSql, values, token), token);
            if (result.Table.Columns.Count < 2) throw new InvalidOperationException("选项 SQL 需要至少两列：编码、名称。");
            combo.Items.Clear(); if (p.HasAll) combo.Items.Add(new Choice(p.AllValue, "全部"));
            foreach (DataRow row in result.Table.Rows) combo.Items.Add(new Choice(Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? "", Convert.ToString(row[1], CultureInfo.InvariantCulture) + "  [" + row[0] + "]"));
            status.Text = "已加载 " + result.Table.Rows.Count + " 个选项，请选择后查询。";
        });
    }
    private async Task RunAsync()
    {
        if (selected == null || !visibility.Includes(selected) || selected.Issues.Count > 0 || !(sources.SelectedItem is QueryDefinition query)) return;
        var report = selected; Dictionary<string, string> values;
        try { values = ReadValues(query.Sql); if (!report.IsDemo && !EnsureConnection()) return; } catch (Exception ex) { Error(ex); return; }
        ClearResults();
        await Busy(async token =>
        {
            status.Text = report.IsDemo ? "正在生成模拟数据…" : "正在查询，连接等待取决于网络及连接配置；执行中可取消…";
            var result = await Task.Run(() => report.IsDemo ? DemoData.Execute(values, token) : OracleQueryService.Execute(catalog.Connection, password, query.Sql, values, token), token);
            token.ThrowIfCancellationRequested(); currentResult = result; results.DataSource = result.Table.DefaultView;
            resultContext = report.Name + " / " + query.Name + (result.Demo ? " / 模拟数据" : " / " + report.Status) + " / 已加载结果";
            report.LastUsed = DateTime.Now; Save();
            status.Text = $"{(result.Demo ? "模拟数据 · " : "")}{result.Table.Rows.Count} 行 · {result.Milliseconds} ms · 可点击列标题排序，或导出当前结果。数据源独立执行，不自动补原模板合计。";
        });
    }
    private void ClearResults()
    { results.DataSource = null; currentResult?.Table.Dispose(); currentResult = null; resultContext = ""; filter.Text = ""; if (export != null) export.Enabled = false; }
    private void FilterResults()
    {
        if (currentResult == null) return;
        var text = string.Concat(filter.Text.Select(c => c == '\'' ? "''" : c == '[' ? "[[]" : c == ']' ? "[]]" : c == '%' ? "[%]" : c == '*' ? "[*]" : c.ToString()));
        currentResult.Table.DefaultView.RowFilter = text.Length == 0 ? "" : string.Join(" OR ", currentResult.Table.Columns.Cast<DataColumn>().Select(c => "Convert([" + c.ColumnName.Replace("\\", "\\\\").Replace("]", "\\]") + "], 'System.String') LIKE '%" + text + "%'") );
    }
    private async Task ExportAsync()
    {
        if (currentResult == null) return;
        using var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", FileName = "报表结果_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var context = resultContext;
        using var snapshot = currentResult.Table.DefaultView.ToTable();
        await Busy(async token => { status.Text = "正在导出当前筛选结果…"; await Task.Run(() => XlsxExporter.Export(snapshot, dialog.FileName, context, token), token); status.Text = "已导出 " + snapshot.Rows.Count + " 行到 " + dialog.FileName; });
    }
    private void ToggleFavorite()
    { if (selected == null) return; selected.Favorite = !selected.Favorite; try { Save(); RefreshReports(); } catch (Exception ex) { Error(ex); } }
    private void EditMetadata()
    {
        if (selected == null) return; using var dialog = new MetadataDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        selected.Category = dialog.Category; selected.Aliases = dialog.Aliases; selected.Notes = dialog.Notes; selected.Verified = dialog.Verified;
        try { Save(); RefreshNavigation(); RefreshReports(); } catch (Exception ex) { Error(ex); }
    }
    private void ShowSql()
    {
        if (selected == null) return;
        TextDialog.Show(this, "报表定义（只读）", "来源：" + selected.SourcePath + "\r\n状态：" + selected.Status + "\r\n" + string.Join("\r\n", selected.Issues) + "\r\n\r\n" + string.Join("\r\n\r\n", selected.Queries.Select(q => "-- " + q.Name + " / " + q.Kind + "\r\n" + q.Sql)));
    }
    private void LoadDemo()
    {
        if (!visibility.Includes(DemoData.Report())) return;
        var demo = catalog.Reports.FirstOrDefault(r => r.IsDemo);
        if (demo == null) { demo = DemoData.Report(); catalog.Reports.Add(demo); }
        selected = demo; search.Clear(); navigation.SelectedIndex = 0; RefreshNavigation(); RefreshReports();
    }

    internal async Task SmokeAsync(string directory)
    {
        LoadDemo(); if (selected?.IsDemo != true || sources.SelectedItem == null || sources.Text.Length == 0) throw new Exception("演示/数据源选择失败");
        await RunAsync(); if (currentResult == null || currentResult.Table.Rows.Count != 14) throw new Exception("模拟查询失败");
        filter.Text = "科室 A"; if (currentResult.Table.DefaultView.Count != 7) throw new Exception("结果筛选失败");
        filter.Text = "["; if (currentResult.Table.DefaultView.Count != 0) throw new Exception("筛选特殊字符失败");
        filter.Text = "]"; if (currentResult.Table.DefaultView.Count != 0) throw new Exception("筛选右括号失败");
        filter.Clear(); currentResult.Table.DefaultView.Sort = "金额 DESC";
        using (var snapshot = currentResult.Table.DefaultView.ToTable()) XlsxExporter.Export(snapshot, Path.Combine(directory, "demo.xlsx"), resultContext);
        ToggleFavorite(); search.Text = "不存在的报表"; if (reports.Items.Count != 0) throw new Exception("目录搜索失败");
        search.Clear(); LoadDemo(); await RunAsync();
        if (currentResult == null) throw new Exception("重复查询失败");
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(Path.Combine(directory, "preview.png"));
        using var connection = new ConnectionDialog(catalog.Connection, "", () => Array.Empty<string>(), (_, _) => throw new Exception("离线检查禁止连接数据库")); connection.Show(this); Application.DoEvents();
        using var connectionBitmap = new Bitmap(connection.Width, connection.Height); connection.DrawToBitmap(connectionBitmap, new Rectangle(Point.Empty, connection.Size)); connectionBitmap.Save(Path.Combine(directory, "connection.png")); connection.Close();
        var tnsPath = Path.Combine(directory, "tnsnames.ora");
        File.WriteAllText(tnsPath, "DEMO=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=example.invalid)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=demo)))");
        using var tns = new ConnectionDialog(catalog.Connection, "", () => Array.Empty<string>(), (_, _) => throw new Exception("离线检查禁止连接数据库")); tns.Show(this); tns.SmokeTns(tnsPath); Application.DoEvents();
        using var tnsBitmap = new Bitmap(tns.Width, tns.Height); tns.DrawToBitmap(tnsBitmap, new Rectangle(Point.Empty, tns.Size)); tnsBitmap.Save(Path.Combine(directory, "connection-tns.png"));
        var tnsStore = new CatalogStore(Path.Combine(directory, "tns-store")); tnsStore.Save(new Catalog { Connection = tns.Settings });
        using var reopened = new ConnectionDialog(tnsStore.Load().Connection, "", () => Array.Empty<string>(), (_, _) => throw new Exception("离线检查禁止连接数据库")); reopened.AssertTnsLoaded(tnsPath);
        tns.ShowAdvanced(); Application.DoEvents();
        using var advancedBitmap = new Bitmap(tns.Width, tns.Height); tns.DrawToBitmap(advancedBitmap, new Rectangle(Point.Empty, tns.Size)); advancedBitmap.Save(Path.Combine(directory, "connection-advanced.png")); tns.Close();
        await ConnectionDialog.SmokeConnectionFlowAsync(this, tnsPath);
        await SmokeVisibilityAsync(directory);
    }

    private async Task SmokeVisibilityAsync(string directory)
    {
        var scoped = Path.Combine(directory, "visibility-" + Guid.NewGuid().ToString("N"));
        var scopedStore = new CatalogStore(scoped);
        var allowed = DemoData.Report(); allowed.Id = "doctor"; allowed.Name = "同名工作量"; allowed.Category = "医生分类";
        allowed.Favorite = true; allowed.LastUsed = DateTime.Now;
        var hidden = DemoData.Report(); hidden.Id = "nurse"; hidden.Name = allowed.Name; hidden.Category = "护士分类";
        hidden.Favorite = true; hidden.LastUsed = DateTime.Now; hidden.Issues.Add("待适配测试");
        scopedStore.Save(new Catalog { Reports = new List<ReportDefinition> { allowed, hidden } });
        var path = Path.Combine(scoped, ReportVisibility.FileName);
        File.WriteAllText(path, "<ReportVisibility mode=\"selected\"><Report id=\"doctor\" /></ReportVisibility>");
        using var limited = new MainForm(scoped); limited.Show(this); Application.DoEvents();
        void AssertVisible(int expected)
        {
            if (limited.reports.Items.Count != expected || limited.reports.Items.Cast<ReportDefinition>().Any(r => r.Id != "doctor"))
                throw new Exception("岗位配置报表过滤失败");
        }
        AssertVisible(1);
        if (limited.navigation.Items.Contains("分类 · 护士分类")) throw new Exception("隐藏报表分类仍然显示");
        foreach (var mode in new[] { "我的收藏", "最近使用", "分类 · 医生分类", "全部报表" })
        { limited.navigation.SelectedItem = mode; AssertVisible(1); }
        limited.navigation.SelectedItem = "待适配"; AssertVisible(0);
        limited.navigation.SelectedItem = "全部报表"; limited.search.Text = "护士"; AssertVisible(0);
        limited.search.Text = "同名工作量"; AssertVisible(1); limited.search.Clear();
        var beforeDemo = limited.catalog.Reports.Count; limited.LoadDemo(); AssertVisible(1);
        if (limited.catalog.Reports.Count != beforeDemo) throw new Exception("演示入口绕过显示配置");
        var fresh = DemoData.Report(); fresh.Id = "new-import"; fresh.Category = "新导入分类";
        var incoming = new ImportSummary(); incoming.Reports.Add(fresh);
        ReportImporter.Merge(limited.catalog, incoming); limited.RefreshNavigation(); limited.RefreshReports(); AssertVisible(1);
        if (limited.navigation.Items.Contains("分类 · 新导入分类")) throw new Exception("新导入报表自动显示");
        await limited.RunAsync();
        if (limited.currentResult?.Table.Rows.Count != 14) throw new Exception("配置内模拟查询失败");
        limited.filter.Text = "科室 A";
        using (var snapshot = limited.currentResult.Table.DefaultView.ToTable())
        {
            if (snapshot.Rows.Count != 7) throw new Exception("配置内查询结果筛选失败");
            XlsxExporter.Export(snapshot, Path.Combine(scoped, "visible.xlsx"), limited.resultContext);
        }
        limited.search.Text = "不存在";
        if (limited.currentResult != null || limited.export.Enabled) throw new Exception("空列表未清除查询结果");
        limited.search.Clear();
        // The file is a startup snapshot; editing it takes effect only after reopening.
        File.WriteAllText(path, "<ReportVisibility mode=\"selected\" />");
        limited.RefreshReports(); AssertVisible(1);
        using var empty = new MainForm(scoped); empty.Show(this); Application.DoEvents();
        if (empty.reports.Items.Count != 0 || empty.selected != null || empty.run.Enabled || empty.export.Enabled || empty.sqlView.Enabled)
            throw new Exception("空清单仍提供报表操作");
        empty.LoadDemo(); if (empty.reports.Items.Count != 0) throw new Exception("空清单演示绕过");
        using var screenshot = new Bitmap(limited.Width, limited.Height);
        limited.DrawToBitmap(screenshot, new Rectangle(Point.Empty, limited.Size)); screenshot.Save(Path.Combine(directory, "visibility.png"));
        empty.Close(); limited.ClearResults(); limited.Close();
        File.WriteAllText(path, "<ReportVisibility mode=\"invalid\" />");
        try { using var invalid = new MainForm(scoped); throw new Exception("错误配置仍然启动"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(ReportVisibility.FileName)) { }
        File.WriteAllText(Path.Combine(directory, "visibility.txt"), "PASS: allow-list, same-name IDs, categories, search, favorites, recent, unsupported, import merge, demo, simulated query/export, empty list, restart, invalid config; no database connection attempted");
    }
}
