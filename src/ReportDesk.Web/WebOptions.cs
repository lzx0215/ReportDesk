using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;

namespace ReportDesk.Web;

public sealed class WebOptions
{
    public string DataDirectory { get; set; } = "";
    public string SiteDirectory { get; set; } = "";
    public string[] MaintenanceAddresses { get; set; } = Array.Empty<string>();
    public string[] TnsFiles { get; set; } = Array.Empty<string>();
    public bool Offline { get; set; } = true;
    public bool ServerReportsEnabled { get; set; }
    public bool SyncEnabled { get; set; }
    public bool SyncSampleValidated { get; set; }
    public bool AllowLoopbackHttp { get; set; }
    public int SyncSeconds { get; set; } = 30;
    public int MaxConcurrentOperations { get; set; } = 3;
    public int IdleMinutes { get; set; } = 60;
    public int MaxSessions { get; set; } = 64;
    private string reportsDirectory = "";
    // Used only when server reports are explicitly enabled. Local mode ignores it.
    public string ReportsDirectory { get => string.IsNullOrWhiteSpace(reportsDirectory) ? Path.Combine(DataDirectory, "Reports") : reportsDirectory; set => reportsDirectory = value; }
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    public static WebOptions Load(string site)
    {
        string Read(string key, string fallback = "") => ConfigurationManager.AppSettings["ReportDesk." + key] ?? fallback;
        bool Flag(string key, bool fallback) => bool.TryParse(Read(key), out var value) ? value : fallback;
        int Number(string key, int fallback) => int.TryParse(Read(key), out var value) ? value : fallback;
        string[] List(string key) => Read(key).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return new WebOptions {
            SiteDirectory = site, DataDirectory = Read("DataDirectory"), ReportsDirectory = Read("ReportsDirectory"),
            MaintenanceAddresses = List("MaintenanceAddresses"),
            TnsFiles = List("TnsFiles"),
            Offline = Flag("Offline", true), ServerReportsEnabled = Flag("ServerReportsEnabled", false), SyncEnabled = Flag("SyncEnabled", false),
            SyncSampleValidated = Flag("SyncSampleValidated", false),
            AllowLoopbackHttp = Flag("AllowLoopbackHttp", false),
            SyncSeconds = Number("SyncSeconds", 30),
            IdleMinutes = Number("IdleMinutes", 60), MaxConcurrentOperations = Number("MaxConcurrentOperations", 3), MaxSessions = Number("MaxSessions", 64)
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) || !Path.IsPathRooted(DataDirectory))
            throw new InvalidOperationException("请先配置站点之外的 ReportDesk.DataDirectory。");
        DataDirectory = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        SiteDirectory = Path.GetFullPath(SiteDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (Inside(DataDirectory, SiteDirectory) || Inside(SiteDirectory, DataDirectory) || DataDirectory == Path.GetPathRoot(DataDirectory)?.TrimEnd('\\'))
            throw new InvalidOperationException("数据目录必须与网站目录分离，且不能是磁盘根目录。");
        if (ServerReportsEnabled)
        {
            bool explicitReports = !string.IsNullOrWhiteSpace(reportsDirectory);
            if (!Path.IsPathRooted(ReportsDirectory)) throw new InvalidOperationException("ReportDesk.ReportsDirectory 必须是服务器绝对路径。");
            var reportRoot = Path.GetFullPath(ReportsDirectory).TrimEnd('\\', '/');
            if (reportRoot == Path.GetPathRoot(reportRoot)?.TrimEnd('\\', '/') ||
                Inside(reportRoot, SiteDirectory) || Inside(SiteDirectory, reportRoot) || Inside(DataDirectory, reportRoot) ||
                new[] { "config", "Logs", "SyncState", "LocalWorkspaces" }.Any(name => Inside(reportRoot, Path.Combine(DataDirectory, name)) || Inside(Path.Combine(DataDirectory, name), reportRoot)))
                throw new InvalidOperationException("报表目录不能是磁盘根目录，也不能与网站、配置、日志、同步状态或本机临时工作区重叠。");
            EnsureNoLinks(reportRoot);
            if (explicitReports && !Directory.Exists(reportRoot))
                throw new InvalidOperationException("ReportDesk.ReportsDirectory 指定的报表目录不存在或不可访问，请核对服务器路径和应用池权限。");
            if (explicitReports) reportsDirectory = reportRoot;
        }
        if (IdleMinutes < 1 || MaxSessions < 1 || MaxConcurrentOperations < 1 || (ServerReportsEnabled && SyncSeconds < 5))
            throw new InvalidOperationException("Web 容量和同步配置无效。");
        foreach (var address in MaintenanceAddresses)
            if (!IPAddress.TryParse(address, out _)) throw new InvalidOperationException("维护白名单必须使用明确 IP 地址。");
        if (ServerReportsEnabled && Offline && SyncEnabled) throw new InvalidOperationException("离线模式不允许启用数据库自动同步。");
        EnsureNoLinks(DataDirectory);
        Directory.CreateDirectory(DataDirectory);
        if (ServerReportsEnabled)
        {
            EnsureNoLinks(ReportsDirectory);
            Directory.CreateDirectory(ReportsDirectory);
        }
    }

    internal static bool Inside(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不允许通过链接访问报表或配置目录。");
    }

    public string NormalizeReportRelative(string relative)
    {
        var text = (relative ?? "").Trim().Replace('\\', '/');
        if (text.Length == 0) return "";
        if (Path.IsPathRooted(text) || text.IndexOf(':') >= 0 || text.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new InvalidOperationException("仅允许服务器报表目录内的相对路径。");
        return text;
    }

    public string ResolveReportPath(string relative)
    {
        RequireServerReports();
        var text = NormalizeReportRelative(relative);
        var full = text.Length == 0 ? Path.GetFullPath(ReportsDirectory) : Path.GetFullPath(Path.Combine(ReportsDirectory, text.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(full, ReportsDirectory) && !string.Equals(full, ReportsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("报表路径超出允许范围。");
        EnsureNoLinks(full);
        if (!Directory.Exists(full) && (!File.Exists(full) || !full.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请选择现有报表目录或 XML 文件。");
        return full;
    }

    public string ResolveBrowseDirectory(string relative)
    {
        var full = ResolveReportPath(relative);
        if (!Directory.Exists(full)) throw new InvalidOperationException("请选择现有报表目录。");
        return full;
    }

    public string MapInsideReports(string relative)
    {
        RequireServerReports();
        var text = NormalizeReportRelative(relative);
        var full = text.Length == 0
            ? Path.GetFullPath(ReportsDirectory)
            : Path.GetFullPath(Path.Combine(ReportsDirectory, text.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(full, ReportsDirectory) && !string.Equals(full, Path.GetFullPath(ReportsDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("报表路径超出允许范围。");
        return full;
    }

    internal const string ServerReportsDisabledMessage = Services.WebBackend.ServerReportsDisabledMessage;
    internal void RequireServerReports()
    {
        if (!ServerReportsEnabled) throw new WebUserException(ServerReportsDisabledMessage);
    }

    public bool IsMaintenance(string address)
    {
        if (MaintenanceAddresses == null || MaintenanceAddresses.Length == 0) return true;
        return IPAddress.TryParse(address, out var remote) &&
            MaintenanceAddresses.Any(value => IPAddress.TryParse(value, out var allowed) && allowed.MapToIPv6().Equals(remote.MapToIPv6()));
    }
}
