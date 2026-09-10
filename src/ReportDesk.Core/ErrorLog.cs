using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace ReportDesk.Core;

public static class ErrorLog
{
    private static readonly object Gate = new();
    public static string DirectoryPath { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReportDesk", "logs");
    // Called at startup, also isolates offline checks from the user's logs.
    public static void Initialize(string directory) { DirectoryPath = Path.GetFullPath(directory); }
    public static IEnumerable<string> ConnectionValues(ConnectionSettings settings, string password) =>
        new[] { password, settings.ProtectedPassword, settings.Username, settings.Host, settings.Service, settings.TnsFile, settings.TnsAlias };

    public static string Sanitize(string text, IEnumerable<string>? sensitive = null)
    {
        // Remove whole connection/SQL tails before replacing individual values.
        text = Regex.Replace(text, @"(?is)\(\s*DESCRIPTION.*", "[连接描述已隐藏]");
        text = Regex.Replace(text, @"(?is)\b(?:SELECT|WITH|INSERT|UPDATE|DELETE|MERGE)\s+.*", "[SQL 已隐藏]");
        text = Regex.Replace(text, @"(?im)\b(?:password|pwd|user\s*id|data\s*source|token|secret)\s*=.*$", "[连接凭据已隐藏]");
        text = Regex.Replace(text, "\"(?:[^\"]|\"\")*\"|'(?:[^']|'')*'", "[引用内容已隐藏]");
        foreach (var value in (sensitive ?? Array.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderByDescending(x => x.Length))
            text = text.Replace(value, "[已隐藏]");
        return text.Replace("\r", " ").Replace("\n", " ");
    }

    public static string Write(string operation, Exception exception, IEnumerable<string>? sensitive = null, bool includeMessage = true)
    {
        try
        {
            lock (Gate)
            {
                if (exception.Data["ReportDesk.LogNotice"] is string previous) return previous;
                var secrets = (sensitive ?? Array.Empty<string>()).ToArray();
                var entry = new StringBuilder();
                entry.AppendLine(DateTimeOffset.Now.ToString("O") + " ERROR operation=" + operation + " version=" + typeof(ErrorLog).Assembly.GetName().Version + " process=" + (Environment.Is64BitProcess ? "x64" : "x86"));
                void Describe(Exception ex, int level)
                {
                    if (level > 12) return;
                    entry.AppendLine("exception=" + ex.GetType().FullName + " hresult=" + ex.HResult + (ex is OracleException oracle ? " oracleCode=" + oracle.Number : ""));
                    entry.AppendLine("message=" + (includeMessage ? Sanitize(ex.Message, secrets) : "[未捕获异常或数据错误：仅记录类型、错误码与调用栈]"));
                    // Method identities only: no source paths, local values, Exception.Data or SQL.
                    foreach (var frame in new StackTrace(ex, false).GetFrames() ?? Array.Empty<StackFrame>())
                    { var method = frame.GetMethod(); entry.AppendLine("  at " + method?.DeclaringType?.FullName + "." + method?.Name); }
                    if (ex is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) Describe(inner, level + 1);
                    else if (ex.InnerException != null) Describe(ex.InnerException, level + 1);
                }
                Describe(exception, 0); entry.AppendLine();
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "ReportDesk-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                File.AppendAllText(path, entry.ToString(), new UTF8Encoding(false));
                var notice = "错误日志：" + path; exception.Data["ReportDesk.LogNotice"] = notice; return notice;
            }
        }
        catch { return "错误日志写入失败，请检查日志目录权限或磁盘空间：" + DirectoryPath; }
    }
}
