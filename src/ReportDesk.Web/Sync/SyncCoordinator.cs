#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReportDesk.Web.Sync
{
    public sealed class SyncCoordinator
    {
        private readonly SyncOptions options;
        private readonly IReleaseReader reader;
        private readonly ReportFilePolicy policy;
        private readonly IReportReloadSink reload;
        private readonly ReportWorkDirectory work;
        private readonly ReportFileTransaction transaction;
        private readonly object statusLock = new object();
        private SyncStatus status = new SyncStatus();
        private string StatePath { get { return Path.Combine(work.StateRoot, "sync-state.xml"); } }

        public SyncCoordinator(SyncOptions options, IReleaseReader reader, ReportFilePolicy policy, IReportReloadSink reloadSink)
        {
            this.options = options; this.reader = reader; this.policy = policy; reload = reloadSink;
            if (options.MaximumCompressedBytes <= 0 || options.MaximumExpandedBytes <= 0 || options.MaximumBatchBytes <= 0 || options.MaximumScanExpandedBytes <= 0 || options.MaximumFilesPerRelease <= 0)
                throw new SyncSafetyException("InvalidLimits");
            work = new ReportWorkDirectory(options.WorkingDirectory, options.StateDirectory);
            transaction = new ReportFileTransaction(work);
            Publish(options.Enabled ? (reader.IsValidated ? "NotStarted" : "OracleNotValidated") : "Disabled", null);
        }

        public SyncStatus Status { get { lock (statusLock) return status.Copy(); } }
        public ReportWorkDirectory WorkDirectory { get { return work; } }

        public void Recover(CancellationToken cancellation = default(CancellationToken))
        {
            try { transaction.Recover(() => reload.Reload(policy.Paths), cancellation); }
            catch { Publish("RecoveryRequired", null); throw new SyncSafetyException("RecoveryRequired"); }
        }

        // Call after copying/importing the initial LIB baseline, before permitting local edits.
        // Existing tracked hashes are NEVER reset; previously untracked imported groups can join the baseline.
        public void InitializeBaseline(CancellationToken cancellation = default(CancellationToken))
        {
            using (work.Acquire(cancellation))
            {
                transaction.RecoverUnderLock(() => reload.Reload(policy.Paths));
                AddImportedBaselineUnderLock(cancellation);
            }
        }

        // Invoke AFTER a successful maintenance import transaction has released its lease.
        // Only new paths gain baseline hashes; replacing a tracked file keeps conflict detection intact.
        public void RegisterImportedBaseline(CancellationToken cancellation = default(CancellationToken))
        {
            using (work.Acquire(cancellation))
            {
                transaction.RecoverUnderLock(() => reload.Reload(policy.Paths));
                policy.IncludeImportedGroups(ReportFilePolicy.FromBaseline(work));
                AddImportedBaselineUnderLock(cancellation);
            }
        }

        private void AddImportedBaselineUnderLock(CancellationToken cancellation)
        {
            var state = File.Exists(StatePath) ? LoadState() : new SyncDiskState();
            foreach (var path in policy.Paths)
            {
                if (state.Files.Any(f => Equal(f.Path, path))) continue;
                cancellation.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(work.Resolve(path)); policy.ValidateFile(path, bytes);
                state.Files.Add(new SyncedFile { Path = path, Hash = ReportWorkDirectory.Hash(bytes) });
            }
            XmlDisk.Save(StatePath, state);
        }

        public SyncStatus RunOnce(CancellationToken cancellation = default(CancellationToken))
        {
            // Distinct from the write lock: downloads must not block interactive definition reads/edits.
            FileStream runLease;
            try
            {
                string runPath = Path.Combine(work.Root, ".reportdesk-sync.lock");
                ReportWorkDirectory.CheckLinks(runPath);
                runLease = new FileStream(runPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return Publish("Busy", null); }
            using (runLease)
            {
                try
                {
                    Recover(cancellation);
                    if (!options.Enabled) return Publish("Disabled", null);
                    if (!reader.IsValidated) return Publish("OracleNotValidated", null);
                    SyncDiskState state;
                    using (work.Acquire(cancellation)) state = LoadState();
                    Publish("Running", state);
                    int applied = 0; int lastId = -1;
                    var candidates = new List<PreparedRelease>();
                    var superseded = new List<SeenRelease>();
                    // Stage and verify the entire snapshot BEFORE exposing any historical version.
                    // Latest complete batches win. Overlapping older batches are never replayed into the catalog.
                    foreach (var release in reader.ReadAll(cancellation))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (release.ReleaseId < 0 || release.ReleaseId <= lastId) throw new SyncSafetyException("PublicationOrderRejected");
                        lastId = release.ReleaseId;
                        PreparedRelease prepared;
                        try { prepared = Prepare(release); }
                        catch (SyncSafetyException ex)
                        {
                            using (work.Acquire(cancellation))
                            {
                                state = LoadState(); Pending(state, release.ReleaseId, ex.Code, "", ""); XmlDisk.Save(StatePath, state);
                            }
                            continue;
                        }
                        prepared.ReleaseId = release.ReleaseId;
                        // Keep current complete groups, not every historical version's BLOB/XML bytes.
                        for (int i = candidates.Count - 1; i >= 0; i--)
                        {
                            var prior = candidates[i];
                            if (!prior.Files.Keys.Any(prepared.Files.ContainsKey)) continue;
                            var remaining = prior.Files.Where(p => !prepared.Files.ContainsKey(p.Key))
                                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                            if (remaining.Count == 0)
                            {
                                superseded.Add(new SeenRelease { ReleaseId = prior.ReleaseId, Fingerprint = prior.Fingerprint });
                                candidates.RemoveAt(i);
                            }
                            else
                            {
                                try { policy.ValidateBatch(remaining); prior.Files = remaining; }
                                catch (SyncSafetyException)
                                {
                                    using (work.Acquire(cancellation))
                                    {
                                        state = LoadState(); Pending(state, prior.ReleaseId, "OverlappingReportBatch", prior.Fingerprint, "");
                                        XmlDisk.Save(StatePath, state);
                                    }
                                    candidates.RemoveAt(i);
                                }
                            }
                        }
                        candidates.Add(prepared);
                        long candidateBytes = candidates.Sum(c => c.Files.Values.Sum(b => b.LongLength));
                        if (candidateBytes > options.MaximumScanExpandedBytes) throw new SyncSafetyException("ScanSizeRejected");
                    }
                    using (work.Acquire(cancellation))
                    {
                        state = LoadState();
                        foreach (var old in superseded) MarkSeen(state, old.ReleaseId, old.Fingerprint);
                        XmlDisk.Save(StatePath, state);
                    }
                    var latest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var candidate in candidates)
                        foreach (var path in candidate.Files.Keys) latest[path] = candidate.ReleaseId;
                    foreach (var prepared in candidates)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int releaseId = prepared.ReleaseId;
                        using (work.Acquire(cancellation))
                        {
                            transaction.RecoverUnderLock(() => reload.Reload(policy.Paths));
                            state = LoadState();
                            var seen = state.Releases.Find(r => r.ReleaseId == releaseId);
                            if (seen != null && seen.Fingerprint == prepared.Fingerprint) continue;
                            // Retain independent groups from older multi-report releases, but never replay their superseded files.
                            var currentFiles = prepared.Files.Where(p => latest[p.Key] == releaseId &&
                                !state.Files.Any(f => Equal(f.Path, p.Key) && f.ReleaseId > releaseId))
                                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                            if (currentFiles.Count == 0)
                            {
                                MarkSeen(state, releaseId, prepared.Fingerprint);
                                XmlDisk.Save(StatePath, state); continue;
                            }
                            if (currentFiles.Count != prepared.Files.Count)
                            {
                                try { policy.ValidateBatch(currentFiles); }
                                catch (SyncSafetyException)
                                {
                                    Pending(state, releaseId, "OverlappingReportBatch", prepared.Fingerprint, "");
                                    XmlDisk.Save(StatePath, state); continue;
                                }
                                prepared.Files = currentFiles;
                            }
                            string currentFingerprint = CurrentFingerprint(prepared.Files.Keys);
                            var pending = state.Pending.Find(p => p.ReleaseId == releaseId);
                            bool decisionCurrent = pending != null && pending.Fingerprint == prepared.Fingerprint && pending.LocalFingerprint == currentFingerprint;
                            bool accept = decisionCurrent && pending.Decision == "AcceptHis";
                            bool keep = decisionCurrent && pending.Decision == "KeepLocal";
                            if (keep)
                            {
                                MarkSeen(state, releaseId, prepared.Fingerprint); XmlDisk.Save(StatePath, state); continue;
                            }
                            bool conflict = prepared.Files.Any(pair =>
                            {
                                var baseline = state.Files.Find(f => Equal(f.Path, pair.Key));
                                var hash = ReportWorkDirectory.HashFile(work.Resolve(pair.Key));
                                return baseline == null ? hash != "" && hash != ReportWorkDirectory.Hash(pair.Value) :
                                    hash != baseline.Hash && hash != ReportWorkDirectory.Hash(pair.Value);
                            });
                            if (conflict && !accept)
                            {
                                Pending(state, releaseId, "LocalConflict", prepared.Fingerprint, currentFingerprint);
                                XmlDisk.Save(StatePath, state); continue;
                            }
                            try
                            {
                                var changed = prepared.Files.Where(p => ReportWorkDirectory.HashFile(work.Resolve(p.Key)) != ReportWorkDirectory.Hash(p.Value)).ToArray();
                                if (changed.Length > 0)
                                    transaction.ExecuteUnderLock(prepared.Files.Keys, () =>
                                    {
                                        foreach (var pair in changed)
                                        {
                                            var full = work.Resolve(pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(full));
                                            ReportWorkDirectory.AtomicWrite(full, pair.Value);
                                        }
                                    }, () => reload.Reload(prepared.Files.Keys.ToArray()), () =>
                                    {
                                        if (CurrentFingerprint(prepared.Files.Keys) != currentFingerprint) throw new SyncSafetyException("LocalConflict");
                                    });
                                else reload.Reload(prepared.Files.Keys.ToArray());
                                foreach (var pair in prepared.Files)
                                {
                                    var file = state.Files.Find(f => Equal(f.Path, pair.Key));
                                    if (file == null) { file = new SyncedFile { Path = pair.Key }; state.Files.Add(file); }
                                    file.Hash = ReportWorkDirectory.Hash(pair.Value); file.ReleaseId = releaseId;
                                }
                                policy.RegisterBatch(prepared.Files);
                                MarkSeen(state, releaseId, prepared.Fingerprint);
                                XmlDisk.Save(StatePath, state); applied++;
                            }
                            catch (SyncSafetyException ex)
                            {
                                if (transaction.RequiresRecovery) throw;
                                Pending(state, releaseId, ex.Code, prepared.Fingerprint, currentFingerprint);
                                XmlDisk.Save(StatePath, state);
                            }
                        }
                    }
                    using (work.Acquire(cancellation))
                    {
                        state = LoadState();
                        if (state.Pending.Count == 0) state.LastSuccessUtc = DateTime.UtcNow;
                        XmlDisk.Save(StatePath, state);
                    }
                    return Publish(state.Pending.Count == 0 ? "Success" : "Pending", state, applied);
                }
                catch (OperationCanceledException) { return Publish("Cancelled", null); }
                catch (SyncSafetyException ex) { return Publish(ex.Code, null); }
                catch { return Publish("SyncFailed", null); }
            }
        }

        // Decision is bound to both the observed publication and local hashes, then rechecked on RunOnce.
        public IReadOnlyList<SyncPendingConflict> GetPendingConflicts(CancellationToken cancellation = default(CancellationToken))
        {
            using (work.Acquire(cancellation))
                return LoadState().Pending.Select(p => new SyncPendingConflict
                { ReleaseId = p.ReleaseId, Code = p.Code, Fingerprint = p.Fingerprint, CanResolve = p.Code == "LocalConflict" }).ToArray();
        }

        public void ResolveConflict(int releaseId, ConflictResolution resolution, CancellationToken cancellation = default(CancellationToken))
        { ResolveConflict(releaseId, null, resolution, cancellation); }

        public void ResolveConflict(int releaseId, string expectedFingerprint, ConflictResolution resolution,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (resolution != ConflictResolution.AcceptHis && resolution != ConflictResolution.KeepLocal) throw new SyncSafetyException("InvalidDecision");
            using (work.Acquire(cancellation))
            {
                var state = LoadState(); var pending = state.Pending.Find(p => p.ReleaseId == releaseId && p.Code == "LocalConflict");
                if (pending == null) throw new SyncSafetyException("ConflictNotFound");
                if (expectedFingerprint != null && pending.Fingerprint != expectedFingerprint) throw new SyncSafetyException("ConflictChanged");
                pending.Decision = resolution.ToString(); XmlDisk.Save(StatePath, state);
            }
        }

        private PreparedRelease Prepare(PublishedRelease release)
        {
            if (release.Files == null || release.Files.Count == 0) throw new SyncSafetyException("PublicationContentMissing");
            if (release.Files.Count > options.MaximumFilesPerRelease) throw new SyncSafetyException("ReleaseSizeRejected");
            var result = new PreparedRelease(); long size = 0, compressed = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal); var signature = new StringBuilder();
            foreach (var file in release.Files.OrderBy(f => f.FileId, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(file.FileId) || !ids.Add(file.FileId)) throw new SyncSafetyException("PublicationIdentityRejected");
                if (file.IsDirectory) throw new SyncSafetyException("DirectoryPackageRejected");
                string name = ReportWorkDirectory.Normalize(file.FileName);
                if (name.Contains("/")) throw new SyncSafetyException("UnsafePath");
                string path = string.IsNullOrEmpty(file.Directory) ? name : ReportWorkDirectory.Normalize(file.Directory) + "/" + name;
                work.Resolve(path);
                if (result.Files.ContainsKey(path)) throw new SyncSafetyException("DuplicateTargetRejected");
                if (file.Content == null) throw new SyncSafetyException("PublicationContentMissing");
                compressed += file.Content.LongLength;
                if (compressed > options.MaximumBatchBytes) throw new SyncSafetyException("ReleaseSizeRejected");
                var bytes = SafeReportArchive.Extract(file.Content, options.MaximumCompressedBytes, options.MaximumExpandedBytes);
                size += bytes.LongLength;
                if (size > options.MaximumBatchBytes) throw new SyncSafetyException("ReleaseSizeRejected");
                result.Files.Add(path, bytes);
                signature.Append(ReportWorkDirectory.Identifier(file.FileId)).Append(':').Append(ReportWorkDirectory.Identifier(path.ToUpperInvariant()))
                    .Append(':').Append(ReportWorkDirectory.Hash(bytes)).Append('\n');
            }
            policy.ValidateBatch(result.Files);
            result.Fingerprint = ReportWorkDirectory.Identifier(signature.ToString()); return result;
        }

        private SyncDiskState LoadState()
        {
            if (!File.Exists(StatePath)) throw new SyncSafetyException("BaselineRequired");
            try
            {
                var state = XmlDisk.Load<SyncDiskState>(StatePath);
                if (state.Version != 1 || state.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
                    throw new SyncSafetyException("SyncStateInvalid");
                foreach (var file in state.Files) work.Resolve(file.Path);
                return state;
            }
            catch { throw new SyncSafetyException("SyncStateInvalid"); }
        }
        private string CurrentFingerprint(IEnumerable<string> paths)
        { return ReportWorkDirectory.Identifier(string.Join("\n", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => p.ToUpperInvariant() + ":" + ReportWorkDirectory.HashFile(work.Resolve(p))))); }
        private static bool Equal(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static void MarkSeen(SyncDiskState state, int id, string fingerprint)
        {
            state.Releases.RemoveAll(r => r.ReleaseId == id); state.Pending.RemoveAll(r => r.ReleaseId == id);
            state.Releases.Add(new SeenRelease { ReleaseId = id, Fingerprint = fingerprint });
        }
        private static void Pending(SyncDiskState state, int id, string code, string fingerprint, string local)
        {
            state.Pending.RemoveAll(p => p.ReleaseId == id);
            state.Pending.Add(new PendingRelease { ReleaseId = id, Code = code, Fingerprint = fingerprint, LocalFingerprint = local });
        }
        private SyncStatus Publish(string code, SyncDiskState disk, int applied = 0)
        {
            lock (statusLock)
            {
                status = new SyncStatus
                {
                    Code = code, LastSuccessUtc = disk == null ? status.LastSuccessUtc : disk.LastSuccessUtc,
                    AppliedReleases = applied, PendingReleases = disk == null ? status.PendingReleases : disk.Pending.Count,
                    ConflictReleases = disk == null ? status.ConflictReleases : disk.Pending.Count(p => p.Code == "LocalConflict"),
                    ReleaseId = disk?.Pending.FirstOrDefault()?.ReleaseId,
                    RequiresRecovery = transaction.RequiresRecovery, OracleValidated = reader.IsValidated
                };
                return status.Copy();
            }
        }
        private sealed class PreparedRelease
        {
            internal int ReleaseId;
            internal Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            internal string Fingerprint = "";
        }
    }

    public sealed class SyncPendingConflict
    {
        public int ReleaseId { get; internal set; }
        public string Code { get; internal set; } = "";
        public string Fingerprint { get; internal set; } = "";
        public bool CanResolve { get; internal set; }
    }

    public sealed class SyncDiskState
    {
        public int Version { get; set; } = 1;
        public DateTime? LastSuccessUtc { get; set; }
        public List<SyncedFile> Files { get; set; } = new List<SyncedFile>();
        public List<SeenRelease> Releases { get; set; } = new List<SeenRelease>();
        public List<PendingRelease> Pending { get; set; } = new List<PendingRelease>();
    }
    public sealed class SyncedFile
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
        public int ReleaseId { get; set; } = -1;
    }
    public sealed class SeenRelease
    {
        public int ReleaseId { get; set; }
        public string Fingerprint { get; set; } = "";
    }
    public sealed class PendingRelease
    {
        public int ReleaseId { get; set; }
        public string Fingerprint { get; set; } = "";
        public string LocalFingerprint { get; set; } = "";
        public string Code { get; set; } = "";
        public string Decision { get; set; } = "";
    }
}
