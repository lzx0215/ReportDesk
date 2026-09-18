using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ReportDesk.Web.Sync;

internal static class Program
{
    private static int passed;
    private static byte[] query, otherQuery, layout;
    private const string QueryPath = "Config/Xml/1041查询设置.xml";
    private const string LayoutPath = "Config/Xml/1041报表设置.xml";
    private static string testRoot;
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "crash") return Crash(args[1]);
            if (args.Length > 0 && args[0] == "lock") return CheckChildLock(args[1]);
            string lib = args.Length == 0 ? @"D:\系统知识库\00_Inbox\yljhis\LIB\LIB" : args[0];
            // Byte sources are actual existing HIS report definitions, read only. No Oracle rows or query results are fabricated.
            string[] sources = { Path.Combine(lib, QueryPath.Replace('/', '\\')), Path.Combine(lib, LayoutPath.Replace('/', '\\')),
                Path.Combine(lib, @"Xml\供应室按科室汇总回收数量查询报表查询设置.xml") };
            string[] hashes = sources.Select(ReportWorkDirectory.HashFile).ToArray();
            query = File.ReadAllBytes(sources[0]); layout = File.ReadAllBytes(sources[1]); otherQuery = File.ReadAllBytes(sources[2]);
            testRoot = Path.Combine(Path.GetTempPath(), "ReportDeskSyncChecks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            Run("ZIP named and legacy empty entry preserve real LIB bytes", ArchiveRoundtrip);
            Run("ZIP traversal, CRC, truncation, multi-entry and expansion guards", ArchiveGuards);
            Run("path traversal, ADS, device names and root boundaries", Paths);
            Run("disabled and unvalidated reader never request a connection", Disabled);
            Run("same-directory lease excludes another process", CrossProcessLock);
            Run("protected edit rolls back both files and reload failure", Rollback);
            Run("process exit mid-batch is recovered before reuse", CrashRecovery);
            Run("transaction keeps marker if recovery reload fails", RecoveryFailure);
            Run("real-byte publication replay is idempotent and late commits are rechecked", Replay);
            Run("first scan never exposes old historical versions", FirstScanLatestOnly);
            Run("new complete real-byte report group is installed automatically", NewReport);
            Run("historical multi-report batch retains independent latest group", MultiReportLatest);
            Run("scan memory bound counts current candidates rather than publication history", CandidateMemoryBound);
            Run("empty startup followed by import registers new baseline without resetting tracked edits", ImportAfterEmptyStartup);
            Run("local conflicts, stale decisions, accept HIS and keep local", Conflicts);
            Run("incomplete pairs, unallowlisted XML and duplicate targets stay pending", PolicyGuards);
            Run("failed publication is retried after restart", Retry);
            Check(sources.Select(ReportWorkDirectory.HashFile).SequenceEqual(hashes), "source LIB unchanged");
            Console.WriteLine("PASS " + passed + " groups; real LIB source hashes unchanged; Oracle/IIS NOT RUN.");
            Console.WriteLine("Isolated recovery evidence: " + testRoot);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action, string code = null)
    {
        try { action(); }
        catch (SyncSafetyException ex) { if (code != null) Check(ex.Code == code, "unexpected safety code " + ex.Code); return; }
        throw new Exception("expected safety rejection");
    }

    private static byte[] Zip(byte[] data, string name = "", bool second = false)
    {
        if (name == "")
        {
            // A standard stored ZIP with a zero-length filename, matching the observed HIS envelope feature.
            // net48 ZipArchive.CreateEntry rejects empty names; writing the test envelope avoids new dependencies.
            using (var output = new MemoryStream())
            using (var w = new BinaryWriter(output, Encoding.UTF8, true))
            {
                uint crc = Crc(data);
                w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
                w.Write(0u); w.Write(crc); w.Write(data.Length); w.Write(data.Length); w.Write((ushort)0); w.Write((ushort)0); w.Write(data);
                int central = (int)output.Position;
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0);
                w.Write(0u); w.Write(crc); w.Write(data.Length); w.Write(data.Length);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u);
                int size = (int)output.Position - central;
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
                w.Write(size); w.Write(central); w.Write((ushort)0); w.Flush(); return output.ToArray();
            }
        }
        using (var output = new MemoryStream())
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(data, 0, data.Length);
                if (second) using (var stream = zip.CreateEntry("second.xml").Open()) stream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
    }
    private static uint Crc(byte[] bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u); }
        return ~crc;
    }
    private static byte[] Extract(byte[] bytes) { return SafeReportArchive.Extract(bytes, 32 * 1024 * 1024, 32 * 1024 * 1024); }
    private static void ArchiveRoundtrip()
    { Check(Extract(Zip(query)).SequenceEqual(query), "empty entry"); Check(Extract(Zip(layout, "layout.xml")).SequenceEqual(layout), "named deflate entry"); }
    private static void ArchiveGuards()
    {
        foreach (string name in new[] { "../evil.xml", "/evil.xml", "C:/evil.xml", "a\\..\\evil.xml", "a.xml:stream", "nul.xml" }) Reject(() => Extract(Zip(query, name)));
        Reject(() => Extract(Zip(query, "report.xml", true)), "ArchiveEntryCountRejected");
        foreach (string name in new[] { "", "report.xml" })
        foreach (uint attributes in new uint[] { 0xA1FF0000, 0x400 })
        {
            byte[] link = Zip(query, name);
            int central = (int)BitConverter.ToUInt32(link, link.Length - 6);
            Array.Copy(BitConverter.GetBytes(attributes), 0, link, central + 38, 4);
            Reject(() => Extract(link), "ArchiveLinkRejected");
        }
        byte[] corrupt = Zip(query); corrupt[35] ^= 1; Reject(() => Extract(corrupt), "ArchiveIntegrityRejected");
        Reject(() => Extract(Zip(query).Take(100).ToArray()));
        Reject(() => SafeReportArchive.Extract(Zip(query), 32 * 1024 * 1024, query.Length - 1), "ArchiveSizeRejected");
        Reject(() => SafeReportArchive.Extract(Zip(query), 1, 32 * 1024 * 1024), "ArchiveSizeRejected");
    }
    private static void Paths()
    {
        using (var f = Fixture.New())
        {
            foreach (string path in new[] { "../a.xml", "x/../../a.xml", "\\root.xml", "C:\\a.xml", "x//a.xml", "a.xml.", "a.xml ", "a.xml:ads", "COM1.xml", "LPT¹.xml" })
                Reject(() => f.Work.Resolve(path), "UnsafePath");
            Check(f.Work.Resolve(QueryPath).StartsWith(f.Work.Root, StringComparison.OrdinalIgnoreCase), "valid path");
        }
    }
    private static void Disabled()
    {
        using (var f = Fixture.New())
        {
            var provider = new ForbiddenConnection(); var reader = new OracleReleaseReader(provider, f.Options);
            f.Options.Enabled = false;
            var c = new SyncCoordinator(f.Options, reader, f.Policy, f.Sink);
            Check(c.RunOnce().Code == "Disabled", "disabled status");
            f.Options.Enabled = true;
            Check(c.RunOnce().Code == "OracleNotValidated", "validation gate");
            Reject(() => reader.ReadAll(CancellationToken.None).ToList(), "OracleNotValidated");
            Check(provider.Calls == 0, "no credential callback");
        }
    }
    private static int Child(string mode, string directory)
    {
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, mode + " \"" + directory + "\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var child = Process.Start(start)) { Check(child.WaitForExit(10000), "child finished"); return child.ExitCode; }
    }
    private static void CrossProcessLock()
    { using (var f = Fixture.New()) using (f.Work.Acquire()) Check(Child("lock", f.Root) == 23, "lock not shared across processes"); }
    private static int CheckChildLock(string root)
    {
        var work = new ReportWorkDirectory(Path.Combine(root, "Reports"), Path.Combine(root, "Sync"));
        using (var cancel = new CancellationTokenSource(300))
        { try { using (work.Acquire(cancel.Token)) return 1; } catch (OperationCanceledException) { return 23; } }
    }
    private static void Rollback()
    {
        using (var f = Fixture.New())
        {
            var tx = new ReportFileTransaction(f.Work); int calls = 0;
            Reject(() => tx.ExecuteProtectedEdit(new[] { QueryPath, LayoutPath }, () =>
            {
                File.WriteAllBytes(f.Work.Resolve(QueryPath), otherQuery);
                File.WriteAllBytes(f.Work.Resolve(LayoutPath), otherQuery);
            }, () => { calls++; if (calls == 1) throw new InvalidOperationException("secret raw exception must not escape"); }), "TransactionRolledBack");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "query rolled back");
            Check(File.ReadAllBytes(f.Work.Resolve(LayoutPath)).SequenceEqual(layout), "layout rolled back");
            Check(calls == 2 && !tx.RequiresRecovery, "rollback reload");
        }
    }
    private static int Crash(string root)
    {
        var work = new ReportWorkDirectory(Path.Combine(root, "Reports"), Path.Combine(root, "Sync"));
        new ReportFileTransaction(work).ExecuteProtectedEdit(new[] { QueryPath, LayoutPath }, () =>
        { File.WriteAllBytes(work.Resolve(QueryPath), File.ReadAllBytes(work.Resolve(LayoutPath))); Environment.Exit(27); }, () => { });
        return 1;
    }
    private static void CrashRecovery()
    {
        using (var f = Fixture.New())
        {
            Check(Child("crash", f.Root) == 27, "crash injected");
            var tx = new ReportFileTransaction(f.Work); Check(tx.RequiresRecovery, "durable pending");
            int reloads = 0; tx.Recover(() => reloads++);
            Check(reloads == 1 && !tx.RequiresRecovery, "recovery complete");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "crash rollback bytes");
        }
    }
    private static void RecoveryFailure()
    {
        using (var f = Fixture.New())
        {
            Check(Child("crash", f.Root) == 27, "crash injected");
            var tx = new ReportFileTransaction(f.Work);
            Reject(() => tx.Recover(() => { throw new Exception("do not log this"); }), "RecoveryRequired");
            Check(tx.RequiresRecovery, "failed recovery marker preserved"); tx.Recover(() => { });
        }
    }

    private static void Replay()
    {
        using (var f = Fixture.New())
        {
            f.Source.Batches.Add(Batch(20, otherQuery));
            Check(f.Coordinator.RunOnce().AppliedReleases == 1, "publication applied");
            int reloads = f.Sink.Calls; Check(f.Coordinator.RunOnce().AppliedReleases == 0 && f.Sink.Calls == reloads, "idempotent");
            f.Source.Batches.Insert(0, Batch(10, query));
            var status = f.Coordinator.RunOnce();
            Check(status.PendingReleases == 0, "late older release inspected and superseded");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(otherQuery), "late old release cannot roll back newest");
            Check(File.ReadAllText(Path.Combine(f.Work.StateRoot, "sync-state.xml")).Contains("<ReleaseId>10</ReleaseId>"), "late release acknowledged");
        }
    }
    private static void FirstScanLatestOnly()
    {
        using (var f = Fixture.New())
        {
            f.Source.Batches.Add(Batch(1, otherQuery)); f.Source.Batches.Add(Batch(2, query));
            f.Sink.OnReload = () => Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "old history must never enter catalog");
            Check(f.Coordinator.RunOnce().Code == "Success", "initial history scan");
            Check(f.Sink.Calls == 1, "only latest batch reloaded");
        }
    }
    private static void NewReport()
    {
        using (var f = Fixture.New())
        {
            var batch = Batch(1, query);
            batch.Files[0].FileName = "新增查询设置.xml"; batch.Files[1].FileName = "新增报表设置.xml";
            f.Source.Batches.Add(batch);
            Check(f.Coordinator.RunOnce().AppliedReleases == 1, "new pair applied");
            Check(File.ReadAllBytes(f.Work.Resolve("Config/Xml/新增查询设置.xml")).SequenceEqual(query), "new query exact source bytes");
            Check(File.ReadAllBytes(f.Work.Resolve("Config/Xml/新增报表设置.xml")).SequenceEqual(layout), "new template exact source bytes");
            Check(f.Coordinator.RunOnce().AppliedReleases == 0, "new pair idempotent");
            Check(f.Policy.Paths.Contains("Config/Xml/新增查询设置.xml"), "new group joins allowlist");
        }
    }
    private static void MultiReportLatest()
    {
        using (var f = Fixture.New())
        {
            var first = Batch(1, otherQuery);
            var independent = Batch(1, query);
            independent.Files[0].FileId = "new-query"; independent.Files[0].FileName = "新增查询设置.xml";
            independent.Files[1].FileId = "new-layout"; independent.Files[1].FileName = "新增报表设置.xml";
            first.Files.AddRange(independent.Files); f.Source.Batches.Add(first); f.Source.Batches.Add(Batch(2, query));
            f.Sink.OnReload = () => Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "no superseded group exposed");
            Check(f.Coordinator.RunOnce().Code == "Success", "multi-report history succeeds");
            Check(File.Exists(f.Work.Resolve("Config/Xml/新增查询设置.xml")), "independent group not lost");
        }
    }
    private static void ImportAfterEmptyStartup()
    {
        string root = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
        string reports = Path.Combine(root, "Reports"); Directory.CreateDirectory(reports);
        var work = new ReportWorkDirectory(reports, Path.Combine(root, "Sync"));
        var options = new SyncOptions { WorkingDirectory = work.Root, StateDirectory = work.StateRoot, Enabled = true };
        var source = new OfflineArchiveSource(); var sink = new Sink();
        var c = new SyncCoordinator(options, source, ReportFilePolicy.FromBaseline(work), sink); c.InitializeBaseline();
        Directory.CreateDirectory(Path.GetDirectoryName(work.Resolve(QueryPath)));
        File.WriteAllBytes(work.Resolve(QueryPath), query); File.WriteAllBytes(work.Resolve(LayoutPath), layout);
        c.RegisterImportedBaseline(); source.Batches.Add(Batch(1, otherQuery));
        Check(c.RunOnce().AppliedReleases == 1, "new import baseline should not conflict");
        File.WriteAllBytes(work.Resolve(QueryPath), query); c.RegisterImportedBaseline();
        source.Batches.Add(Batch(2, otherQuery));
        Check(c.RunOnce().ConflictReleases == 1, "import must not reset tracked local edits");
    }
    private static void CandidateMemoryBound()
    {
        using (var f = Fixture.New())
        {
            long current = Math.Max(query.LongLength, otherQuery.LongLength) + layout.LongLength;
            f.Options.MaximumScanExpandedBytes = current;
            for (int id = 1; id <= 24; id++) f.Source.Batches.Add(Batch(id, id % 2 == 0 ? query : otherQuery));
            f.Sink.OnReload = () => Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "only latest candidate exposed");
            Check(f.Coordinator.RunOnce().Code == "Success", "24 historical versions must not exhaust one-candidate bound");
            Check(f.Sink.Calls == 1, "only latest candidate applied");
            f.Options.MaximumScanExpandedBytes = current - 1;
            Check(f.Coordinator.RunOnce().Code == "ScanSizeRejected", "actual current candidate bound remains enforced");
        }
    }
    private static void Conflicts()
    {
        using (var f = Fixture.New())
        {
            File.WriteAllBytes(f.Work.Resolve(QueryPath), layout); // deliberately local safety fixture, no DB/query result
            f.Source.Batches.Add(Batch(1, otherQuery));
            Check(f.Coordinator.RunOnce().ConflictReleases == 1, "local edit conflict");
            Check(f.Coordinator.GetPendingConflicts().Single().CanResolve && f.Coordinator.GetPendingConflicts()[0].Code == "LocalConflict", "UI conflict API");
            f.Coordinator.ResolveConflict(1, ConflictResolution.AcceptHis);
            File.WriteAllBytes(f.Work.Resolve(QueryPath), Encoding.UTF8.GetBytes("changed again"));
            Check(f.Coordinator.RunOnce().ConflictReleases == 1, "stale accept invalidated");
            f.Coordinator.ResolveConflict(1, ConflictResolution.AcceptHis);
            Check(f.Coordinator.RunOnce().AppliedReleases == 1, "explicit accept");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(otherQuery), "accepted HIS bytes");
            File.WriteAllBytes(f.Work.Resolve(QueryPath), layout); f.Source.Batches.Add(Batch(2, query));
            Check(f.Coordinator.RunOnce().ConflictReleases == 1, "second local conflict");
            f.Coordinator.ResolveConflict(2, ConflictResolution.KeepLocal);
            Check(f.Coordinator.RunOnce().Code == "Success", "keep local acknowledged");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(layout), "keep local preserved");
        }
    }
    private static void PolicyGuards()
    {
        using (var f = Fixture.New())
        {
            var batch = Batch(1, otherQuery); batch.Files.RemoveAt(1); f.Source.Batches.Add(batch);
            Check(f.Coordinator.RunOnce().PendingReleases == 1, "incomplete pair pending");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "no half pair");
            f.Source.Batches.Clear(); batch = Batch(1, otherQuery); batch.Files[0].FileName = "unknown.xml"; f.Source.Batches.Add(batch);
            Check(f.Coordinator.RunOnce().PendingReleases == 1, "unknown blocked");
            f.Source.Batches.Clear(); batch = Batch(1, otherQuery); batch.Files.Add(new PublishedFile
            { FileId = "duplicate", FileName = "1041查询设置.xml", Directory = "Config/Xml", Content = Zip(query) }); f.Source.Batches.Add(batch);
            Check(f.Coordinator.RunOnce().PendingReleases == 1, "duplicate blocked");
        }
    }
    private static void Retry()
    {
        using (var f = Fixture.New())
        {
            f.Source.Batches.Add(Batch(1, otherQuery)); f.Sink.FailNext = true;
            Check(f.Coordinator.RunOnce().PendingReleases == 1, "reload failure pending");
            Check(File.ReadAllBytes(f.Work.Resolve(QueryPath)).SequenceEqual(query), "failed release rollback");
            var restarted = new SyncCoordinator(f.Options, f.Source, f.Policy, f.Sink);
            Check(restarted.RunOnce().AppliedReleases == 1, "restart retry");
            string disk = File.ReadAllText(Path.Combine(f.Work.StateRoot, "sync-state.xml"));
            Check(!disk.Contains("select ") && !disk.Contains("do not log") && !disk.Contains("secret"), "state metadata only");
        }
    }
    private static PublishedRelease Batch(int id, byte[] queryBytes)
    {
        return new PublishedRelease { ReleaseId = id, Files = new List<PublishedFile>
        {
            new PublishedFile { FileId = "query-" + id, FileName = "1041查询设置.xml", Directory = "Config/Xml", Content = Zip(queryBytes) },
            new PublishedFile { FileId = "layout-" + id, FileName = "1041报表设置.xml", Directory = "Config/Xml", Content = Zip(layout, "layout.xml") }
        } };
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root; internal SyncOptions Options; internal ReportWorkDirectory Work; internal ReportFilePolicy Policy;
        internal OfflineArchiveSource Source = new OfflineArchiveSource(); internal Sink Sink = new Sink(); internal SyncCoordinator Coordinator;
        internal static Fixture New()
        {
            var f = new Fixture { Root = Path.Combine(testRoot, Guid.NewGuid().ToString("N")) };
            f.Options = new SyncOptions { WorkingDirectory = Path.Combine(f.Root, "Reports"), StateDirectory = Path.Combine(f.Root, "Sync"), Enabled = true };
            Directory.CreateDirectory(Path.Combine(f.Options.WorkingDirectory, "Config", "Xml"));
            f.Work = new ReportWorkDirectory(f.Options.WorkingDirectory, f.Options.StateDirectory);
            File.WriteAllBytes(f.Work.Resolve(QueryPath), query); File.WriteAllBytes(f.Work.Resolve(LayoutPath), layout);
            f.Policy = new ReportFilePolicy(new[] { new ReportSyncGroup { QueryPath = QueryPath, LayoutPaths = new List<string> { LayoutPath } } });
            f.Coordinator = new SyncCoordinator(f.Options, f.Source, f.Policy, f.Sink); f.Coordinator.InitializeBaseline(); return f;
        }
        public void Dispose() { } // keep isolated evidence; never delete or modify the original LIB
    }
    private sealed class OfflineArchiveSource : IReleaseReader
    {
        internal List<PublishedRelease> Batches = new List<PublishedRelease>();
        public bool IsValidated { get { return true; } }
        public IEnumerable<PublishedRelease> ReadAll(CancellationToken cancellation) { return Batches; }
    }
    private sealed class Sink : IReportReloadSink
    {
        internal int Calls; internal bool FailNext; internal Action OnReload;
        public void Reload(IReadOnlyList<string> paths) { Calls++; OnReload?.Invoke(); if (FailNext) { FailNext = false; throw new InvalidOperationException("do not log raw SQL or credentials"); } }
    }
    private sealed class ForbiddenConnection : IOracleSyncConnectionProvider
    { internal int Calls; public string GetConnectionString() { Calls++; throw new Exception("Oracle must not be contacted"); } }
}
