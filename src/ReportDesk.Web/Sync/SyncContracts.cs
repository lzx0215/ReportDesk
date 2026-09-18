#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;

namespace ReportDesk.Web.Sync
{
    public sealed class SyncOptions
    {
        public string WorkingDirectory { get; set; } = "";
        public string StateDirectory { get; set; } = "";
        public bool Enabled { get; set; }
        public long MaximumCompressedBytes { get; set; } = 32 * 1024 * 1024;
        public long MaximumExpandedBytes { get; set; } = 32 * 1024 * 1024;
        public long MaximumBatchBytes { get; set; } = 128 * 1024 * 1024;
        public long MaximumScanExpandedBytes { get; set; } = 256 * 1024 * 1024;
        public int MaximumFilesPerRelease { get; set; } = 128;
    }

    // No raw exception messages, paths, SQL, parameters, credentials or publication descriptions.
    public sealed class SyncStatus
    {
        public string Code { get; internal set; } = "NotStarted";
        public DateTime? LastSuccessUtc { get; internal set; }
        public int AppliedReleases { get; internal set; }
        public int PendingReleases { get; internal set; }
        public int ConflictReleases { get; internal set; }
        public int? ReleaseId { get; internal set; }
        public bool RequiresRecovery { get; internal set; }
        public bool OracleValidated { get; internal set; }
        public string ScanMode { get { return "FullRecheck"; } }
        internal SyncStatus Copy() { return (SyncStatus)MemberwiseClone(); }
    }

    public sealed class PublishedRelease
    {
        public int ReleaseId { get; set; }
        public List<PublishedFile> Files { get; set; } = new List<PublishedFile>();
    }

    public sealed class PublishedFile
    {
        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Directory { get; set; } = "";
        public bool IsDirectory { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    public interface IReleaseReader
    {
        bool IsValidated { get; }
        // One consistent publication snapshot. Full recheck deliberately includes old/late commits.
        // Implementations must never return fake Oracle rows; offline checks use a separate byte-source fixture.
        IEnumerable<PublishedRelease> ReadAll(CancellationToken cancellation);
    }

    public interface IOracleSyncConnectionProvider
    {
        string GetConnectionString();
    }

    public interface IReportReloadSink
    {
        // Called under the shared directory lock, after all files change. Throw to roll back.
        // Must atomically swap the prepared catalog; never retain a partially prepared catalog on failure.
        void Reload(IReadOnlyList<string> relativePaths);
    }

    public enum ConflictResolution { AcceptHis, KeepLocal }

    public sealed class SyncSafetyException : Exception
    {
        public string Code { get; private set; }
        internal SyncSafetyException(string code) : base(code) { Code = code; }
    }
}
