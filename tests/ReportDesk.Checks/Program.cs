using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using ReportDesk.Core;

internal static class Program
{
    private static int count;
    private static int Main(string[] args)
    {
        var folder = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "ReportDesk-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ErrorLog.Initialize(Path.Combine(folder, "logs-" + Guid.NewGuid().ToString("N")));
        try
        {
            ReportVisibilityChecks.Run(folder, Check);
            Check("quoted, repeated and partial placeholders bind safely", () =>
            {
                var attack = "x' OR 1=1 --";
                var b = SqlTemplate.Compile("select '&id', '%&id%', 'O''Brien' from dual where x='&id'", new Dictionary<string, string> { ["id"] = attack });
                Assert(!b.Sql.Contains(attack) && b.Values.Count == 1 && b.Values["p0"] == attack && b.Sql.Contains("'%' || :p0 || '%'") && b.Sql.Contains("'O''Brien'"));
            });
            Check("SQL comments do not become input parameters", () => Assert(SqlTemplate.Compile("-- &notInput\n select /* &notInput */ '&yes' from dual").RequiredNames.SequenceEqual(new[] { "yes" })));
            Check("existing Oracle time format remains unchanged", () => Assert(SqlTemplate.Compile("select to_date('&time','yyyy-mm-dd hh24:mi:ss') from dual").Sql.Contains("to_date(:p0,'yyyy-mm-dd hh24:mi:ss')")));
            foreach (var sql in new[] { "delete from a", "select 1 from a; delete from a", "select 1 from a for update", "with function x return number is begin return 1; end; select x from dual", "select a.nextval from dual", "select * from &table", "select * from a@remote", "select q'[&x]' from dual", "select :unknown from dual", "select 'unterminated from dual", "select * from a where b in ('18'）" })
                Check("reject unsupported/write syntax: " + sql, () => Throws(() => SqlTemplate.Compile(sql)));
            Check("missing parameter is an error", () => Throws(() => SqlTemplate.Compile("select '&id' from dual", new Dictionary<string, string>())));
            Check("case-insensitive repeat uses one bind", () => Assert(SqlTemplate.Compile("select '&ID', '&id' from dual").RequiredNames.Count == 1));
            Check("with select supported", () => Assert(SqlTemplate.Compile("with x as (select 1 a from dual) select a from x").Sql.StartsWith("with")));
            Check("padding and LIKE preserve configured behavior", () => Assert(SqlTemplate.Transform(new ParameterDefinition { PadLeft = true, PadLength = 5, PadCharacter = "0", IsLike = true }, "12") == "%00012%"));
            var xml = "<?xml version=\"1.0\" encoding=\"gb2312\"?><ReportQueryInfo><List><List><Name>when</Name><Text>开始日期</Text><ControlType Type=\"FS.Core.UI.Report.Common.ControlType.DateTimeType,FS.Core.UI\"><CustomFormat>yyyy-MM-dd 00:00:00</CustomFormat></ControlType></List></List><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select '&amp;when' 日期, '&amp;CurrentDeptID' 科室 from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>";
            var sampleA = Path.Combine(folder, "a", "同名查询设置.xml"); var sampleB = Path.Combine(folder, "b", "同名查询设置.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(sampleA)!); Directory.CreateDirectory(Path.GetDirectoryName(sampleB)!);
            File.WriteAllText(sampleA, xml, Encoding.GetEncoding("gb2312")); File.WriteAllText(sampleB, xml, Encoding.GetEncoding("gb2312"));
            ReportDefinition? imported = null;
            Check("XML declaration encoding and implicit context preserved", () =>
            { imported = ReportImporter.ImportFile(sampleA); Assert(imported != null && imported.Parameters[0].Label == "开始日期" && imported.Parameters.Any(p => p.Name == "CurrentDeptID" && p.Implicit)); });
            Check("same name different source is not merged", () => Assert(imported!.Id != ReportImporter.ImportFile(sampleB)!.Id));
            Check("external XML entity rejected", () =>
            {
                var file = Path.Combine(folder, "xxe.xml"); File.WriteAllText(file, "<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///never-read'>]><ReportQueryInfo>&x;</ReportQueryInfo>"); Throws(() => ReportImporter.ImportFile(file));
            });
            Check("unsupported control blocks execution eligibility", () =>
            {
                var file = Path.Combine(folder, "unsupported.xml"); File.WriteAllText(file, xml.Replace("gb2312", "utf-8").Replace("DateTimeType", "CustomControl"), new UTF8Encoding(false)); Assert(ReportImporter.ImportFile(file)!.Issues.Count > 0);
            });
            var catalog = new Catalog(); var summary = new ImportSummary(); summary.Reports.Add(imported!); ReportImporter.Merge(catalog, summary);
            catalog.Reports[0].Favorite = true; catalog.Reports[0].Verified = true; catalog.Reports[0].Aliases = "我的别名";
            Check("refresh keeps annotations, hash change resets verification", () =>
            {
                var next = new ImportSummary(); var item = ReportImporter.ImportFile(sampleA)!; item.SourceHash = "changed"; next.Reports.Add(item); ReportImporter.Merge(catalog, next);
                Assert(catalog.Reports.Count == 1 && catalog.Reports[0].Favorite && !catalog.Reports[0].Verified && catalog.Reports[0].Aliases == "我的别名");
            });
            Check("catalog round trip and atomic backup", () =>
            {
                var store = new CatalogStore(Path.Combine(folder, "store")); store.Save(catalog); store.Save(catalog); var read = store.Load(); Assert(read.Reports.Count == 1 && read.Reports[0].Aliases == "我的别名" && File.Exists(store.FilePath + ".bak"));
            });
            Check("corrupt catalog is not silently replaced", () =>
            {
                var store = new CatalogStore(Path.Combine(folder, "corrupt")); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!); File.WriteAllText(store.FilePath, "broken"); Throws(() => store.Load()); Assert(File.ReadAllText(store.FilePath) == "broken");
            });
            Check("DPAPI round trip hides plaintext", () => { var protectedText = CatalogStore.Protect("test-password-only"); Assert(!protectedText.Contains("test-password-only") && CatalogStore.Unprotect(protectedText) == "test-password-only"); });
            Check("connection builder handles punctuation in password", () =>
            {
                var connection = OracleQueryService.ConnectionString(new ConnectionSettings { Host = "example.local", Service = "service", Username = "readuser" }, "x;\"y");
                var builder = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(connection); Assert(builder.Password == "x;\"y" && !builder.Pooling);
            });
            Check("connect descriptor injection is rejected", () => Throws(() => OracleQueryService.ConnectionString(new ConnectionSettings { Host = "bad)(ADDRESS=", Service = "service", Username = "readuser" }, "")));
            var descriptor = "(DESCRIPTION=(CONNECT_TIMEOUT=4)(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=first.invalid)(PORT=1521))(ADDRESS=(PROTOCOL=TCP)(HOST=second.invalid)(PORT=1522)))(CONNECT_DATA=(SID=legacy)))";
            var tnsFile = Path.Combine(folder, "tnsnames.ora");
            File.WriteAllText(tnsFile, "# 中文说明\nPRIMARY, SECOND.example =\n" + descriptor + "\n", Encoding.UTF8);
            var tnsSettings = new ConnectionSettings { Mode = ConnectionMode.Tns, TnsFile = tnsFile, TnsAlias = "primary", Username = "readuser" };
            Check("TNS multiple aliases, comments, SID and address list preserved", () =>
            {
                var entries = TnsNames.Read(tnsFile); Assert(entries.Count == 2 && entries["second.EXAMPLE"] == descriptor);
                var b = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(OracleQueryService.ConnectionString(tnsSettings, "x;\"y"));
                Assert(b.DataSource == descriptor && b.Password == "x;\"y" && !b.Pooling && !b.PersistSecurityInfo);
            });
            Check("TNS quoted punctuation and description lists preserved", () =>
            {
                var value = "(DESCRIPTION_LIST=" + descriptor + "(DESCRIPTION=(CONNECT_DATA=(SERVICE_NAME=\"a#(b)\"))))";
                Assert(TnsNames.Parse("test = " + value)["TEST"] == value);
            });
            Check("TNS malformed and ambiguous definitions fail closed", () =>
            {
                foreach (var bad in new[] { "", "# comments only", "a=" + descriptor + "\nA=" + descriptor, "a=(DESCRIPTION=(X=1)", "a=(DESCRIPTION=(X=\"1))", "a=server/service", "a=(ADDRESS=(HOST=x))", "a=" + descriptor + ")", "a b=" + descriptor, "IFILE=more.ora", "a=" + descriptor + "\nIFILE=more.ora" })
                    Throws(() => TnsNames.Parse(bad));
            });
            Check("TNS missing alias cannot fall back to global config", () => Throws(() => TnsNames.Resolve(tnsFile, "missing")));
            Check("TNS changed file reloads same alias before next query", () =>
            {
                File.WriteAllText(tnsFile, "primary=" + descriptor.Replace("first.invalid", "changed.invalid"));
                var b = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(OracleQueryService.ConnectionString(tnsSettings, ""));
                Assert(b.DataSource.Contains("changed.invalid") && !b.DataSource.Contains("first.invalid"));
            });
            Check("TNS removed alias or deleted file stops connection construction", () =>
            {
                File.WriteAllText(tnsFile, "different=" + descriptor); Throws(() => OracleQueryService.ConnectionString(tnsSettings, ""));
                File.Delete(tnsFile); Throws(() => OracleQueryService.ConnectionString(tnsSettings, ""));
                File.WriteAllText(tnsFile, "primary=" + descriptor);
            });
            Check("TNS validation does not change source file", () =>
            {
                var before = File.ReadAllBytes(tnsFile); OracleQueryService.ConnectionString(tnsSettings, ""); Assert(before.SequenceEqual(File.ReadAllBytes(tnsFile)));
            });
            Check("TNS path and size limits reject invalid input", () =>
            {
                Throws(() => TnsNames.Read("tnsnames.ora")); var huge = Path.Combine(folder, "large.ora");
                File.WriteAllText(huge, new string('x', 1024 * 1024 + 1)); Throws(() => TnsNames.Read(huge));
            });
            Check("TNS settings persist only path and alias, not descriptor or password", () =>
            {
                var store = new CatalogStore(Path.Combine(folder, "tns-catalog")); store.Save(new Catalog { Connection = tnsSettings });
                var loaded = store.Load().Connection;
                Assert(loaded.Mode == ConnectionMode.Tns && loaded.TnsFile == tnsFile && loaded.TnsAlias == "primary");
                Assert(!File.ReadAllText(store.FilePath).Contains("first.invalid"));
                OracleQueryService.ConnectionString(loaded, "");
            });
            Check("legacy catalog without TNS fields remains direct", () =>
            {
                var store = new CatalogStore(Path.Combine(folder, "legacy")); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
                File.WriteAllText(store.FilePath, "{\"Reports\":[],\"Connection\":{\"Host\":\"example.local\",\"Port\":1521,\"Service\":\"original\",\"Username\":\"readuser\",\"ProtectedPassword\":\"\",\"TimeoutSeconds\":60,\"MaxRows\":5000}}");
                var loaded = store.Load().Connection;
                Assert(loaded.Mode == ConnectionMode.Direct && loaded.Host == "example.local"); OracleQueryService.ConnectionString(loaded, "");
            });
            Check("unknown connection mode rejected", () => Throws(() => OracleQueryService.ConnectionString(new ConnectionSettings { Mode = (ConnectionMode)99, Username = "readuser" }, "")));
            Check("TNS discovery limits itself to named files and deduplicates paths", () =>
            {
                var discoveryRoot = Path.Combine(folder, "discovery-" + Guid.NewGuid().ToString("N")); var a = Path.Combine(discoveryRoot, "a"); var b = Path.Combine(discoveryRoot, "b");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                File.WriteAllText(Path.Combine(a, "tnsnames.ora"), "demo=" + descriptor);
                File.WriteAllText(Path.Combine(b, "unrelated.ora"), "do not read");
                var found = TnsDiscovery.FindFiles(new[] { a, a.ToUpperInvariant(), b, "relative", "", Path.Combine(discoveryRoot, "missing") });
                Assert(found.Length == 1 && found[0] == Path.Combine(a, "tnsnames.ora"));
                File.WriteAllText(Path.Combine(b, "tnsnames.ora"), "demo=" + descriptor);
                Assert(TnsDiscovery.FindFiles(new[] { a, b }).Length == 2);
            });
            Check("connection name persists without changing network target", () =>
            {
                tnsSettings.Name = "HIS 报表查询"; var store = new CatalogStore(Path.Combine(folder, "named")); store.Save(new Catalog { Connection = tnsSettings });
                var loaded = store.Load().Connection; Assert(loaded.Name == tnsSettings.Name);
                var builder = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(OracleQueryService.ConnectionString(loaded, ""));
                Assert(builder.DataSource == descriptor && !builder.ConnectionString.Contains(tnsSettings.Name));
            });
            Check("direct connection removes application network deadlines and ignores legacy limits", () =>
            {
                var settings = new ConnectionSettings { Host = "example.invalid", Service = "demo", Username = "readuser", TimeoutSeconds = 60, MaxRows = 5000 };
                var builder = new Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(OracleQueryService.ConnectionString(settings, "synthetic"));
                Assert(builder.ConnectionTimeout == 0 && !builder.Pooling && !builder.DataSource.Contains("CONNECT_TIMEOUT") && !builder.DataSource.Contains("RETRY_COUNT"));
                using var cts = new CancellationTokenSource(); cts.Cancel();
                try { OracleQueryService.Execute(settings, "synthetic", "select 1 from dual", new Dictionary<string, string>(), cts.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
            });
            Check("result reader exceeds former 50000 row and 32 MiB limits", () =>
            {
                using var data = new DataTable(); data.Columns.Add("id", typeof(int)); data.Columns.Add("text");
                var payload = new string('x', 600);
                for (var i = 0; i < 60001; i++) data.Rows.Add(i, payload);
                using var reader = data.CreateDataReader(); var result = OracleQueryService.ReadResult(reader);
                Assert(result.Table.Rows.Count == 60001 && (int)result.Table.Rows[60000][0] == 60000 && !result.Truncated); result.Table.Dispose();
                using var cancelledReader = data.CreateDataReader(); using var cts = new CancellationTokenSource(); cts.Cancel();
                Throws(() => OracleQueryService.ReadResult(cancelledReader, cts.Token));
            });
            Check("error log appends diagnostics and nested stack without secrets", () =>
            {
                Exception error;
                try { throw new InvalidOperationException("outer secret-only patient-only", new IOException("inner failed password=secret-only;Data Source=hidden-host")); }
                catch (Exception ex) { error = ex; }
                var notice = ErrorLog.Write("LogCheck", error, new[] { "secret-only", "patient-only" });
                Assert(notice.StartsWith("错误日志："));
                var file = Directory.GetFiles(ErrorLog.DirectoryPath, "*.log").Single(); var before = File.ReadAllText(file);
                Assert(before.Contains("LogCheck") && before.Contains("System.IO.IOException") && before.Contains("  at ") && !before.Contains("secret-only") && !before.Contains("patient-only") && !before.Contains("hidden-host"));
                ErrorLog.Write("LogCheck", error); Assert(File.ReadAllText(file) == before);
                ErrorLog.Write("NextError", new Exception("another failure")); Assert(File.ReadAllText(file).Length > before.Length);
            });
            Check("log redacts SQL, quoted values and connection descriptors", () =>
            {
                foreach (var message in new[] { "failed SELECT private_value from patient_table", "bad (DESCRIPTION=(HOST=private_value))", "value 'private_value' failed", "token=private_value", "Data Source=private_value" })
                    Assert(!ErrorLog.Sanitize(message).Contains("private_value"));
                var notice = ErrorLog.Write("UnhandledCheck", new Exception("private_value"), includeMessage: false);
                Assert(notice.StartsWith("错误日志：") && !File.ReadAllText(Directory.GetFiles(ErrorLog.DirectoryPath, "*.log").Single()).Contains("private_value"));
            });
            Check("log write failure is reported without replacing original exception", () =>
            {
                var good = ErrorLog.DirectoryPath; var blocked = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blocked, "test fixture");
                try { ErrorLog.Initialize(blocked); Assert(ErrorLog.Write("FailureCheck", new Exception("original")).StartsWith("错误日志写入失败")); }
                finally { ErrorLog.Initialize(good); }
            });
            Check("concurrent errors produce complete log entries", () =>
            {
                System.Threading.Tasks.Parallel.For(0, 12, i => ErrorLog.Write("Parallel" + i + ";", new Exception("synthetic error")));
                var text = File.ReadAllText(Directory.GetFiles(ErrorLog.DirectoryPath, "*.log").Single());
                for (var i = 0; i < 12; i++) Assert(text.Contains("operation=Parallel" + i + ";"));
            });
            Check("demo no longer truncates beyond 1000 rows", () =>
            {
                var result = DemoData.Execute(new Dictionary<string, string> { ["begin"] = "2024-01-01", ["end"] = "2025-12-31", ["department"] = "" });
                Assert(result.Table.Rows.Count == 1462 && !result.Truncated);
            });
            var table = new DataTable(); table.Columns.Add("编号"); table.Columns.Add("金额", typeof(decimal)); table.Columns.Add("文本"); table.Columns.Add("日期", typeof(DateTime)); table.Columns.Add("大数", typeof(decimal));
            table.Rows.Add("00001234", 123.45m, "=HYPERLINK(\"never\")", new DateTime(2026, 1, 2, 3, 4, 5), 1234567890123456789m);
            table.Rows.Add("00000000", DBNull.Value, "中<&文", DBNull.Value, 1.2m);
            var xlsx = Path.Combine(folder, "export.xlsx");
            Check("xlsx package preserves identifiers and rejects formula injection", () =>
            {
                XlsxExporter.Export(table, xlsx, "测试模拟数据");
                using var zip = ZipFile.OpenRead(xlsx); foreach (var e in zip.Entries) { using var s = e.Open(); XDocument.Load(s); }
                using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var cells = doc.Descendants(ns + "c").ToDictionary(x => (string)x.Attribute("r")!);
                Assert((string?)cells["A2"].Attribute("t") == "inlineStr" && cells["A2"].Value == "00001234" && !doc.Descendants(ns + "f").Any());
                Assert((string?)cells["E2"].Attribute("t") == "inlineStr" && cells["B2"].Value == "123.45");
            });
            Check("failed export leaves previous workbook unchanged", () =>
            {
                var before = File.ReadAllBytes(xlsx); table.Rows[0][2] = new string('x', 32768); Throws(() => XlsxExporter.Export(table, xlsx, "bad")); Assert(before.SequenceEqual(File.ReadAllBytes(xlsx))); table.Rows[0][2] = "ok";
            });
            Check("cancelled export leaves no final file", () =>
            { var file = Path.Combine(folder, "cancelled.xlsx"); using var cts = new CancellationTokenSource(); cts.Cancel(); Throws(() => XlsxExporter.Export(table, file, "", cts.Token)); Assert(!File.Exists(file)); });
            Check("demo respects date range and keyword", () =>
            {
                var result = DemoData.Execute(new Dictionary<string, string> { ["begin"] = "2026-01-01", ["end"] = "2026-01-03", ["department"] = " A" }); Assert(result.Demo && result.Table.Rows.Count == 3);
            });
            if (args.Length > 1)
            {
                var scan = ReportImporter.ImportFolder(args[1]);
                var result = $"definitions={scan.Reports.Count}; skipped={scan.Skipped}; errors={scan.Errors.Count}; candidates={scan.Reports.Count(r => r.Issues.Count == 0)}; needsAdaptation={scan.Reports.Count(r => r.Issues.Count > 0)}";
                File.WriteAllText(Path.Combine(folder, "import-scan.txt"), result + "\n" + string.Join("\n", scan.Errors), new UTF8Encoding(false));
                Console.WriteLine(result); Assert(scan.Reports.Count > 0 && scan.Reports.Select(r => r.Id).Distinct().Count() == scan.Reports.Count);
            }
            Console.WriteLine("PASS " + count + " checks; process=" + (Environment.Is64BitProcess ? "x64" : "x86") + "; no database connection attempted"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected exception"); }
}
