using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace ReportDesk.Core;

public static class TnsDiscovery
{
    // Discover known locations only; never scan drives or read another app's credentials.
    public static string[] Discover()
    {
        var directories = new List<string>();
        AddDirectory(directories, Environment.GetEnvironmentVariable("TNS_ADMIN"));
        AddHome(directories, Environment.GetEnvironmentVariable("ORACLE_HOME"));
        directories.Add(AppDomain.CurrentDomain.BaseDirectory);
        AddHome(directories, AppDomain.CurrentDomain.BaseDirectory);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var oracle = root.OpenSubKey(@"SOFTWARE\ORACLE", false);
                if (oracle == null) continue;
                ReadKey(oracle, directories);
                foreach (var name in oracle.GetSubKeyNames())
                {
                    using var home = oracle.OpenSubKey(name, false);
                    if (home != null) ReadKey(home, directories);
                }
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException) { }
        }
        return FindFiles(directories);
    }

    public static string[] FindFiles(IEnumerable<string> directories)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Path.IsPathRooted(expanded)) continue;
                var path = Path.GetFullPath(Path.Combine(expanded, "tnsnames.ora"));
                if (File.Exists(path)) files.Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException) { }
        }
        return files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ReadKey(RegistryKey key, List<string> directories)
    {
        AddDirectory(directories, key.GetValue("TNS_ADMIN") as string);
        AddHome(directories, key.GetValue("ORACLE_HOME") as string);
    }
    private static void AddDirectory(List<string> directories, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) directories.Add(value!); }
    private static void AddHome(List<string> directories, string? value)
    { if (!string.IsNullOrWhiteSpace(value)) directories.Add(value!.Trim().Trim('"').TrimEnd('\\', '/') + @"\network\admin"); }
}
