#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using ReportDesk.Core;

namespace ReportDesk.Web.Sync
{
    public sealed class ReportFileTransaction
    {
        private readonly ReportWorkDirectory work;
        private string JournalPath { get { return Path.Combine(work.StateRoot, "pending.xml"); } }
        public bool RequiresRecovery { get { return File.Exists(JournalPath); } }
        public ReportFileTransaction(ReportWorkDirectory workDirectory) { work = workDirectory; }

        // Paths must enumerate ALL files that mutation may create/replace. No deletion or directory moves.
        // callbacks execute under the lease and MUST NOT acquire it again.
        public void ExecuteProtectedEdit(IEnumerable<string> relativePaths, Action mutation, Action reload,
            CancellationToken cancellation = default(CancellationToken))
        {
            using (work.Acquire(cancellation))
            {
                RecoverUnderLock(reload);
                ExecuteUnderLock(relativePaths, mutation, reload);
            }
        }

        // Single XML edit: record the expected written bytes before mutation. If an
        // external program changes the target to any other bytes, never restore
        // over them; preserve the journal for manual reconciliation.
        public void ExecuteProtectedXmlEdit(string relativePath, string expectedNewHash, Action mutation, Action reload,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(expectedNewHash) || expectedNewHash.Length != 64 || expectedNewHash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidOperationException("XML 保存预期哈希无效。");
            using (work.Acquire(cancellation))
            {
                RecoverUnderLock(reload);
                ExecuteUnderLock(new[] { relativePath }, mutation, reload, expectedNewHash: expectedNewHash);
            }
        }

        public void Recover(Action reload, CancellationToken cancellation = default(CancellationToken))
        { using (work.Acquire(cancellation)) RecoverUnderLock(reload); }

        // Startup-only: restore disk before policy scanning or WebBackend/catalog construction.
        // While serving requests use Recover(Action) so restored definitions are also reloaded under the lease.
        public void RecoverFiles(CancellationToken cancellation = default(CancellationToken))
        { Recover(() => { }, cancellation); }

        internal void ExecuteUnderLock(IEnumerable<string> relativePaths, Action mutation, Action reload, Action precondition = null, string expectedNewHash = null)
        {
            if (RequiresRecovery) throw new SyncSafetyException("RecoveryRequired");
            var paths = relativePaths.Select(ReportWorkDirectory.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) throw new SyncSafetyException("EmptyTransaction");
            var journal = new FileTransactionJournal { Id = Guid.NewGuid().ToString("N") };
            if (expectedNewHash != null && paths.Length != 1) throw new InvalidOperationException("XML 编辑只能包含一个目标文件。");
            string backupRoot = Path.Combine(work.StateRoot, journal.Id);
            ReportWorkDirectory.CheckLinks(backupRoot); Directory.CreateDirectory(backupRoot);
            foreach (var path in paths)
            {
                string full = work.Resolve(path);
                var item = new FileTransactionEntry { Path = path, Existed = File.Exists(full), Backup = journal.Entries.Count.ToString() + ".bin", ExpectedNewHash = expectedNewHash };
                if (expectedNewHash != null && !item.Existed) throw new InvalidOperationException("XML 目标文件已不存在。");
                if (item.Existed)
                {
                    var bytes = File.ReadAllBytes(full); item.Hash = ReportWorkDirectory.Hash(bytes);
                    ReportWorkDirectory.WriteDurable(Path.Combine(backupRoot, item.Backup), bytes);
                }
                journal.Entries.Add(item);
            }
            // A staging-time conflict must not trigger rollback over the newly detected external edit.
            // The precondition runs before the marker, so a failure leaves destination bytes untouched.
            if (precondition != null) precondition();
            XmlDisk.Save(JournalPath, journal); // Durable write-ahead marker precedes every report mutation.
            try
            {
                mutation(); reload();
                journal.Committed = true; XmlDisk.Save(JournalPath, journal);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("ReportTransactionEdit", ex, includeMessage: false);
                try { RecoverUnderLock(reload); }
                catch { throw new SyncSafetyException("RecoveryRequired"); }
                throw new SyncSafetyException("TransactionRolledBack");
            }
            Cleanup(journal);
        }

        internal void RecoverUnderLock(Action reload)
        {
            if (!RequiresRecovery) return;
            FileTransactionJournal journal;
            try
            {
                journal = XmlDisk.Load<FileTransactionJournal>(JournalPath);
                Guid id;
                if (!Guid.TryParseExact(journal.Id, "N", out id) || journal.Entries.Count == 0 ||
                    journal.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Count)
                    throw new SyncSafetyException("RecoveryRequired");
                // Validate the whole recovery set before touching any target.
                foreach (var e in journal.Entries)
                {
                    work.Resolve(e.Path);
                    if (e.Backup != journal.Entries.IndexOf(e).ToString() + ".bin") throw new SyncSafetyException("RecoveryRequired");
                    if (!journal.Committed && e.Existed && ReportWorkDirectory.HashFile(BackupPath(journal, e)) != e.Hash)
                        throw new SyncSafetyException("RecoveryRequired");
                    if (!journal.Committed && !string.IsNullOrEmpty(e.ExpectedNewHash))
                    {
                        // Validate the complete recovery set before touching any
                        // target. Missing files or unknown bytes may be an external edit.
                        if (!e.Existed || e.ExpectedNewHash.Length != 64 || e.ExpectedNewHash.Any(c => !Uri.IsHexDigit(c)))
                            throw new SyncSafetyException("RecoveryRequired");
                        var currentHash = ReportWorkDirectory.HashFile(work.Resolve(e.Path));
                        if (!string.Equals(currentHash, e.Hash, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(currentHash, e.ExpectedNewHash, StringComparison.OrdinalIgnoreCase))
                            throw new SyncSafetyException("RecoveryRequired");
                    }
                }
                if (!journal.Committed)
                    foreach (var e in journal.Entries)
                    {
                        string full = work.Resolve(e.Path);
                        if (e.Existed)
                        {
                            // A denied save may not have changed the original at all. Replacing
                            // those bytes again can fail for the same reason and poison startup.
                            // All backups were validated above; only changed/missing targets need writes.
                            if (ReportWorkDirectory.HashFile(full) == e.Hash) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            ReportWorkDirectory.AtomicWrite(full, File.ReadAllBytes(BackupPath(journal, e)));
                        }
                        else if (File.Exists(full)) File.Delete(full);
                    }
                reload(); // If this fails leave the marker and refuse to open definitions.
                Cleanup(journal);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("ReportTransactionRecovery", ex, includeMessage: false);
                throw new SyncSafetyException("RecoveryRequired");
            }
        }

        private string BackupPath(FileTransactionJournal journal, FileTransactionEntry entry)
        {
            string full = Path.Combine(work.StateRoot, journal.Id, entry.Backup);
            ReportWorkDirectory.CheckLinks(full); return full;
        }
        private void Cleanup(FileTransactionJournal journal)
        {
            // Drop marker only after a successful rollback/commit + reload. Leftover backup cleanup is harmless.
            File.Delete(JournalPath);
            foreach (var e in journal.Entries)
            {
                var path = BackupPath(journal, e);
                if (File.Exists(path)) File.Delete(path);
            }
            var directory = Path.Combine(work.StateRoot, journal.Id);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    // Serializer DTOs are public for XmlSerializer on net48; not HTTP response DTOs.
    public sealed class FileTransactionJournal
    {
        public string Id { get; set; } = "";
        public bool Committed { get; set; }
        public List<FileTransactionEntry> Entries { get; set; } = new List<FileTransactionEntry>();
    }
    public sealed class FileTransactionEntry
    {
        public string Path { get; set; } = "";
        public string Backup { get; set; } = "";
        public bool Existed { get; set; }
        public string Hash { get; set; } = "";
        public string ExpectedNewHash { get; set; } = "";
    }
    internal static class XmlDisk
    {
        internal static void Save<T>(string path, T value)
        {
            using (var buffer = new MemoryStream())
            {
                new XmlSerializer(typeof(T)).Serialize(buffer, value);
                ReportWorkDirectory.AtomicWrite(path, buffer.ToArray());
            }
        }
        internal static T Load<T>(string path)
        {
            ReportWorkDirectory.CheckLinks(path);
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 }))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
    }
}
