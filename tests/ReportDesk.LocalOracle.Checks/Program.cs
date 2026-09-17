using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using ReportDesk.Core;
using ReportDesk.Host;

internal static class LocalOracleChecks
{
    private static Dictionary<string, object> Args(params object[] pairs)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
        return result;
    }
    private static Dictionary<string, object> Call(Service service, string method, Dictionary<string, object> args)
    {
        var json = ReportDesk.Host.Program.Json();
        return json.Deserialize<Dictionary<string, object>>(json.Serialize(service.Handle(method, args, CancellationToken.None, _ => { })));
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Usage: <real-query.xml> <local-ObjectConfig.xml> [test-copy-directory]"); return 2; }
        var run = Path.GetFullPath(Path.Combine("artifacts/verification/desktop", "local-oracle-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(run); ErrorLog.Initialize(Path.Combine(run, "logs"));
        try
        {
            using var reader = XmlReader.Create(args[1], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var attribute = document.Descendants().Attributes().First(a => a.Name.LocalName == "connectionString" || a.Name.LocalName == "connection-string");
            var builder = new DbConnectionStringBuilder { ConnectionString = attribute.Value };
            // Credentials stay in memory; deliberately force loopback, never use the source's network endpoint.
            var username = Convert.ToString(builder["User ID"])!;
            var password = Convert.ToString(builder["Password"])!;
            var original = File.ReadAllBytes(args[0]);
            var source = args.Length > 2 ? Path.Combine(Path.GetFullPath(args[2]), Path.GetFileName(run)) : Path.Combine(run, "source");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, Path.GetFileName(args[0])); File.WriteAllBytes(file, original);
            using var service = new Service(Path.Combine(run, "data"), run, false);
            Call(service, "testConnection", Args("host", "127.0.0.1", "port", 1521, "service", "ORCL", "username", username, "password", password, "remember", false));
            Console.WriteLine("PASS local Oracle connection (127.0.0.1:1521/ORCL); password not persisted");
            Call(service, "import", Args("path", file));
            var report = ReportImporter.ImportFile(file)!;
            var editor = Call(service, "sqlEditorOpen", Args("reportId", report.Id));
            var snapshot = SqlXmlEditor.Read(file);
            var target = snapshot.Sources.First(s => s.Editable);
            // Preserve the real query and add a zero-row wrapper: exercise tables, syntax and binding without patient output.
            var query = "select * from (\n" + target.Sql + "\n) where 1 = 0\n-- REPORTDESK_LOCAL_ORACLE_SAVE_CHECK";
            var saved = Call(service, "sqlEditorSave", Args("reportId", report.Id, "token", editor["token"], "sourceIndex", target.Index, "sql", query));
            Assert((bool)saved["saved"] && (bool)saved["reloaded"], "Save/reload failed");
            Assert(SqlXmlEditor.Read(file).Sources[target.Index].Sql == query, "Disk SQL differs from submitted SQL");
            Assert(Directory.GetFiles(source, "*.bak").Length == 0, "Save must not create a backup");
            var refreshed = ReportImporter.ImportFile(file)!;
            var values = new Dictionary<string, object>();
            var requiredNames = SqlTemplate.Compile(query).RequiredNames.Select(ReportImporter.ParameterName).ToArray();
            foreach (var p in refreshed.Parameters.Where(p => requiredNames.Contains(p.Name)))
            {
                Assert(p.Kind == "DateTimeType", "Choose a real date-only report for this local check");
                values[p.Name] = "2026-01-01T00:00:00";
            }
            var result = Call(service, "query", Args("reportId", report.Id, "source", target.QueryIndex, "values", values));
            Assert(Convert.ToInt32(result["total"]) == 0, "Expected zero-row local verification");
            Console.WriteLine("PASS saved real report SQL executes through Host on local Oracle with bound dates (zero-row verification)");
            Assert(File.ReadAllBytes(args[0]).SequenceEqual(original), "Original test library changed");
            File.WriteAllText(Path.Combine(run, "PASS.txt"), "PASS loopback Oracle connection, real XML copy overwrite/readback/reload without backup, bound original report SQL zero-row execution. No patient results, no database writes, no saved password.\n");
            Console.WriteLine("PASS " + run); return 0;
        }
        catch (Exception ex)
        {
            // Driver/config exceptions can carry connection strings: emit only category and Oracle error code.
            Console.WriteLine("FAIL " + ex.GetType().Name);
            if (ex is Oracle.ManagedDataAccess.Client.OracleException oracle) Console.WriteLine("Oracle code: " + oracle.Number);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
