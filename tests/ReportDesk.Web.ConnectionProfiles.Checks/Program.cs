using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

// Exercises delivered Web/Core binaries with injected login outcomes only.
// Synthetic connection metadata and passwords stay in this isolated test root.
internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static Assembly web = null!, core = null!;
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    private static Dictionary<string, object> A(params object[] values)
    { var result = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1]; return result; }
    private static Dictionary<string, object> D(object value) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(value));
    private static object Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    private static Dictionary<string, object> Call(object service, string method, Dictionary<string, object>? args = null, CancellationToken token = default)
        => D(Invoke(service, "WebAdminHandle", method, args ?? A(), token, (Action<string>)(_ => { })));
    private static Dictionary<string, object>[] Profiles(Dictionary<string, object> value)
        => ((IEnumerable)value["profiles"]).Cast<object>().Select(D).ToArray();
    private static Dictionary<string, object> Input(string name, string secret, bool remember = true)
        => A("name", name, "host", "localhost", "port", 1521, "service", "offline", "username", "offline_reader", "password", secret, "remember", remember);
    private static IDisposable Service(string root, bool offline = false, Func<object, string, string>? tester = null)
    {
        var settingsType = core.GetType("ReportDesk.Core.ConnectionSettings")!;
        var settings = Expression.Parameter(settingsType, "settings"); var password = Expression.Parameter(typeof(string), "password");
        tester ??= (_, _) => "OFFLINE-INJECTED";
        var type = typeof(Func<,,>).MakeGenericType(settingsType, typeof(string), typeof(string));
        var test = Expression.Lambda(type, Expression.Invoke(Expression.Constant(tester), Expression.Convert(settings, typeof(object)), password), settings, password).Compile();
        return (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Host.Service")!, root, root, offline, test, true)!;
    }
    private static void Fails(Action action, string expected)
    {
        try { action(); }
        catch (Exception ex) { Check(ex.Message.Contains(expected), "failure category: " + expected); return; }
        throw new Exception("Expected rejection: " + expected);
    }
    private static string CurrentName(object service) => Property<string>(Property<object>(Invoke(service, "WebGetConnection"), "Settings"), "Name");
    private static string CurrentPassword(object service) => Property<string>(Invoke(service, "WebGetConnection"), "Password");

    private static int Main(string[] args)
    {
        try
        {
            string bin = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                string file = Path.Combine(bin, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            core = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Core.dll"));
            web = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Web.dll"));
            string root = Path.Combine(Path.GetTempPath(), "ReportDesk-ConnectionProfiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            core.GetType("ReportDesk.Core.ErrorLog")!.GetMethod("Initialize")!.Invoke(null, new object[] { Path.Combine(root, "Logs") });
            string file = Path.Combine(root, "connection.json");
            const string firstSecret = "SYNTHETIC_PROFILE_FIRST", secondSecret = "SYNTHETIC_PROFILE_SECOND", memorySecret = "SYNTHETIC_PROFILE_MEMORY";
            using (var legacy = Service(root))
                Invoke(legacy, "Handle", "saveSettings", Input("first", firstSecret), CancellationToken.None, (Action<string>)(_ => { }));
            string original = File.ReadAllText(file);
            string firstId, secondId, memoryId;
            using (var service = Service(root, tester: (s, p) => { Check(p == secondSecret, "test uses selected profile password only"); return "OFFLINE-INJECTED"; }))
            {
                var first = Call(service, "settings"); firstId = (string)first["profileId"];
                Check(Profiles(first).Length == 1 && (string)first["name"] == "first" && (bool)first["hasPassword"], "legacy single connection loaded without rewriting");
                Check(File.ReadAllText(file) == original, "reading profiles does not migrate on disk");
                var input = Input("second", secondSecret); input["newProfile"] = true;
                var second = Call(service, "saveSettings", input); secondId = (string)second["profileId"];
                Check(secondId != firstId && Profiles(second).Length == 2 && CurrentName(service) == "first", "adding a connection preserves active connection");
                Check((string)Call(service, "settings", A("profileId", secondId))["name"] == "second" && CurrentPassword(service) == firstSecret, "viewing another profile does not switch credentials");
                var test = Input("second", "ignored"); test["profileId"] = secondId; test["keepPassword"] = true;
                var tested = Call(service, "testConnection", test);
                Check((string)tested["version"] == "OFFLINE-INJECTED" && CurrentName(service) == "first", "testing inactive profile saves without activation");
                var memory = Input("memory", memorySecret, false); memory["newProfile"] = true;
                var memoryDto = Call(service, "saveSettings", memory); memoryId = (string)memoryDto["profileId"];
                Check((bool)memoryDto["hasPassword"] && !(bool)memoryDto["remember"], "per-profile session password retained without DPAPI persistence");
                string text = File.ReadAllText(file), dto = Json.Serialize(memoryDto);
                Check(!text.Contains(firstSecret) && !text.Contains(secondSecret) && !text.Contains(memorySecret), "disk contains no plaintext profile passwords");
                Check(!dto.Contains("ProtectedPassword") && !dto.Contains("password\"") && !dto.Contains(memorySecret), "HTTP settings DTO never contains passwords or ciphertext");
                var snapshot = (IDisposable)Invoke(service, "WebCreateContext");
                using (snapshot)
                {
                    var activated = Call(service, "activateConnection", A("profileId", secondId));
                    Check((string)activated["activeProfileId"] == secondId && CurrentPassword(service) == secondSecret, "explicit activation selects the intended credentials");
                    Check(CurrentName(snapshot) == "first" && CurrentPassword(snapshot) == firstSecret, "existing query snapshot survives connection activation");
                    Invoke(snapshot, "WebRefresh", service);
                    Check(CurrentName(snapshot) == "second", "next context refresh receives activated connection");
                }
                var store = Activator.CreateInstance(web.GetType("ReportDesk.Host.ConnectionSettingsStore")!, root)!;
                Check(Property<string>(Invoke(store, "Load"), "Name") == "second", "legacy reader still loads active root projection");
                var clone = Input("third", ""); clone["newProfile"] = true; clone["keepPassword"] = true;
                Fails(() => Call(service, "saveSettings", clone), "新增连接请单独输入密码");
                var unnamed = Input(" ", ""); unnamed["newProfile"] = true;
                Fails(() => Call(service, "saveSettings", unnamed), "请填写连接名");
                Fails(() => Call(service, "activateConnection", A("profileId", "missing")), "此连接已不存在");
                Call(service, "activateConnection", A("profileId", memoryId));
                Check(CurrentPassword(service) == memorySecret, "memory-only password isolated and reusable in current process");
                Call(service, "activateConnection", A("profileId", secondId));
            }
            using (var service = Service(root))
            {
                Check((bool)Call(service, "settings", A("profileId", firstId))["hasPassword"] && (bool)Call(service, "settings", A("profileId", secondId))["hasPassword"], "each remembered profile decrypts after restart");
                Check(!(bool)Call(service, "settings", A("profileId", memoryId))["hasPassword"], "memory-only password is absent after restart");
                var edit = Input("", "", true); edit["keepPassword"] = true;
                var saved = Call(service, "saveSettings", edit);
                Check((string)saved["profileId"] == secondId && Profiles(saved).Length == 3 && CurrentName(service) == "", "legacy no-ID save preserves empty-name compatibility and other profiles");
            }
            original = File.ReadAllText(file);
            var change = Input("rejected", "", true); change["profileId"] = firstId; change["keepPassword"] = true;
            using (var service = Service(root, tester: (_, p) => throw new InvalidOperationException("INJECTED_FAILURE " + p)))
            {
                try { Call(service, "testConnection", change); throw new Exception("Expected injected failure"); }
                catch (InvalidOperationException ex) { Check(!ex.Message.Contains(firstSecret), "inactive password is sanitized from test errors"); }
                Check(File.ReadAllText(file) == original && (string)Call(service, "settings", A("profileId", firstId))["name"] == "first", "failed connection preserves disk and memory");
            }
            using (var cancelled = new CancellationTokenSource())
            using (var service = Service(root, tester: (_, _) => { cancelled.Cancel(); return "CANCELLED"; }))
            {
                try { Call(service, "testConnection", change, cancelled.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { Check(File.ReadAllText(file) == original, "cancelled test does not save"); }
            }
            using (var service = Service(root))
            {
                Call(service, "settings");
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Fails(() => Call(service, "testConnection", change), "配置保存失败");
                Check(File.ReadAllText(file) == original && (string)Call(service, "settings", A("profileId", firstId))["name"] == "first", "atomic write failure keeps previous profile list and active connection");
            }
            using (var service = Service(root, true, (_, _) => throw new Exception("REAL CONNECTION PATH REACHED")))
                Fails(() => Call(service, "testConnection", change), "禁止真实数据库连接");
            VerifyTnsAndPermissions(root);
            VerifyStaleWriters(root);
            foreach (var log in Directory.GetFiles(root, "*.log", SearchOption.AllDirectories))
                Check(!File.ReadAllText(log).Contains(firstSecret) && !File.ReadAllText(log).Contains(secondSecret) && !File.ReadAllText(log).Contains(memorySecret), "logs do not retain profile passwords");
            Console.WriteLine("PASS " + checks + " connection profile checks; Oracle NOT RUN; " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void VerifyTnsAndPermissions(string root)
    {
        string data = Path.Combine(root, "backend"), config = Path.Combine(data, "config"), reports = Path.Combine(root, "empty-reports");
        Directory.CreateDirectory(config); Directory.CreateDirectory(reports);
        string tns = Path.Combine(config, "tnsnames.ora");
        File.WriteAllText(tns, "OFFLINE_ALIAS=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=offline)))");
        using var backend = (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.Services.WebBackend")!, data, reports, true)!;
        using var context = (IDisposable)Invoke(backend, "CreateSession");
        object BackendCall(string method, Dictionary<string, object> args, bool admin = true)
            => Invoke(backend, "Call", context, method, args, CancellationToken.None, (Action<string>)(_ => { }), admin);
        var input = Input("tns", "SYNTHETIC_TNS", false); input["mode"] = 1; input["tnsFile"] = tns; input["tnsAlias"] = "OFFLINE_ALIAS"; input["newProfile"] = true;
        var saved = D(BackendCall("saveSettings", input)); string id = (string)saved["profileId"];
        backend.GetType().GetProperty("ValidateApprovedTns", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(backend, (Action<string>)(_ => throw new InvalidOperationException("TNS permission revoked")));
        Fails(() => BackendCall("activateConnection", A("profileId", id)), "TNS permission revoked");
        Check((string)D(BackendCall("settings", A()))["activeProfileId"] != id, "revoked TNS approval prevents activation");
        Fails(() => BackendCall("activateConnection", A("profileId", id), false), "不允许调用此维护操作");
        var current = Invoke(backend, "GetConnection"); var settings = Property<object>(current, "Settings");
        settings.GetType().GetProperty("Name")!.SetValue(settings, "trusted-injected");
        Invoke(backend, "SetConnectionSettings", settings, "SYNTHETIC_INJECTED");
        Check((string)D(BackendCall("settings", A()))["name"] == "trusted-injected", "trusted injection updates active settings after profile cache loads");
    }

    private static void VerifyStaleWriters(string root)
    {
        string folder = Path.Combine(root, "stale-writers"); Directory.CreateDirectory(folder);
        using var first = Service(folder);
        Call(first, "saveSettings", Input("memory-active", "SYNTHETIC_STALE_MEMORY", false));
        using var second = Service(folder);
        Call(first, "settings"); Call(second, "settings");
        var other = Input("other-worker", "SYNTHETIC_OTHER", false); other["newProfile"] = true;
        Call(second, "saveSettings", other);
        string file = Path.Combine(folder, "connection.json"), accepted = File.ReadAllText(file);
        var stale = Input("stale-worker", "SYNTHETIC_STALE", false); stale["newProfile"] = true;
        Fails(() => Call(first, "saveSettings", stale), "其他进程修改");
        Check(File.ReadAllText(file) == accepted, "stale writer cannot overwrite another worker's profile list");
        var refreshed = Call(first, "settings");
        Check(Profiles(refreshed).Length == 2 && CurrentPassword(first) == "SYNTHETIC_STALE_MEMORY", "reopening settings reloads profiles and preserves unchanged session-only password");
        var retried = Call(first, "saveSettings", stale);
        Check(Profiles(retried).Length == 3, "explicit retry after refresh preserves both workers' connections");
        using var late = Service(folder);
        var newest = Call(first, "activateConnection", A("profileId", (string)retried["profileId"]));
        var lateSettings = Call(late, "settings");
        Check((string)lateSettings["activeProfileId"] == (string)newest["activeProfileId"] && CurrentName(late) == "stale-worker", "first settings read synchronizes an activity change after worker startup");
        using var injected = Service(folder);
        var snapshot = Invoke(injected, "WebGetConnection"); var settings = Property<object>(snapshot, "Settings");
        settings.GetType().GetProperty("Name")!.SetValue(settings, "before-cache-injected");
        Invoke(injected, "WebInjectConnection", settings, "SYNTHETIC_BEFORE_CACHE");
        Check((string)Call(injected, "settings")["name"] == "before-cache-injected" && CurrentPassword(injected) == "SYNTHETIC_BEFORE_CACHE", "trusted injection remains visible before profile cache loads");
        string good = File.ReadAllText(file);
        File.WriteAllText(file, "null");
        Fails(() => Call(first, "settings"), "连接配置内容为空");
        Check(File.ReadAllText(file) == "null", "null configuration is preserved for recovery");
        File.WriteAllText(file, "{\"WebConnectionProfilesVersion\":99}");
        Fails(() => Call(first, "settings"), "格式不受支持");
        Check(File.ReadAllText(file).Contains("99"), "unsupported profile format is never rewritten");
        File.Delete(file);
        Fails(() => Call(first, "settings"), "连接配置文件已丢失");
        File.WriteAllText(file, good);
    }
}
