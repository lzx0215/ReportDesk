using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using ReportDesk.Core;
using ReportDesk.Host;

internal static class LocalLayoutChecks
{
    private static Dictionary<string, object> Args(params object[] pairs)
    {
        var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1]; return result;
    }
    private static Dictionary<string, object> Call(Service service, string method, Dictionary<string, object> args)
    {
        var json = ReportDesk.Host.Program.Json(); return json.Deserialize<Dictionary<string, object>>(json.Serialize(service.Handle(method, args, CancellationToken.None, _ => { })));
    }
    private sealed class CheckFailure : Exception { internal CheckFailure(string message) : base(message) { } }
    private static void Assert(bool value, string message) { if (!value) throw new CheckFailure(message); }
    internal static int Run(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("Usage: --layout <real-query.xml> <local-ObjectConfig.xml> [copy-root]"); return 2; }
        var run = Path.GetFullPath(Path.Combine("artifacts/verification/desktop", "layout-local-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(run); ErrorLog.Initialize(Path.Combine(run, "logs"));
        bool reconcile = args[0] == "--layout-reconcile";
        try
        {
            using var reader = XmlReader.Create(args[2], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var config = XDocument.Load(reader);
            var connection = new DbConnectionStringBuilder { ConnectionString = config.Descendants().Attributes().First(a => a.Name.LocalName == "connectionString" || a.Name.LocalName == "connection-string").Value };
            string user = Convert.ToString(connection["User ID"])!, password = Convert.ToString(connection["Password"])!;
            string original = Path.GetFullPath(args[1]), originalLayout = original.Replace("查询设置.xml", "报表设置.xml");
            var originalBytes = File.ReadAllBytes(original); var originalLayoutBytes = File.ReadAllBytes(originalLayout);
            var source = args.Length > 3 ? Path.Combine(Path.GetFullPath(args[3]), Path.GetFileName(run)) : Path.Combine(run, "source");
            Directory.CreateDirectory(source);
            string file = Path.Combine(source, Path.GetFileName(original)), layout = Path.Combine(source, Path.GetFileName(originalLayout));
            File.WriteAllBytes(file, originalBytes); File.WriteAllBytes(layout, originalLayoutBytes);
            var queryTimestamp = File.GetLastWriteTimeUtc(file);
            using var service = new Service(Path.Combine(run, "data"), run, false);
            Call(service, "testConnection", Args("host", "127.0.0.1", "port", 1521, "service", "ORCL", "username", user, "password", password, "remember", false));
            Call(service, "import", Args("path", file));
            var report = ReportImporter.ImportFile(file)!; var opened = Call(service, "sqlEditorOpen", Args("reportId", report.Id));
            var snap = SqlXmlEditor.Read(file); var target = snap.Sources.First(s => s.Editable);
            var sql = reconcile ? target.Sql : "select q.*, 'layout-check' AS \"新增字段\" from (\n" + target.Sql + "\n) q where 1 = 0";
            var values = new Dictionary<string, object>();
            foreach (var p in report.Parameters) { Assert(p.Kind == "DateTimeType", "Expected date-only test fixture"); values[p.Name] = "2026-01-01T00:00:00"; }
            var payload = Args("reportId", report.Id, "token", opened["token"], "sourceIndex", target.Index, "sql", sql, "values", values, "path", "C:/never-use.xml");
            var preview = Call(service, "layoutPreview", payload);
            Assert(File.ReadAllBytes(file).SequenceEqual(originalBytes) && File.ReadAllBytes(layout).SequenceEqual(originalLayoutBytes), "Preview wrote files");
            var columns = ((IEnumerable)preview["columns"]).Cast<Dictionary<string, object>>().ToArray();
            var last = columns.Length - 1;
            Assert((bool)columns[last]["added"] && (string)columns[last]["header"] == (reconcile ? "申请ID" : "新增字段"), "Unexpected column description");
            if (reconcile) Assert((bool)preview["reconciled"] && columns.Length == 6 && columns.Count(c => (bool)c["added"]) == 1, "Expected saved SQL 6/template 5 recovery");
            Console.WriteLine("PASS real Oracle schema-only preview: " + columns.Length + " fields; saved-SQL recovery=" + preview["reconciled"]);
            var invalid = new Dictionary<string, object>(payload) { ["previewToken"] = "stale", ["columns"] = columns };
            bool rejected = false; try { Call(service, "layoutSave", invalid); } catch (InvalidOperationException) { rejected = true; } Assert(rejected, "Stale preview accepted");
            columns[last]["width"] = 180;
            payload["columns"] = columns; payload["previewToken"] = preview["previewToken"];
            var saved = Call(service, "layoutSave", payload);
            Assert((bool)saved["saved"] && (bool)saved["reloaded"], "Pair not saved/reloaded");
            Assert(SqlXmlEditor.Read(file).Sources[target.Index].Sql == sql, "SQL mismatch");
            var doc = XDocument.Load(layout);
            Assert(doc.Descendants("ColumnCount").First().Value == columns.Length.ToString(), "Layout width not increased");
            Assert(doc.Descendants("Cell").Single(c => (string?)c.Attribute("row") == "1" && (string?)c.Attribute("column") == last.ToString()).Element("Data")!.Value == (string)columns[last]["header"], "Header mismatch");
            if (reconcile)
            {
                Assert(File.ReadAllBytes(file).SequenceEqual(originalBytes) && File.GetLastWriteTimeUtc(file) == queryTimestamp, "Unchanged SQL was rewritten");
                var reopened = Call(service, "sqlEditorOpen", Args("reportId", report.Id));
                var again = Call(service, "layoutPreview", Args("reportId", report.Id, "token", reopened["token"], "sourceIndex", target.Index, "sql", sql, "values", values));
                Assert(!(bool)again["reconciled"] && ((IEnumerable)again["columns"]).Cast<Dictionary<string, object>>().All(c => !(bool)c["added"]), "Reopen adds duplicate columns");
            }
            else
            {
            var result = Call(service, "query", Args("reportId", report.Id, "source", target.QueryIndex, "values", values));
            // HIS IsSumRow adds a generated total even when Oracle returned no detail rows.
            Assert(Convert.ToInt32(result["total"]) <= 1, "Unexpected detail rows");
            var rawResult = OracleQueryService.Execute(new ConnectionSettings { Mode = ConnectionMode.Direct, Host = "127.0.0.1", Port = 1521, Service = "ORCL", Username = user }, password, sql,
                SqlTemplate.Compile(sql).RequiredNames.ToDictionary(n => n, _ => "2026-01-01 00:00:00"), CancellationToken.None);
            Assert(rawResult.Table.Rows.Count == 0 && rawResult.Table.Columns.Count == columns.Length, "Raw zero-row verification failed");
            rawResult.Table.Dispose();
            }
            Assert(Directory.GetFiles(source).Length == 2, "Unexpected backup or temp");
            Assert(File.ReadAllBytes(original).SequenceEqual(originalBytes) && File.ReadAllBytes(originalLayout).SequenceEqual(originalLayoutBytes), "Original library modified");
            File.WriteAllText(Path.Combine(run, "PASS.txt"), "PASS local 127.0.0.1:1521/ORCL schema-only metadata; real observation-room copies; alias/header/width; preview read-only; stale preview rejected; readback/reload; " +
                (reconcile ? "saved SQL 6/template 5 recovery, SQL bytes and timestamp unchanged, reopen no duplicate columns; " : "raw zero-row query; ") + "originals unchanged; no backup, patient rows or saved password. HIS client/printing NOT RUN.\n");
            Console.WriteLine("PASS " + (reconcile ? "saved SQL preserved, template recovered" : "raw Oracle returns zero rows (Host may add a total)") + "; original library unchanged\n" + run); return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().Name);
            if (ex is CheckFailure) Console.WriteLine(ex.Message);
            if (ex is Oracle.ManagedDataAccess.Client.OracleException oracle) Console.WriteLine("Oracle code: " + oracle.Number);
            Console.WriteLine(ex.StackTrace); return 1;
        }
    }
}
