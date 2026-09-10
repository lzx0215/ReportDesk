using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ReportDesk.Core;

namespace ReportDesk.App;

internal sealed class ConnectionDialog : Form
{
    private readonly TextBox connectionName = new(), host = new(), service = new(), username = new(), password = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535 };
    private readonly CheckBox remember = new() { Text = "保存密码（此 Windows 用户加密）", AutoSize = true };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox tnsFile = new() { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 680 };
    private readonly ComboBox tnsAlias = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button browse = new() { Text = "浏览…", AutoSize = true }, reload = new() { Text = "刷新服务", AutoSize = true }, discover = new() { Text = "自动查找", AutoSize = true };
    private readonly Button test = new() { Text = "测试连接", Width = 120 }, save = new() { Text = "确定", Width = 88 };
    private readonly Label tnsState = new() { AutoSize = true, ForeColor = Color.DimGray }, testState = new() { Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    private readonly LinkLabel sourceLink = new() { Text = "配置网络服务来源…", AutoSize = true };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage general = new("常规"), advanced = new("高级");
    private readonly TableLayoutPanel directGrid = Grid(), tnsGrid = Grid();
    private readonly Func<string[]> discoverFiles;
    private readonly Func<ConnectionSettings, string, System.Threading.Tasks.Task<string>> testConnection;
    private System.Threading.Tasks.Task discoveryTask = System.Threading.Tasks.Task.CompletedTask;
    private bool testing, discovering;
    public ConnectionSettings Settings { get; private set; }
    public string Password => password.Text;

    public ConnectionDialog(ConnectionSettings settings, string currentPassword,
        Func<string[]>? discoverFiles = null,
        Func<ConnectionSettings, string, System.Threading.Tasks.Task<string>>? testConnection = null)
    {
        this.discoverFiles = discoverFiles ?? TnsDiscovery.Discover;
        this.testConnection = testConnection ?? ((s, p) => System.Threading.Tasks.Task.Run(() => OracleQueryService.TestConnection(s, p)));
        Settings = settings; Text = "连接设置（Oracle）"; ClientSize = new Size(610, 650);
        Font = new Font("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        connectionName.Text = settings.Name ?? ""; host.Text = settings.Host; service.Text = settings.Service;
        username.Text = settings.Username; password.Text = currentPassword;
        port.Value = settings.Port;
        remember.Checked = !string.IsNullOrEmpty(settings.ProtectedPassword);
        mode.Items.AddRange(new object[] { "基本", "TNS" }); mode.SelectedIndex = settings.Mode == ConnectionMode.Tns ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(settings.TnsFile)) { tnsFile.Items.Add(settings.TnsFile); tnsFile.SelectedIndex = 0; }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        tabs.TabPages.Add(general); tabs.TabPages.Add(advanced); layout.Controls.Add(tabs, 0, 0); layout.Controls.Add(testState, 0, 1);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        var cancel = new Button { Text = "取消", Width = 88, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(test); buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;

        var common = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        common.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); common.RowStyles.Add(new RowStyle(SizeType.Absolute, 84)); common.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        common.Controls.Add(new Label { Text = "ReportDesk    ─────────    Oracle", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SteelBlue, Font = new Font(Font.FontFamily, 14) });
        var identity = Grid(); Add(identity, "连接名", connectionName); Add(identity, "连接类型", mode); common.Controls.Add(identity);
        var connectionBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        connectionBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 126)); connectionBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 122)); connectionBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var credentials = Grid();
        Add(credentials, "用户名", username); Add(credentials, "密码", password); Add(credentials, "", remember);
        Add(directGrid, "主机", host); Add(directGrid, "端口", port); Add(directGrid, "服务名", service);
        Add(tnsGrid, "网络服务名", tnsAlias); Add(tnsGrid, "", sourceLink); Add(tnsGrid, "", new Label { Text = "选择已有的 TNS 服务，再填写账号密码。", AutoSize = true, ForeColor = Color.DimGray });
        // Both grids occupy the same connection-type area; credentials keep a stable position.
        var address = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        address.Controls.Add(directGrid); address.Controls.Add(tnsGrid);
        connectionBody.Controls.Add(address, 0, 0); connectionBody.Controls.Add(credentials, 0, 1); common.Controls.Add(connectionBody);
        general.Controls.Add(common);

        var details = Grid(); details.Padding = new Padding(18); details.Dock = DockStyle.Top; details.Height = 398;
        Add(details, "TNS 配置文件", tnsFile);
        var fileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        fileActions.Controls.Add(browse); fileActions.Controls.Add(discover); fileActions.Controls.Add(reload);
        Add(details, "", fileActions); Add(details, "文件状态", tnsState, 70);
        Add(details, "查询时间 / 行数", new Label { Text = "不限制，可手动取消查询", AutoSize = true });
        Add(details, "连接时间", new Label { Text = "程序不设超时；仍受网络和 TNS 配置影响", AutoSize = true });
        var note = new Label { Text = "确定：保存配置，不自动连接。\n测试连接：仅登录数据库后关闭，不查询报表。\n测试成功不代表报表权限或数据口径已核对。\nTNS 握手等待由连接描述控制，关闭窗口不会立即中断握手。\n密码默认不保存；本工具当前保存一组连接设置。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
        var noteRow = details.RowCount++; details.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); details.Controls.Add(note, 0, noteRow); details.SetColumnSpan(note, 2);
        advanced.Controls.Add(details);
        sourceLink.Click += (_, _) => tabs.SelectedTab = advanced;
        mode.SelectedIndexChanged += (_, _) => { UpdateMode(); InvalidateTest(); };
        tnsFile.SelectedIndexChanged += (_, _) => { LoadAliases(""); InvalidateTest(); };
        reload.Click += (_, _) => { LoadAliases(tnsAlias.Text); InvalidateTest(); };
        discover.Click += async (_, _) => await DiscoverAsync();
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "选择 tnsnames.ora", Filter = "Oracle 配置文件 (*.ora)|*.ora|所有文件 (*.*)|*.*", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            SelectFile(picker.FileName);
        };
        foreach (var box in new TextBox[] { connectionName, host, service, username, password }) box.TextChanged += (_, _) => InvalidateTest();
        port.ValueChanged += (_, _) => InvalidateTest(); tnsAlias.SelectedIndexChanged += (_, _) => InvalidateTest();
        test.Click += async (_, _) => await TestAsync();
        save.Click += (_, _) =>
        {
            try { SaveSettings(); DialogResult = DialogResult.OK; }
            catch (Exception ex) { ShowError("SaveConnection", ex); }
        };
        if (tnsFile.Text.Length > 0) LoadAliases(settings.TnsAlias);
        UpdateMode();
        Shown += (_, _) => { if (tnsFile.Text.Length == 0) discoveryTask = DiscoverAsync(); };
    }

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 0 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }
    private static void Add(TableLayoutPanel grid, string label, Control control, int height = 40)
    {
        var row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.Dock = DockStyle.Fill; grid.Controls.Add(control, 1, row);
    }

    private void UpdateMode()
    {
        var tns = mode.SelectedIndex == 1;
        directGrid.Visible = !tns; tnsGrid.Visible = tns;
        host.Enabled = port.Enabled = service.Enabled = !tns;
        tnsAlias.Enabled = tns;
        if (tns) tnsGrid.BringToFront(); else directGrid.BringToFront();
        UpdateSourceLink();
    }
    private void UpdateSourceLink()
    {
        sourceLink.Text = tnsAlias.Items.Count > 0 ? "已加载 " + tnsAlias.Items.Count + " 个网络服务 · 查看来源…"
            : "未加载网络服务 · 前往高级配置…";
    }
    private void SelectFile(string path)
    {
        if (!tnsFile.Items.Contains(path)) tnsFile.Items.Add(path);
        if (tnsFile.Text == path) LoadAliases(tnsAlias.Text); else tnsFile.SelectedItem = path;
        InvalidateTest();
    }
    private async System.Threading.Tasks.Task DiscoverAsync()
    {
        if (discovering || testing) return;
        discovering = true; discover.Enabled = false; test.Enabled = save.Enabled = false;
        tnsState.Text = "正在查找常见 TNS 配置位置…";
        try
        {
            var paths = await System.Threading.Tasks.Task.Run(discoverFiles);
            if (IsDisposed || Disposing) return;
            foreach (var path in paths) if (!tnsFile.Items.Contains(path)) tnsFile.Items.Add(path);
            if (tnsFile.Text.Length == 0 && paths.Length == 1) SelectFile(paths[0]);
            else if (tnsFile.Text.Length == 0) tnsState.Text = paths.Length == 0
                ? "未找到配置。请浏览选择 tnsnames.ora。"
                : "发现多个文件，请从上方选择来源，避免同名服务混用。";
            else LoadAliases(tnsAlias.Text);
            UpdateSourceLink(); InvalidateTest();
        }
        catch (Exception ex) { var notice = ErrorLog.Write("DiscoverTns", ex); if (!IsDisposed && !Disposing) { tnsState.Text = "自动查找失败，请浏览选择配置文件。"; testState.Text = notice; } }
        finally
        {
            discovering = false;
            if (!IsDisposed && !Disposing) { discover.Enabled = true; test.Enabled = save.Enabled = true; }
        }
    }
    private void LoadAliases(string preferred)
    {
        tnsAlias.Items.Clear();
        try
        {
            var aliases = TnsNames.Read(tnsFile.Text).Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            tnsAlias.Items.AddRange(aliases);
            tnsAlias.SelectedIndex = Array.FindIndex(aliases, x => string.Equals(x, preferred, StringComparison.OrdinalIgnoreCase));
            tnsState.Text = "已读取 " + aliases.Length + " 个网络服务" + (tnsAlias.SelectedIndex < 0 ? "，请在常规页选择。" : "。");
        }
        catch (Exception ex) { tnsState.Text = "读取失败：" + ErrorLog.Sanitize(ex.Message); ShowError("ReadTns", ex); }
        UpdateSourceLink();
    }
    private ConnectionSettings ReadSettings()
    {
        return new ConnectionSettings { Name = connectionName.Text.Trim(), Mode = mode.SelectedIndex == 1 ? ConnectionMode.Tns : ConnectionMode.Direct,
            TnsFile = tnsFile.Text, TnsAlias = tnsAlias.Text, Host = host.Text.Trim(), Service = service.Text.Trim(),
            Port = (int)port.Value, Username = username.Text.Trim() };
    }
    private void SaveSettings()
    {
        var next = ReadSettings(); OracleQueryService.ConnectionString(next, password.Text);
        next.ProtectedPassword = remember.Checked ? CatalogStore.Protect(password.Text) : "";
        Settings = next;
    }
    private void InvalidateTest()
    {
        if (testing) return;
        testState.ForeColor = Color.DimGray; testState.Text = "点击“测试连接”验证当前设置；“确定”仅保存。";
    }
    private async System.Threading.Tasks.Task TestAsync()
    {
        if (testing || discovering) return;
        try
        {
            var next = ReadSettings(); var secret = password.Text;
            OracleQueryService.ConnectionString(next, secret);
            if (secret.Length == 0) throw new InvalidOperationException("请输入密码后测试连接。");
            testing = true; tabs.Enabled = test.Enabled = save.Enabled = false;
            testState.ForeColor = Color.DimGray; testState.Text = "正在测试连接，请等待网络握手；完成后自动关闭测试连接…";
            var version = await testConnection(next, secret);
            if (IsDisposed || Disposing) return;
            testState.ForeColor = Color.DarkGreen; testState.Text = "连接成功 · Oracle " + version + "\n仅验证登录连通性；点击“确定”保存设置。";
        }
        catch (Exception ex)
        { if (!IsDisposed && !Disposing) ShowError("TestConnectionDialog", ex); else ErrorLog.Write("TestConnectionClosed", ex, includeMessage: false); }
        finally
        {
            testing = false;
            if (!IsDisposed && !Disposing) tabs.Enabled = test.Enabled = save.Enabled = true;
        }
    }

    private void ShowError(string operation, Exception ex)
    {
        var secrets = ErrorLog.ConnectionValues(ReadSettings(), password.Text).ToArray();
        var notice = ErrorLog.Write(operation, ex, secrets);
        testState.ForeColor = Color.Firebrick; testState.Text = ErrorLog.Sanitize(ex.Message, secrets) + "\n" + notice;
    }

    internal void SmokeTns(string path)
    {
        connectionName.Text = "HIS 报表查询"; host.Text = "example.local"; service.Text = "original"; username.Text = "test_readonly";
        SelectFile(path); mode.SelectedIndex = 1; LoadAliases("DEMO");
        if (host.Enabled || !tnsAlias.Enabled || tnsAlias.Text != "DEMO") throw new Exception("TNS 模式控件失败");
        SaveSettings(); mode.SelectedIndex = 0; SaveSettings();
        if (Settings.Mode != ConnectionMode.Direct || Settings.Service != "original" || Settings.TnsAlias != "DEMO") throw new Exception("切回直接连接失败");
        mode.SelectedIndex = 1; SaveSettings();
    }
    internal void AssertTnsLoaded(string path)
    {
        if (mode.SelectedIndex != 1 || tnsFile.Text != path || tnsAlias.Text != "DEMO" || host.Enabled || service.Text != "original" || connectionName.Text != "HIS 报表查询")
            throw new Exception("重开连接设置未恢复 TNS 配置");
    }
    internal void ShowAdvanced() => tabs.SelectedTab = advanced;

    private async System.Threading.Tasks.Task ShowReadyAsync(Form owner)
    {
        // Shown can be posted after Show returns; wait for actual initialization.
        var shown = new System.Threading.Tasks.TaskCompletionSource<bool>();
        Shown += (_, _) => shown.TrySetResult(true);
        Show(owner); await shown.Task; await discoveryTask;
    }

    internal static async System.Threading.Tasks.Task SmokeConnectionFlowAsync(Form owner, string path)
    {
        var pending = new System.Threading.Tasks.TaskCompletionSource<string>(); var calls = 0;
        using var dialog = new ConnectionDialog(new ConnectionSettings(), "", () => new[] { path }, (s, p) =>
        {
            calls++;
            if (s.TnsAlias != "DEMO" || p != "synthetic-only") throw new Exception("测试连接读取了错误输入");
            return pending.Task;
        });
        await dialog.ShowReadyAsync(owner);
        if (dialog.tnsFile.Text != path || dialog.tnsAlias.Items.Count != 1 || dialog.tnsAlias.SelectedIndex != -1) throw new Exception("自动发现或未选别名处理失败: fileMatch=" + (dialog.tnsFile.Text == path) + "; aliases=" + dialog.tnsAlias.Items.Count + "; selected=" + dialog.tnsAlias.SelectedIndex + "; state=" + dialog.tnsState.Text);
        dialog.mode.SelectedIndex = 1; dialog.username.Text = "test_readonly"; dialog.password.Text = "synthetic-only";
        await dialog.TestAsync(); if (calls != 0) throw new Exception("无别名时不应尝试连接");
        dialog.LoadAliases("DEMO");
        var operation = dialog.TestAsync();
        if (dialog.tabs.Enabled || dialog.save.Enabled || dialog.test.Enabled) throw new Exception("测试期间未阻止重复操作");
        await dialog.TestAsync(); if (calls != 1) throw new Exception("重复测试未被阻止");
        pending.SetResult("19c (offline fake)"); await operation;
        if (!dialog.testState.Text.StartsWith("连接成功") || !dialog.tabs.Enabled || dialog.Settings.TnsFile.Length != 0) throw new Exception("测试成功或未保存边界失败");
        dialog.password.Text = "edited"; if (dialog.testState.Text.StartsWith("连接成功")) throw new Exception("修改输入后仍显示旧成功");
        dialog.password.Text = "synthetic-only"; pending = new System.Threading.Tasks.TaskCompletionSource<string>();
        operation = dialog.TestAsync(); pending.SetException(new InvalidOperationException("模拟连接失败")); await operation;
        if (!dialog.testState.Text.StartsWith("模拟连接失败") || !dialog.testState.Text.Contains("错误日志") || !dialog.test.Enabled) throw new Exception("失败后界面未恢复或没有日志提示");
        dialog.Close();

        using var multiple = new ConnectionDialog(new ConnectionSettings(), "", () => new[] { path, path + ".other" }, (_, _) => throw new Exception("不应联网"));
        await multiple.ShowReadyAsync(owner);
        if (multiple.tnsFile.SelectedIndex != -1 || multiple.tnsAlias.Items.Count != 0) throw new Exception("多来源时自动选错配置");
        multiple.Close();
        using var missing = new ConnectionDialog(new ConnectionSettings { Mode = ConnectionMode.Tns, TnsFile = path + ".missing", TnsAlias = "DEMO" }, "", () => new[] { path }, (_, _) => throw new Exception("不应联网"));
        await missing.ShowReadyAsync(owner);
        if (missing.tnsFile.Text != path + ".missing" || missing.tnsAlias.Items.Count != 0) throw new Exception("失效保存路径不应自动回退");
        missing.Close();

        pending = new System.Threading.Tasks.TaskCompletionSource<string>();
        using var closed = new ConnectionDialog(new ConnectionSettings { Mode = ConnectionMode.Tns, TnsFile = path, TnsAlias = "DEMO", Username = "test_readonly" }, "synthetic-only", () => Array.Empty<string>(), (_, _) => pending.Task);
        await closed.ShowReadyAsync(owner); var closingOperation = closed.TestAsync(); closed.Close();
        pending.SetResult("19c (offline fake)"); await closingOperation;
    }
}

internal sealed class MetadataDialog : Form
{
    public string Category => category.Text.Trim().Length == 0 ? "未分类" : category.Text.Trim();
    public string Aliases => aliases.Text.Trim();
    public string Notes => notes.Text;
    public bool Verified => verified.Checked;
    private readonly TextBox category = new(), aliases = new(), notes = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox verified = new() { Text = "我已在内网与 HIS 核对该报表的数据与口径", AutoSize = true };
    public MetadataDialog(ReportDefinition report)
    {
        Text = "报表说明 · " + report.Name; ClientSize = new Size(620, 430); Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
        category.Text = report.Category; aliases.Text = report.Aliases; notes.Text = report.Notes;
        verified.Checked = report.Verified; verified.Enabled = report.Issues.Count == 0 && !report.IsDemo;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 24, 36, 24, 36, 24, 170, 32, 38 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "分类", AutoSize = true }); layout.Controls.Add(category);
        layout.Controls.Add(new Label { Text = "别名 / 搜索关键词", AutoSize = true }); layout.Controls.Add(aliases);
        layout.Controls.Add(new Label { Text = "用途、时间口径和核对说明（不要填写患者信息）", AutoSize = true }); layout.Controls.Add(notes);
        layout.Controls.Add(verified); layout.Controls.Add(new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true });
        category.Dock = aliases.Dock = notes.Dock = DockStyle.Fill; Controls.Add(layout);
    }
}

internal static class TextDialog
{
    public static void Show(IWin32Window owner, string title, string content)
    {
        using var form = new Form { Text = title, Width = 880, Height = 630, StartPosition = FormStartPosition.CenterParent };
        form.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = content, Font = new Font("Consolas", 10) });
        form.ShowDialog(owner);
    }
}
