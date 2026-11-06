using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ReportDesk.Core;

internal static class Program
{
    private static int failures;
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected rejection."); }
    private static void Run(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); } }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "reportdesk-fullxml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true), Encoding.GetEncoding(936) })
            {
                var enc = encoding;
                Run("preserve encoding and BOM " + enc.WebName + "/" + enc.GetPreamble().Length, () => {
                    string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".xml");
                    string xml = "<?xml version=\"1.0\" encoding=\"" + enc.WebName + "\"?><ReportQueryInfo><Data>原文</Data></ReportQueryInfo>";
                    File.WriteAllBytes(path, enc.GetPreamble().Concat(enc.GetBytes(xml)).ToArray());
                    var original = FullXmlEditor.Read(path, "ReportQueryInfo");
                    var result = FullXmlEditor.Save(original, original.Text.Replace("原文", "更新"));
                    Assert(result.Changed && result.Snapshot.Text.Contains("更新"));
                    Assert(File.ReadAllBytes(path).SequenceEqual(enc.GetPreamble().Concat(enc.GetBytes(xml.Replace("原文", "更新")))));
                    Assert(!FullXmlEditor.Save(result.Snapshot, result.Snapshot.Text).Changed);
                });
            }
            Run("refuse malformed, wrong root, DTD and changed encoding", () => {
                string path = Path.Combine(root, "invalid.xml");
                File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ReportQueryInfo><Data>原</Data></ReportQueryInfo>", new UTF8Encoding(false));
                var snap = FullXmlEditor.Read(path, "ReportQueryInfo"); var bytes = File.ReadAllBytes(path);
                Throws(() => FullXmlEditor.Save(snap, "<ReportQueryInfo>"));
                Throws(() => FullXmlEditor.Save(snap, "<Spread/>"));
                Throws(() => FullXmlEditor.Save(snap, "<!DOCTYPE ReportQueryInfo [<!ENTITY x SYSTEM 'file:///secret'>]><ReportQueryInfo>&x;</ReportQueryInfo>"));
                Throws(() => FullXmlEditor.Save(snap, snap.Text.Replace("utf-8", "utf-16")));
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
            });
            Run("reject concurrent change and cancellation", () => {
                string path = Path.Combine(root, "conflict.xml");
                File.WriteAllText(path, "<ReportQueryInfo/>"); var snap = FullXmlEditor.Read(path, "ReportQueryInfo");
                File.WriteAllText(path, "<ReportQueryInfo><Other/></ReportQueryInfo>"); var bytes = File.ReadAllBytes(path);
                Throws(() => FullXmlEditor.Save(snap, "<ReportQueryInfo><New/></ReportQueryInfo>"));
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
                var fresh = FullXmlEditor.Read(path, "ReportQueryInfo"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
                Throws(() => FullXmlEditor.Save(fresh, "<ReportQueryInfo><New/></ReportQueryInfo>", cancel.Token));
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
            });
            Run("browser line normalization and mixed style refusal", () => {
                string path = Path.Combine(root, "newlines.xml");
                string xml = "<ReportQueryInfo>\r\n<Data>旧</Data>\r\n</ReportQueryInfo>";
                File.WriteAllText(path, xml, new UTF8Encoding(false));
                var snap = FullXmlEditor.Read(path, "ReportQueryInfo");
                FullXmlEditor.Validate(snap, snap.Text.Replace("\r\n", "\n").Replace("旧", "新"));
                Assert(File.ReadAllText(path) == xml);
                var saved = FullXmlEditor.Save(snap, snap.Text.Replace("\r\n", "\n").Replace("旧", "新"));
                Assert(saved.Snapshot.Text == xml.Replace("旧", "新"));
                File.WriteAllText(path, "<ReportQueryInfo>\r\n<Data/>\n</ReportQueryInfo>", new UTF8Encoding(false));
                var mixed = FullXmlEditor.Read(path, "ReportQueryInfo"); var bytes = File.ReadAllBytes(path);
                Throws(() => FullXmlEditor.Save(mixed, mixed.Text.Replace("Data", "Other")));
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
            });
            Run("real saved report template copy", () => {
                string source = Path.GetFullPath("artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml/抗菌药物消耗情况报表设置.xml");
                if (!File.Exists(source)) { Console.WriteLine("SKIP retained report not available"); return; }
                string path = Path.Combine(root, "layout.xml"); File.Copy(source, path);
                var oldHash = FullXmlEditor.Read(source, "Spread").Hash;
                var snap = FullXmlEditor.Read(path, "Spread");
                Assert(snap.Text.Contains("药物名称"));
                var result = FullXmlEditor.Save(snap, snap.Text.Replace("药物名称", "药品名称（测试）"));
                Assert(result.Changed && result.Snapshot.Text.Contains("药品名称（测试）"));
                Assert(FullXmlEditor.Read(source, "Spread").Hash == oldHash);
            });
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine(failures == 0 ? "All full XML checks passed." : failures + " check(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
