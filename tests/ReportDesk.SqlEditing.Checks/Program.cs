using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ReportDesk.Core;
using ReportDesk.Host;

internal static class SqlEditingChecks
{
    private static int failed;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ReportDesk-SqlEditing-" + Guid.NewGuid().ToString("N"));
    private static string Body => "<ReportQueryInfo extra='keep'><Unknown value='a&gt;b' />\r\n" +
        "<!-- <Sql>not a target</Sql> --><QueryDataSource>" +
        "<QueryDataSource><Name>empty</Name><Sql /><SqlType>MainReportUsing</SqlType></QueryDataSource>" +
        "<QueryDataSource><Name>same</Name><Sql>select 1 as N from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource>" +
        "<QueryDataSource><Name>same</Name><Sql keep='&gt;'><![CDATA[select 2 as N from dual]]></Sql><SqlType>DetailReportUsing</SqlType><Unknown>keep</Unknown></QueryDataSource>" +
        "<QueryDataSource><Name>options</Name><Sql>select 3 as N from dual</Sql><SqlType>ConditionUsing</SqlType></QueryDataSource>" +
        "</QueryDataSource><Other><Sql>do not edit</Sql></Other></ReportQueryInfo>\r\n";
    private static string Fixture(Encoding encoding) => "<?xml version=\"1.0\" encoding=\"" + encoding.WebName + "\"?>\r\n" + Body;
    private static byte[] Encode(string value, Encoding encoding) => encoding.GetPreamble().Concat(encoding.GetBytes(value)).ToArray();
    private static string Create(Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false, true);
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "样例查询设置.xml"); File.WriteAllBytes(file, Encode(Fixture(encoding), encoding)); return file;
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Throws(Action action)
    {
        bool caught = false;
        try { action(); } catch (Exception) { caught = true; }
        Assert(caught);
    }
    private static void Check(string title, Action action)
    {
        try { action(); Console.WriteLine("PASS " + title); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + title + " (" + ex.GetType().Name + ")"); }
    }
    private static Dictionary<string, object> Args(params object[] values)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < values.Length; i += 2) result[(string)values[i]] = values[i + 1];
        return result;
    }
    private static Dictionary<string, object> Call(Service service, string method, Dictionary<string, object> args)
    {
        var json = ReportDesk.Host.Program.Json();
        return (Dictionary<string, object>)json.DeserializeObject(json.Serialize(service.Handle(method, args, CancellationToken.None, _ => { })));
    }
    private static int Main()
    {
        Directory.CreateDirectory(Root); ErrorLog.Initialize(Path.Combine(Root, "logs"));
        try
        {
            foreach (var encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true),
                new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), Encoding.GetEncoding(936) })
            {
                Check("surgical write, backup and encoding: " + encoding.WebName + "/" + encoding.GetPreamble().Length, () => {
                    var file = Create(encoding); var before = File.ReadAllBytes(file); var snapshot = SqlXmlEditor.Read(file);
                    Assert(snapshot.Sources[0].QueryIndex == -1 && snapshot.Sources[1].QueryIndex == 0 && snapshot.Sources[2].QueryIndex == 1);
                    const string sql = "select '科室<&' as NAME, 4 as N from dual\nwhere 1 < 2";
                    var saved = SqlXmlEditor.Save(snapshot, 2, sql);
                    Assert(saved.Changed && File.ReadAllBytes(saved.BackupPath).SequenceEqual(before));
                    Assert(File.ReadAllBytes(file).SequenceEqual(Encode(Fixture(encoding).Replace("select 2 as N from dual", sql), encoding)));
                    Assert(SqlXmlEditor.Read(file).Sources[2].Sql == sql && SqlXmlEditor.Read(file).Sources[1].Sql == snapshot.Sources[1].Sql);
                    Assert(ReportImporter.ImportFolder(Path.GetDirectoryName(file)!).Reports.Count == 1);
                    Assert(!Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Any());
                });
            }
            Check("CDATA terminator is safely split", () => {
                var file = Create(); const string sql = "select ']]>' as X from dual";
                SqlXmlEditor.Save(SqlXmlEditor.Read(file), 2, sql); Assert(SqlXmlEditor.Read(file).Sources[2].Sql == sql);
            });
            Check("plain text escaping and CR character round trip", () => {
                var file = Create(); const string sql = "select '<&>' as X from dual\r\nwhere 1 < 2";
                SqlXmlEditor.Save(SqlXmlEditor.Read(file), 1, sql); Assert(SqlXmlEditor.Read(file).Sources[1].Sql == sql);
            });
            Check("no-op does not rewrite or create backup", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); var saved = SqlXmlEditor.Save(s, 1, s.Sources[1].Sql);
                Assert(!saved.Changed && saved.BackupPath == "" && File.ReadAllBytes(file).SequenceEqual(Encode(Fixture(new UTF8Encoding(false)), new UTF8Encoding(false))));
            });
            Check("external edits are not overwritten", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); File.AppendAllText(file, " "); var external = File.ReadAllBytes(file);
                Throws(() => SqlXmlEditor.Save(s, 1, "select 8 from dual")); Assert(File.ReadAllBytes(file).SequenceEqual(external));
                Assert(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.bak").Length == 0);
            });
            Check("cancellation before commit leaves original intact", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); var before = File.ReadAllBytes(file);
                using var cts = new CancellationTokenSource(); cts.Cancel();
                Throws(() => SqlXmlEditor.Save(s, 1, "select 8 from dual", cts.Token)); Assert(File.ReadAllBytes(file).SequenceEqual(before));
            });
            Check("invalid input, empty and readonly sources cannot save", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); var before = File.ReadAllBytes(file);
                foreach (var sql in new[] { "", " \n", "select '\u0001' from dual", new string('x', SqlXmlEditor.MaxSqlCharacters + 1) })
                    Throws(() => SqlXmlEditor.Save(s, 1, sql));
                foreach (var index in new[] { -1, 0, 3, 9 }) Throws(() => SqlXmlEditor.Save(s, index, "select 8 from dual"));
                Assert(File.ReadAllBytes(file).SequenceEqual(before));
            });
            Check("DTD and malformed XML rejected", () => {
                foreach (var xml in new[] { "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///never-read'>]>" + Body, "<ReportQueryInfo>" })
                { var file = Create(); File.WriteAllText(file, xml); Throws(() => SqlXmlEditor.Read(file)); }
            });
            Check("unencodable text never becomes question marks", () => {
                var file = Create(Encoding.GetEncoding(936)); var s = SqlXmlEditor.Read(file); var before = File.ReadAllBytes(file);
                Throws(() => SqlXmlEditor.Save(s, 1, "select '😀' from dual")); Assert(File.ReadAllBytes(file).SequenceEqual(before));
            });
            Check("exclusive file lock prevents write", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); var before = File.ReadAllBytes(file);
                using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws(() => SqlXmlEditor.Save(s, 1, "select 8 from dual"));
                Assert(File.ReadAllBytes(file).SequenceEqual(before));
            });
            Check("readonly original is not overwritten", () => {
                var file = Create(); var s = SqlXmlEditor.Read(file); var before = File.ReadAllBytes(file);
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                try { Throws(() => SqlXmlEditor.Save(s, 1, "select 8 from dual")); Assert(File.ReadAllBytes(file).SequenceEqual(before)); }
                finally { File.SetAttributes(file, FileAttributes.Normal); }
            });
            Check("save is separate from SQL execution validation", () => {
                var file = Create(); const string sql = "select q'[sample]' as X from dual";
                SqlXmlEditor.Save(SqlXmlEditor.Read(file), 1, sql); Assert(SqlXmlEditor.Read(file).Sources[1].Sql == sql);
            });
            Check("Host saves and reloads only one report, invalidates results and rejects stale token", () => {
                var file = Create(); var directory = Path.GetDirectoryName(file)!;
                var other = Path.Combine(directory, "other.xml"); File.WriteAllText(other, Body);
                var id = ReportImporter.ImportFile(file)!.Id; var otherId = ReportImporter.ImportFile(other)!.Id;
                Directory.CreateDirectory(Path.Combine(Root, "config"));
                using var service = new Service(Path.Combine(Root, "data"), Path.Combine(Root, "config"), true);
                Call(service, "import", Args("path", directory, "folder", true));
                var editor = Call(service, "sqlEditorOpen", Args("reportId", id));
                File.WriteAllText(other, Body.Replace("select 1 as N", "select 99 as N"));
                typeof(Service).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, new QueryResult());
                typeof(Service).GetField("resultId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, "stale-result");
                var payload = Args("reportId", id, "token", editor["token"], "sourceIndex", 1, "sql", "select 5 as N from dual", "path", other);
                var saved = Call(service, "sqlEditorSave", payload);
                Assert((bool)saved["saved"] && (bool)saved["reloaded"]);
                Assert(((string)Call(service, "definition", Args("reportId", id))["text"]).Contains("select 5 as N"));
                Assert(!((string)Call(service, "definition", Args("reportId", otherId))["text"]).Contains("select 99 as N"));
                Throws(() => Call(service, "page", Args("resultId", "stale-result")));
                Throws(() => Call(service, "sqlEditorSave", payload));
                var latest = Call(service, "sqlEditorOpen", Args("reportId", id));
                var checkedSql = Call(service, "sqlEditorCheck", Args("reportId", id, "token", latest["token"], "sourceIndex", 1, "sql", "select '&new_value' from dual"));
                Assert((bool)checkedSql["passed"]);
                File.WriteAllText(file, "<broken>");
                Throws(() => Call(service, "reloadReport", Args("reportId", id)));
                Throws(() => Call(service, "query", Args("reportId", id, "source", 0)));
                File.WriteAllText(file, Body);
                Call(service, "reloadReport", Args("reportId", id));
                var ready = Call(service, "select", Args("reportId", id)); Assert(((object[])ready["selectedIssues"]).Length == 0);
            });
            Console.WriteLine(failed == 0 ? "All SQL editing checks passed (no Oracle connection)." : failed + " check(s) failed.");
            return failed == 0 ? 0 : 1;
        }
        finally { try { Directory.Delete(Root, true); } catch { } }
    }
}
