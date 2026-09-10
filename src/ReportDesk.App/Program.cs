using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ReportDesk.Core;

namespace ReportDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var smoke = args.Length == 2 && args[0] == "--smoke";
        if (smoke) ErrorLog.Initialize(Path.Combine(Path.GetFullPath(args[1]), "logs"));
        Application.ThreadException += (_, e) => MessageBox.Show("程序发生错误。\n" + ErrorLog.Write("UIUnhandled", e.Exception, includeMessage: false), "ReportDesk");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("ProcessUnhandled", e.ExceptionObject as Exception ?? new Exception("未知异常"), includeMessage: false);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => ErrorLog.Write("TaskUnhandled", e.Exception, includeMessage: false);
        using var mutex = new Mutex(true, "Local\\ReportDesk-" + Environment.UserName + (smoke ? "-smoke-" + Guid.NewGuid().ToString("N") : ""), out var created);
        if (!created) { MessageBox.Show("ReportDesk 已在运行，请切换到已有窗口。", "报表管理"); return 0; }
        try
        {
            if (smoke) Directory.CreateDirectory(args[1]);
            var form = new MainForm(smoke ? args[1] : null);
            if (smoke)
            {
                form.Shown += async (_, _) =>
                {
                    try { await form.SmokeAsync(args[1]); File.WriteAllText(Path.Combine(args[1], "smoke.txt"), "PASS; process=" + (Environment.Is64BitProcess ? "x64" : "x86")); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "smoke.txt"), "FAIL: " + ex); Environment.ExitCode = 1; }
                    finally { form.Close(); }
                };
            }
            Application.Run(form); return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            if (smoke) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "smoke.txt"), "FAIL: " + ex); }
            else MessageBox.Show("启动失败。已有报表库未被覆盖。\n" + ErrorLog.Sanitize(ex.Message) + "\n" + ErrorLog.Write("Startup", ex, includeMessage: false), "ReportDesk");
            return 1;
        }
    }
}
