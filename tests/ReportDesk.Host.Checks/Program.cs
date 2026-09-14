using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Host;

internal static class PersistenceChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Dictionary<string, object> Args(string name = "saved") => new()
    {
        ["name"] = name, ["host"] = "localhost", ["service"] = "offline", ["port"] = 1521,
        ["username"] = "offline_reader", ["password"] = "SYNTHETIC_PERSISTENCE_CHECK", ["remember"] = true
    };
    private static Dictionary<string, object> Call(Service service, string method, Dictionary<string, object>? args = null, CancellationToken token = default) =>
        Program.Json().Deserialize<Dictionary<string, object>>(Program.Json().Serialize(service.Handle(method, args ?? new(), token, _ => { })));
    private static void Fails(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException ex) { Check(ex.Message.Contains(message), "Unexpected failure category"); return; }
        throw new Exception("Expected failure was not raised");
    }
    private static void Main()
    {
        var run = Path.GetFullPath(Path.Combine("artifacts", "verification", "desktop", "connection-persistence-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(run);
        ErrorLog.Initialize(Path.Combine(run, "logs"));
        var settingsFile = Path.Combine(run, "connection.json");
        int calls = 0;
        Func<ConnectionSettings, string, string> success = (s, p) =>
        {
            calls++; Check(p == "SYNTHETIC_PERSISTENCE_CHECK", "Password did not survive restart");
            return "OFFLINE-INJECTED";
        };
        using (var service = new Service(run, run, false, success))
        {
            Check((bool)Call(service, "settings")["remember"], "Remember must default on");
            var saved = Call(service, "testConnection", Args());
            Check((string)saved["version"] == "OFFLINE-INJECTED", "Injected success missing");
        }
        Check(calls == 1 && File.Exists(settingsFile), "Success was not persisted automatically");
        var original = File.ReadAllText(settingsFile);
        Check(!original.Contains("SYNTHETIC_PERSISTENCE_CHECK"), "Plaintext password persisted");
        var stored = new ConnectionSettingsStore(run).Load();
        Check(CatalogStore.Unprotect(stored.ProtectedPassword) == "SYNTHETIC_PERSISTENCE_CHECK", "DPAPI round trip failed");
        using (var service = new Service(run, run, false, success))
        {
            var settings = Call(service, "settings");
            Check((bool)settings["hasPassword"] && !settings.ContainsKey("password"), "Restart password metadata invalid");
            var args = Args(); args.Remove("password"); args["keepPassword"] = true;
            Call(service, "testConnection", args);
        }
        original = File.ReadAllText(settingsFile);
        using (var service = new Service(run, run, false, (_, _) => throw new InvalidOperationException("INJECTED_FAILURE")))
        {
            Fails(() => Call(service, "testConnection", Args("rejected")), "INJECTED_FAILURE");
            Check((string)Call(service, "settings")["name"] == "saved", "Failure changed in-memory settings");
        }
        Check(File.ReadAllText(settingsFile) == original, "Failure overwrote saved settings");
        using (var cts = new CancellationTokenSource())
        using (var service = new Service(run, run, false, (_, _) => { cts.Cancel(); return "CANCELLED"; }))
        {
            bool cancelled = false;
            try { Call(service, "testConnection", Args("cancelled"), cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && File.ReadAllText(settingsFile) == original, "Cancelled connection was saved");
        }
        using (var service = new Service(run, run, true, (_, _) => throw new Exception("Offline mode reached connection delegate")))
            Fails(() => Call(service, "testConnection", Args()), "禁止真实数据库");
        using (var service = new Service(run, run, false, success))
        {
            var args = Args(); args["remember"] = false; Call(service, "testConnection", args);
        }
        using (var service = new Service(run, run, true))
        {
            var settings = Call(service, "settings");
            Check(!(bool)settings["hasPassword"] && !(bool)settings["remember"], "Password opt-out did not survive restart");
        }
        // Lock the destination to exercise atomic-save failure without changing permissions.
        original = File.ReadAllText(settingsFile);
        using (var service = new Service(run, run, false, success))
        using (var locked = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Fails(() => Call(service, "testConnection", Args("unwritable")), "连接成功，但配置保存失败");
            Check((string)Call(service, "settings")["name"] == "saved", "Write failure replaced session config");
        }
        Check(File.ReadAllText(settingsFile) == original, "Write failure damaged prior settings");
        foreach (var file in Directory.GetFiles(run, "*.log", SearchOption.AllDirectories))
            Check(!File.ReadAllText(file).Contains("SYNTHETIC_PERSISTENCE_CHECK"), "Secret appeared in logs");
        File.WriteAllText(Path.Combine(run, "PASS.txt"), "PASS injected connection success/failure/cancellation, automatic atomic save, DPAPI restart reuse, opt-out, disk failure and offline guard. Oracle NOT RUN.");
        Console.WriteLine("PASS " + run);
    }
}
