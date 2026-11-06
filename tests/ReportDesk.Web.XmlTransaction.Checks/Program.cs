using System;
using System.IO;
using System.Text;
using ReportDesk.Web.Sync;

internal static class Program
{
    static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReportDeskXmlTransaction-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "files"); var state = Path.Combine(root, "state"); Directory.CreateDirectory(files);
        var path = Path.Combine(files, "test.xml");
        try
        {
            var work = new ReportWorkDirectory(files, state); var tx = new ReportFileTransaction(work);
            string Hash(string value) => ReportWorkDirectory.Hash(Encoding.UTF8.GetBytes(value));
            void Expect(bool ok) { if (!ok) throw new Exception("XML transaction assertion failed"); }
            void Reject(Action action, string code) { try { action(); } catch (SyncSafetyException e) { Expect(e.Code == code); return; } throw new Exception("Expected " + code); }
            File.WriteAllText(path, "original");
            tx.ExecuteProtectedXmlEdit("test.xml", Hash("new"), () => File.WriteAllText(path, "new"), () => { });
            Expect(File.ReadAllText(path) == "new" && !tx.RequiresRecovery);
            File.WriteAllText(path, "original");
            Reject(() => tx.ExecuteProtectedXmlEdit("test.xml", Hash("new"), () => { File.WriteAllText(path, "new"); throw new Exception(); }, () => { }), "TransactionRolledBack");
            Expect(File.ReadAllText(path) == "original" && !tx.RequiresRecovery);
            Reject(() => tx.ExecuteProtectedXmlEdit("test.xml", Hash("new"), () => { File.WriteAllText(path, "external-before"); throw new Exception(); }, () => { }), "RecoveryRequired");
            Expect(File.ReadAllText(path) == "external-before" && tx.RequiresRecovery);
            Reject(() => tx.Recover(() => { }), "RecoveryRequired"); Expect(File.ReadAllText(path) == "external-before");
            // Simulate manual reconciliation by restoring recorded original bytes.
            File.WriteAllText(path, "original"); tx.Recover(() => { }); Expect(!tx.RequiresRecovery);
            Reject(() => tx.ExecuteProtectedXmlEdit("test.xml", Hash("new"), () => { File.WriteAllText(path, "new"); }, () => { File.WriteAllText(path, "external-after"); throw new Exception(); }), "RecoveryRequired");
            Expect(File.ReadAllText(path) == "external-after" && tx.RequiresRecovery);
            Reject(() => tx.Recover(() => { }), "RecoveryRequired"); Expect(File.ReadAllText(path) == "external-after");
            File.WriteAllText(path, "original"); tx.Recover(() => { }); Expect(!tx.RequiresRecovery);
            Console.WriteLine("PASS XML transaction: normal commit/rollback, external changes before and after write preserved, retry retains marker; temporary files only.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
