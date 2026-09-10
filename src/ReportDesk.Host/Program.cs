using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ReportDesk.Core;

namespace ReportDesk.Host;

internal sealed class Request
{
    public int id { get; set; }
    public string method { get; set; } = "";
    public Dictionary<string, object> args { get; set; } = new();
}

internal static class Program
{
    private static readonly object OutputGate = new(), WorkGate = new();
    private static CancellationTokenSource? active;
    private static int activeId;
    private static Task? work;
    private static Service service = null!;
    internal static JavaScriptSerializer Json() => new() { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
    internal static void Send(object value) { lock (OutputGate) { Console.WriteLine(Json().Serialize(value)); Console.Out.Flush(); } }
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            string? data = args.Length > 0 ? args[0] : null;
            string config = args.Length > 1 ? args[1] : AppDomain.CurrentDomain.BaseDirectory;
            bool offline = args.Length > 2 && args[2] == "--offline";
            ErrorLog.Initialize(Path.Combine(data ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReportDesk"), "logs"));
            service = new Service(data, config, offline);
            Send(new { type = "ready", protocol = 1 });
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                Request req;
                try { req = Json().Deserialize<Request>(line); if (req == null || req.args == null) throw new InvalidDataException(); }
                catch (Exception ex) { ErrorLog.Write("HostProtocol", ex, includeMessage: false); Send(new { type = "fatal", message = "后台通信格式错误。" }); return 2; }
                if (req.method == "cancel")
                {
                    lock (WorkGate) { if (active != null && activeId == Service.Number(req.args, "operationId")) active.Cancel(); }
                    Send(new { id = req.id, ok = true, data = new { } }); continue;
                }
                lock (WorkGate)
                {
                    if (active != null) { Send(new { id = req.id, ok = false, message = "正在处理另一项操作。", cancelled = false }); continue; }
                    active = new CancellationTokenSource(); activeId = req.id;
                    var cts = active;
                    work = Task.Run(() =>
                    {
                        object response;
                        try
                        {
                            var result = service.Handle(req.method, req.args, cts.Token, message => Send(new { type = "progress", operationId = req.id, message }));
                            response = new { id = req.id, ok = true, data = result };
                        }
                        catch (OperationCanceledException) { response = new { id = req.id, ok = false, cancelled = true, message = "操作已取消。" }; }
                        catch (Exception ex)
                        {
                            var notice = ErrorLog.Write(req.method, ex, includeMessage: false);
                            var message = ex is InvalidOperationException ? ErrorLog.Sanitize(ex.Message, service.Secrets(req.args)) : "操作失败，请查看本地错误日志。";
                            response = new { id = req.id, ok = false, cancelled = false, message = message + "\n" + notice };
                        }
                        lock (WorkGate) { active = null; cts.Dispose(); }
                        Send(response);
                    });
                }
            }
            lock (WorkGate) active?.Cancel();
            work?.GetAwaiter().GetResult(); service.Dispose(); return 0;
        }
        catch (Exception ex) { var notice = ErrorLog.Write("HostStartup", ex, includeMessage: false); Send(new { type = "fatal", message = "后台启动失败，请检查报表库与 report-visibility.xml 配置。\n" + notice }); return 1; }
    }
}

internal sealed class Service : IDisposable
{
    private readonly CatalogStore store;
    private readonly ReportVisibility visibility;
    private readonly bool offline;
    private readonly string configDirectory;
    private Catalog catalog;
    private string password = "";
    private QueryResult? result;
    private DataTable? lookup;
    private string resultId = "", lookupId = "", context = "";
    private int revision;
    public Service(string? data, string config, bool offline)
    {
        configDirectory = Path.GetFullPath(config); this.offline = offline;
        visibility = ReportVisibility.Load(Path.Combine(configDirectory, ReportVisibility.FileName));
        store = new CatalogStore(data); catalog = store.Load();
        try { password = CatalogStore.Unprotect(catalog.Connection.ProtectedPassword); }
        catch (Exception ex) { ErrorLog.Write("DecryptPassword", ex, includeMessage: false); }
    }
    public static string Text(Dictionary<string, object> a, string key, string fallback = "") => a.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? fallback : fallback;
    public static int Number(Dictionary<string, object> a, string key, int fallback = 0) => a.TryGetValue(key, out var v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback;
    private static bool Flag(Dictionary<string, object> a, string key) => a.TryGetValue(key, out var v) && v is bool b && b;
    public IEnumerable<string> Secrets(Dictionary<string, object> args)
    {
        IEnumerable<string> Flatten(object v) => v is Dictionary<string, object> d ? d.Values.SelectMany(Flatten) : new[] { Convert.ToString(v) ?? "" };
        return ErrorLog.ConnectionValues(catalog.Connection, password).Concat(Flatten(args)).Concat(catalog.Reports.SelectMany(r => r.Queries.Select(q => q.Sql)));
    }
    private IEnumerable<ReportDefinition> Visible() => catalog.Reports.Where(visibility.Includes);
    private ReportDefinition Report(Dictionary<string, object> a) => Visible().FirstOrDefault(r => r.Id == Text(a, "reportId")) ?? throw new InvalidOperationException("报表不存在或不在显示清单内。");
    private QueryDefinition Query(ReportDefinition r, Dictionary<string, object> a)
    {
        if (r.Issues.Count > 0) throw new InvalidOperationException("该报表存在待适配内容，不能执行。");
        int index = Number(a, "source");
        if (index < 0 || index >= r.Queries.Count) throw new InvalidOperationException("请选择有效的数据源。");
        return r.Queries[index];
    }
    private object List() => Visible().Select(r => new { id = r.Id, name = r.Name, category = r.Category, aliases = r.Aliases, notes = r.Notes, path = r.SourcePath, favorite = r.Favorite, lastUsed = r.LastUsed?.ToString("O"), status = r.Status, demo = r.IsDemo, issues = r.Issues, verified = r.Verified }).ToArray();
    private object Settings() => new { name = catalog.Connection.Name, mode = (int)catalog.Connection.Mode, host = catalog.Connection.Host, port = catalog.Connection.Port, service = catalog.Connection.Service, username = catalog.Connection.Username, tnsFile = catalog.Connection.TnsFile, tnsAlias = catalog.Connection.TnsAlias, remember = catalog.Connection.ProtectedPassword.Length > 0, hasPassword = password.Length > 0 };
    private void Clear() { result?.Table.Dispose(); result = null; resultId = ""; lookup?.Dispose(); lookup = null; lookupId = ""; revision++; }
    private object Details(Dictionary<string, object> a)
    {
        var r = Report(a); var index = Number(a, "source");
        if (index < 0 || index >= r.Queries.Count) index = 0;
        var needed = new List<string>();
        if (r.Issues.Count == 0 && r.Queries.Count > 0)
        {
            needed = SqlTemplate.Compile(r.Queries[index].Sql).RequiredNames;
            foreach (var p in r.Parameters.Where(p => needed.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToArray())
                if (p.LookupSql.Length > 0) foreach (var n in SqlTemplate.Compile(p.LookupSql).RequiredNames) if (!needed.Contains(n, StringComparer.OrdinalIgnoreCase)) needed.Add(n);
        }
        return new { id = r.Id, name = r.Name, notes = r.Notes, aliases = r.Aliases, category = r.Category, verified = r.Verified, favorite = r.Favorite, status = r.Status, demo = r.IsDemo, issues = r.Issues, path = r.SourcePath,
            sources = r.Queries.Select((q, i) => new { index = i, name = q.ToString() }).ToArray(), source = index,
            parameters = r.Parameters.Where(p => needed.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => new { name = p.Name, label = p.Label.Length > 0 ? p.Label : p.Name, kind = p.Kind, format = p.Format, implicitValue = p.Implicit, hasAll = p.HasAll, allValue = p.AllValue, lookup = p.LookupSql.Length > 0, initial = p.Kind == "DateTimeType" ? InitialDate(p) : "" }).ToArray() };
    }
    private static string InitialDate(ParameterDefinition p)
    {
        var now = DateTime.Now;
        try { now = now.AddMonths(p.AddMonths).AddDays(p.AddDays); } catch (ArgumentOutOfRangeException) { }
        return now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }
    private Dictionary<string, string> Values(ReportDefinition r, string sql, Dictionary<string, object> a)
    {
        var raw = a.TryGetValue("values", out var v) && v is Dictionary<string, object> d ? d : new Dictionary<string, object>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SqlTemplate.Compile(sql).RequiredNames)
        {
            var p = r.Parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("缺少参数定义。");
            if (!raw.ContainsKey(p.Name) || raw[p.Name] == null) throw new InvalidOperationException("请填写或选择所有查询条件。");
            var text = Text(raw, p.Name);
            if (p.Implicit && string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请填写已确认的上下文编码。");
            if (p.Kind == "DateTimeType")
            {
                if (!DateTime.TryParseExact(text, new[] { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidOperationException("日期格式无效。");
                text = date.ToString(string.IsNullOrWhiteSpace(p.Format) ? "yyyy-MM-dd HH:mm:ss" : p.Format, CultureInfo.InvariantCulture);
            }
            values[p.Name] = SqlTemplate.Transform(p, text);
        }
        return values;
    }
    private QueryResult Execute(ReportDefinition r, string sql, Dictionary<string, string> values, CancellationToken token)
    {
        if (r.IsDemo) return DemoData.Execute(values, token);
        if (offline) throw new InvalidOperationException("离线验证模式禁止真实数据库连接。");
        if (password.Length == 0) throw new InvalidOperationException("请先在连接设置中填写密码。");
        return OracleQueryService.Execute(catalog.Connection, password, sql, values, token);
    }
    private static string? Cell(object value) => value == DBNull.Value ? null : value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);
    private static void Filter(DataView view, string text)
    {
        text = string.Concat(text.Select(c => c == '\'' ? "''" : c == '[' ? "[[]" : c == ']' ? "[]]" : c == '%' ? "[%]" : c == '*' ? "[*]" : c.ToString()));
        view.RowFilter = text.Length == 0 ? "" : string.Join(" OR ", view.Table!.Columns.Cast<DataColumn>().Select(c => "Convert([" + c.ColumnName.Replace("\\", "\\\\").Replace("]", "\\]") + "], 'System.String') LIKE '%" + text + "%'") );
    }
    private object Page(DataTable table, Dictionary<string, object> a, string id)
    {
        int offset = Math.Max(0, Number(a, "offset"));
        // A transport window, never a query/result cap. All rows stay in the backend.
        int take = Math.Max(1, Math.Min(200, Number(a, "take", 200)));
        var view = table.DefaultView;
        return new { resultId = id, revision, total = table.Rows.Count, count = view.Count, offset,
            columns = table.Columns.Cast<DataColumn>().Select(c => new { name = c.ColumnName, type = c.DataType.Name }).ToArray(),
            rows = Enumerable.Range(offset, Math.Max(0, Math.Min(take, view.Count - offset))).Select(i => table.Columns.Cast<DataColumn>().Select(c => Cell(view[i][c.ColumnName])).ToArray()).ToArray() };
    }
    private DataTable Current(Dictionary<string, object> a)
    {
        if (result == null || resultId != Text(a, "resultId")) throw new InvalidOperationException("查询结果已失效，请重新查询。");
        if (a.ContainsKey("revision") && Number(a, "revision") != revision) throw new InvalidOperationException("表格视图已变化，请刷新后重试。");
        return result.Table;
    }
    private ConnectionSettings ReadSettings(Dictionary<string, object> a) => new() { Name = Text(a, "name"), Mode = (ConnectionMode)Number(a, "mode"), Host = Text(a, "host"), Port = Number(a, "port", 1521), Service = Text(a, "service"), Username = Text(a, "username"), TnsFile = Text(a, "tnsFile"), TnsAlias = Text(a, "tnsAlias") };
    public object Handle(string method, Dictionary<string, object> a, CancellationToken token, Action<string> progress)
    {
        switch (method)
        {
            case "bootstrap": return new { reports = List(), settings = Settings(), demoVisible = visibility.Includes(DemoData.Report()), offline };
            case "list": return List();
            case "select": Clear(); return Details(a);
            case "demo":
                if (!visibility.Includes(DemoData.Report())) throw new InvalidOperationException("演示报表不在显示清单内。");
                if (!catalog.Reports.Any(r => r.Id == "built-in-demo")) { catalog.Reports.Add(DemoData.Report()); Save(); }
                return List();
            case "definition": var report = Report(a); return new { title = report.Name, text = string.Join("\n\n", report.Queries.Select(q => q.Name + "\n" + q.Sql)) };
            case "favorite": var favorite = Report(a); bool old = favorite.Favorite; favorite.Favorite = Flag(a, "favorite"); try { Save(); } catch { favorite.Favorite = old; throw; } return List();
            case "metadata":
                var meta = Report(a); var before = (meta.Category, meta.Aliases, meta.Notes, meta.Verified);
                meta.Category = Text(a, "category"); meta.Aliases = Text(a, "aliases"); meta.Notes = Text(a, "notes"); meta.Verified = Flag(a, "verified");
                try { Save(); } catch { (meta.Category, meta.Aliases, meta.Notes, meta.Verified) = before; throw; } return List();
            case "import":
                progress("正在导入报表定义…"); ImportSummary summary;
                if (Flag(a, "folder")) summary = ReportImporter.ImportFolder(Text(a, "path"), token);
                else { summary = new ImportSummary(); var item = ReportImporter.ImportFile(Text(a, "path")); if (item == null) summary.Skipped++; else summary.Reports.Add(item); }
                token.ThrowIfCancellationRequested(); var prior = catalog.Reports.ToList(); ReportImporter.Merge(catalog, summary);
                try { Save(); } catch { catalog.Reports = prior; throw; }
                Clear(); return new { reports = List(), imported = summary.Reports.Count, pending = summary.Reports.Count(r => r.Issues.Count > 0), skipped = summary.Skipped, errors = summary.Errors };
            case "query":
                Clear(); var r = Report(a); var q = Query(r, a); var values = Values(r, q.Sql, a);
                progress(r.IsDemo ? "正在生成模拟结果…" : "正在连接、执行并读取结果；可请求取消…");
                QueryResult? generated = null;
                try
                {
                    generated = Execute(r, q.Sql, values, token); token.ThrowIfCancellationRequested();
                    var lastUsed = r.LastUsed; r.LastUsed = DateTime.Now; try { Save(); } catch { r.LastUsed = lastUsed; throw; }
                    result = generated; generated = null; resultId = Guid.NewGuid().ToString("N"); context = r.Name + " / " + q.Name + " / " + r.Status;
                    progress("结果已就绪。"); return Page(result.Table, a, resultId);
                }
                finally { generated?.Table.Dispose(); }
            case "view":
                var table = Current(a); int col = Number(a, "sort", -1);
                if (col < -1 || col >= table.Columns.Count) throw new InvalidOperationException("排序列无效。");
                var oldFilter = table.DefaultView.RowFilter; var oldSort = table.DefaultView.Sort;
                try
                {
                    Filter(table.DefaultView, Text(a, "filter"));
                    table.DefaultView.Sort = col < 0 ? "" : "[" + table.Columns[col].ColumnName.Replace("\\", "\\\\").Replace("]", "\\]") + "] " + (Flag(a, "descending") ? "DESC" : "ASC");
                }
                catch { table.DefaultView.RowFilter = oldFilter; table.DefaultView.Sort = oldSort; throw; }
                revision++; return Page(table, a, resultId);
            case "page": return Page(Current(a), a, resultId);
            case "export":
                var exportTable = Current(a); progress("正在导出当前筛选与排序的完整结果…");
                using (var snapshot = exportTable.DefaultView.ToTable()) XlsxExporter.Export(snapshot, Text(a, "path"), context, token);
                return new { count = exportTable.DefaultView.Count };
            case "lookup":
                var lr = Report(a); Query(lr, a); var lp = lr.Parameters.FirstOrDefault(p => p.Name == Text(a, "parameter")) ?? throw new InvalidOperationException("参数不存在。");
                if (lp.LookupSql.Length == 0) throw new InvalidOperationException("该参数没有选项 SQL。");
                lookup?.Dispose(); lookup = null; lookupId = "";
                progress("正在加载下拉选项…"); var options = Execute(lr, lp.LookupSql, Values(lr, lp.LookupSql, a), token);
                if (options.Table.Columns.Count < 2) { options.Table.Dispose(); throw new InvalidOperationException("选项 SQL 需要编码、名称两列。"); }
                lookup = options.Table; lookupId = Guid.NewGuid().ToString("N"); return Page(lookup, a, lookupId);
            case "lookupPage":
                if (lookup == null || lookupId != Text(a, "resultId")) throw new InvalidOperationException("选项已失效，请重新加载。");
                Filter(lookup.DefaultView, Text(a, "filter")); return Page(lookup, a, lookupId);
            case "settings": return Settings();
            case "saveSettings":
                var settings = ReadSettings(a); var nextPassword = Flag(a, "keepPassword") ? password : Text(a, "password");
                OracleQueryService.ConnectionString(settings, nextPassword);
                settings.ProtectedPassword = Flag(a, "remember") ? CatalogStore.Protect(nextPassword) : "";
                var previous = catalog.Connection; catalog.Connection = settings;
                try { Save(); } catch { catalog.Connection = previous; throw; }
                password = nextPassword; Clear(); return Settings();
            case "testConnection":
                if (offline) throw new InvalidOperationException("离线验证模式禁止真实数据库连接。");
                progress("正在测试连接；握手等待由网络与 TNS 决定…");
                var version = OracleQueryService.TestConnection(ReadSettings(a), Flag(a, "keepPassword") ? password : Text(a, "password")); token.ThrowIfCancellationRequested(); return new { version };
            case "discoverTns":
                if (offline) return Array.Empty<string>();
                return TnsDiscovery.Discover().Concat(TnsDiscovery.FindFiles(new[] { configDirectory, Path.Combine(configDirectory, "network", "admin") })).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            case "tnsAliases": return TnsNames.Read(Text(a, "path")).Keys.ToArray();
            case "clear": Clear(); return new { };
            default: throw new InvalidOperationException("不支持的后台操作。");
        }
    }
    private void Save() => store.Save(catalog);
    public void Dispose() => Clear();
}
