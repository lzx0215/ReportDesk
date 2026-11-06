#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using ReportDesk.Core;
using ReportDesk.Web.Sync;

namespace ReportDesk.Web.Publish
{
    // A local preview only: neither authenticates a user nor creates an HIS release.
    public sealed class PublishFilePreview
    {
        public string Role { get; private set; }
        public string RelativePath { get; private set; }
        public string Status { get; private set; }
        public string Sha256 { get; private set; }
        internal PublishFilePreview(string role, string path, string status, string hash)
        { Role = role; RelativePath = path; Status = status; Sha256 = hash; }
    }

    public sealed class ReportPublishPreview
    {
        public string QueryRelativePath { get; private set; }
        public IReadOnlyList<PublishFilePreview> Files { get; private set; }
        public bool RequiresConfirmation { get { return Files.Any(f => f.Status == "Candidate"); } }
        internal ReportPublishPreview(string query, IList<PublishFilePreview> files)
        { QueryRelativePath = query; Files = new ReadOnlyCollection<PublishFilePreview>(files.ToArray()); }
    }

    public static class ReportPublishPreparation
    {
        private const long MaximumFileBytes = 32L * 1024 * 1024;

        // queryRelativePath must come from the server's selected report, not an arbitrary client path.
        public static ReportPublishPreview Preview(ReportWorkDirectory work, string queryRelativePath,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            using (work.Acquire(cancellation)) return PrepareUnderLock(work, queryRelativePath, cancellation);
        }

        // The preview must be stored by the server and bound to the user's confirmation.
        // A future writer must revalidate and consume immutable bytes while holding the same lock;
        // this method alone does not make a subsequent, unlocked database write safe.
        public static void VerifyCurrent(ReportWorkDirectory work, ReportPublishPreview preview,
            bool candidatesConfirmed, CancellationToken cancellation = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (preview.RequiresConfirmation && !candidatesConfirmed) throw new SyncSafetyException("CandidateConfirmationRequired");
            using (work.Acquire(cancellation))
            {
                var now = PrepareUnderLock(work, preview.QueryRelativePath, cancellation);
                if (preview.Files.Count != now.Files.Count || preview.Files.Where((f, i) =>
                    !string.Equals(f.Role, now.Files[i].Role, StringComparison.Ordinal) ||
                    !string.Equals(f.Status, now.Files[i].Status, StringComparison.Ordinal) ||
                    !string.Equals(f.RelativePath, now.Files[i].RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(f.Sha256, now.Files[i].Sha256, StringComparison.Ordinal)).Any())
                    throw new SyncSafetyException("PreviewChanged");
            }
        }

        private static ReportPublishPreview PrepareUnderLock(ReportWorkDirectory work, string queryPath, CancellationToken cancellation)
        {
            queryPath = ReportWorkDirectory.Normalize(queryPath);
            var queryFull = work.Resolve(queryPath);
            var initialQuery = Read(work, queryPath);
            ValidateXml(initialQuery, true);
            var inventory = ReportFileDiscovery.Scan(work.Root, cancellation);
            if (inventory.Warnings.Count != 0) throw new SyncSafetyException("DiscoveryIncomplete");
            var related = ReportFileDiscovery.Match(queryFull, inventory, cancellation);
            // Main template association is a naming candidate. A nonstandard query name is unresolved.
            if (!Path.GetFileNameWithoutExtension(queryFull).EndsWith("查询设置", StringComparison.Ordinal) || related.Count == 0)
                throw new SyncSafetyException("MainCompanionUnresolved");
            if (related.Any(r => r.Paths.Count != 1 || (r.Status != "Matched" && r.Status != "Candidate")))
                throw new SyncSafetyException("CompanionUnresolved");
            var files = new List<PublishFilePreview> { new PublishFilePreview("Query", queryPath, "Source", ReportWorkDirectory.Hash(initialQuery)) };
            for (int i = 0; i < related.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string full = Path.GetFullPath(related[i].Paths[0]);
                if (!ReportWorkDirectory.Within(work.Root, full)) throw new SyncSafetyException("UnsafePath");
                string relative = ReportWorkDirectory.Normalize(full.Substring(work.Root.Length + 1));
                var bytes = Read(work, relative);
                ValidateXml(bytes, false);
                files.Add(new PublishFilePreview(i == 0 ? "Main" : "Detail", relative, related[i].Status, ReportWorkDirectory.Hash(bytes)));
            }
            // External writers do not necessarily honor the work lock; detect changes during discovery.
            foreach (var file in files)
                if (!string.Equals(file.Sha256, ReportWorkDirectory.Hash(Read(work, file.RelativePath)), StringComparison.Ordinal))
                    throw new SyncSafetyException("PreviewChanged");
            return new ReportPublishPreview(queryPath, files);
        }

        private static byte[] Read(ReportWorkDirectory work, string relative)
        {
            try
            {
                var full = work.Resolve(relative);
                using (var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length <= 0 || file.Length > MaximumFileBytes) throw new SyncSafetyException("ReportFileSizeRejected");
                    using (var memory = new MemoryStream())
                    {
                        var buffer = new byte[81920]; int count;
                        while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (memory.Length + count > MaximumFileBytes) throw new SyncSafetyException("ReportFileSizeRejected");
                            memory.Write(buffer, 0, count);
                        }
                        return memory.ToArray();
                    }
                }
            }
            catch (SyncSafetyException) { throw; }
            catch (IOException) { throw new SyncSafetyException("ReportFileUnavailable"); }
            catch (UnauthorizedAccessException) { throw new SyncSafetyException("ReportFileUnavailable"); }
        }

        private static void ValidateXml(byte[] bytes, bool query)
        {
            try
            {
                using (var input = new MemoryStream(bytes, false))
                using (var reader = XmlReader.Create(input, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                {
                    var root = XDocument.Load(reader).Root;
                    if (root == null || root.Name != (query ? "ReportQueryInfo" : "Spread")) throw new SyncSafetyException("ReportTypeRejected");
                    if (!query && (string)root.Attribute("class") != "FarPoint.Win.Spread.FpSpread") throw new SyncSafetyException("ReportTypeRejected");
                    if (query && !(root.Element("QueryDataSource")?.Elements("QueryDataSource") ?? Enumerable.Empty<XElement>())
                        .Any(e => !string.IsNullOrWhiteSpace((string)e.Element("Sql")) &&
                            new[] { "MainReportUsing", "DetailReportUsing", "TableGroupUsing" }.Contains((string)e.Element("SqlType"))))
                        throw new SyncSafetyException("IncompleteQuery");
                }
            }
            catch (SyncSafetyException) { throw; }
            catch (XmlException) { throw new SyncSafetyException("ReportXmlInvalid"); }
        }
    }
}
