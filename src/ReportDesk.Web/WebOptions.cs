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
    public bool SyncEnabled { get; set; }
    public bool SyncSampleValidated { get; set; }
    public bool AllowLoopbackHttp { get; set; }
    public int SyncSeconds { get; set; } = 30;
    public int MaxConcurrentOperations { get; set; } = 3;
    public int IdleMinutes { get; set; } = 60;
    public int MaxSessions { get; set; } = 64;
    public string ReportsDirectory => Path.Combine(DataDirectory, "Reports");
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    public static WebOptions Load(string site)
    {
        string Read(string key, string fallback = "") => ConfigurationManager.AppSettings["ReportDesk." + key] ?? fallback;
        bool Flag(string key, bool fallback) => bool.TryParse(Read(key), out var value) ? value : fallback;
        int Number(string key, int fallback) => int.TryParse(Read(key), out var value) ? value : fallback;
        string[] List(string key) => Read(key).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        return new WebOptions {
            SiteDirectory = site, DataDirectory = Read("DataDirectory"),
            MaintenanceAddresses = List("MaintenanceAddresses"),
            TnsFiles = List("TnsFiles"),
            Offline = Flag("Offline", true), SyncEnabled = Flag("SyncEnabled", false),
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
        if (IdleMinutes < 1 || MaxSessions < 1 || MaxConcurrentOperations < 1 || SyncSeconds < 5)
            throw new InvalidOperationException("Web 容量和同步配置无效。");
        foreach (var address in MaintenanceAddresses)
            if (!IPAddress.TryParse(address, out _)) throw new InvalidOperationException("维护白名单必须使用明确 IP 地址。");
        if (Offline && SyncEnabled) throw new InvalidOperationException("离线模式不允许启用数据库自动同步。");
        EnsureNoLinks(DataDirectory); EnsureNoLinks(ReportsDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);
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
        var text = NormalizeReportRelative(relative);
        var full = text.Length == 0
            ? Path.GetFullPath(ReportsDirectory)
            : Path.GetFullPath(Path.Combine(ReportsDirectory, text.Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(full, ReportsDirectory) && !string.Equals(full, Path.GetFullPath(ReportsDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("报表路径超出允许范围。");
        return full;
    }

    public bool IsMaintenance(string address)
    {
        if (MaintenanceAddresses == null || MaintenanceAddresses.Length == 0) return true;
        return IPAddress.TryParse(address, out var remote) &&
            MaintenanceAddresses.Any(value => IPAddress.TryParse(value, out var allowed) && allowed.MapToIPv6().Equals(remote.MapToIPv6()));
    }
}
