#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using ReportDesk.Core;

namespace ReportDesk.Web.Sync
{
    public sealed class ReportSyncGroup
    {
        public string QueryPath { get; set; } = "";
        public List<string> LayoutPaths { get; set; } = new List<string>();
    }

    // Imported groups plus new complete report groups verified from the incoming batch's XML bytes.
    public sealed class ReportFilePolicy
    {
        private readonly List<ReportSyncGroup> groups;
        private readonly Dictionary<string, string> kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> Paths { get { return kinds.Keys.ToArray(); } }

        public ReportFilePolicy(IEnumerable<ReportSyncGroup> reportGroups)
        {
            groups = reportGroups.Select(g => new ReportSyncGroup
            {
                QueryPath = ReportWorkDirectory.Normalize(g.QueryPath),
                LayoutPaths = g.LayoutPaths.Select(ReportWorkDirectory.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            }).ToList();
            foreach (var g in groups)
            {
                Register(g.QueryPath, "ReportQueryInfo");
                foreach (var path in g.LayoutPaths) Register(path, "Spread");
            }
        }
        private void Register(string path, string kind)
        {
            string prior;
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || (kinds.TryGetValue(path, out prior) && prior != kind))
                throw new SyncSafetyException("InvalidAllowlist");
            kinds[path] = kind;
        }

        // This helper uses Core's existing matching evidence. Ambiguous/missing companions exclude the whole group.
        // Naming candidates remain candidates, not claims about menu placement or proven HIS equivalence.
        public static ReportFilePolicy FromBaseline(ReportWorkDirectory work)
        {
            var inventory = ReportFileDiscovery.Scan(work.Root);
            var groups = new List<ReportSyncGroup>();
            foreach (var query in inventory.Queries)
            {
                var report = ReportImporter.ImportFile(query);
                if (report == null || !ReportClassification.IsStandalone(report)) continue;
                var related = ReportFileDiscovery.Match(query, inventory);
                if (related.Any(r => r.Paths.Count != 1 || (r.Status != "Matched" && r.Status != "Candidate"))) continue;
                groups.Add(new ReportSyncGroup
                {
                    QueryPath = query.Substring(work.Root.Length + 1),
                    LayoutPaths = related.SelectMany(r => r.Paths).Select(p => p.Substring(work.Root.Length + 1)).ToList()
                });
            }
            return new ReportFilePolicy(groups);
        }

        internal void ValidateFile(string path, byte[] bytes)
        {
            string kind;
            if (!kinds.TryGetValue(path, out kind)) throw new SyncSafetyException("FileNotAllowlisted");
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                {
                    var root = XDocument.Load(reader).Root;
                    if (root == null || root.Name != kind) throw new SyncSafetyException("ReportTypeRejected");
                    if (kind == "Spread" && (string)root.Attribute("class") != "FarPoint.Win.Spread.FpSpread")
                        throw new SyncSafetyException("ReportTypeRejected");
                    if (kind == "ReportQueryInfo" && !(root.Element("QueryDataSource")?.Elements("QueryDataSource") ?? Enumerable.Empty<XElement>())
                        .Any(e => !string.IsNullOrWhiteSpace((string)e.Element("Sql")) &&
                            new[] { "MainReportUsing", "DetailReportUsing", "TableGroupUsing" }.Contains((string)e.Element("SqlType"))))
                        throw new SyncSafetyException("IncompleteQuery");
                    if (kind == "ReportQueryInfo") ValidateDetailReference(path, root);
                }
            }
            catch (SyncSafetyException) { throw; }
            catch { throw new SyncSafetyException("ReportXmlInvalid"); }
        }

        private void ValidateDetailReference(string queryPath, XElement root)
        {
            var info = root.Element("ReportInfo");
            bool detail;
            if (!bool.TryParse((string)info?.Element("IsDetail"), out detail) || !detail) return;
            var reference = ((string)info?.Element("DetailDirectory") ?? "").Replace('\\', '/');
            if (reference.StartsWith("//", StringComparison.Ordinal)) throw new SyncSafetyException("CompanionRejected");
            // Core supports a single leading separator as a reference relative to the LIB root.
            var normalized = ReportWorkDirectory.Normalize(reference.TrimStart('/'));
            var directory = queryPath.LastIndexOf('/') < 0 ? "" : queryPath.Substring(0, queryPath.LastIndexOf('/') + 1);
            if (!groups.Where(g => g.QueryPath.Equals(queryPath, StringComparison.OrdinalIgnoreCase))
                .SelectMany(g => g.LayoutPaths).Any(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    (!reference.StartsWith("/", StringComparison.Ordinal) && p.Equals(directory + normalized, StringComparison.OrdinalIgnoreCase))))
                throw new SyncSafetyException("CompanionRejected");
        }

        internal void ValidateBatch(IDictionary<string, byte[]> files)
        {
            var proposed = DiscoverNewGroups(files);
            var combined = new ReportFilePolicy(groups.Concat(proposed));
            foreach (var file in files) combined.ValidateFile(file.Key, file.Value);
            foreach (var group in combined.groups)
            {
                var members = new[] { group.QueryPath }.Concat(group.LayoutPaths).ToArray();
                if (members.Any(files.ContainsKey) && !members.All(files.ContainsKey))
                    throw new SyncSafetyException("IncompleteReportBatch");
            }
        }

        internal void RegisterBatch(IDictionary<string, byte[]> files)
        {
            foreach (var group in DiscoverNewGroups(files))
            {
                groups.Add(group); Register(group.QueryPath, "ReportQueryInfo");
                foreach (var path in group.LayoutPaths) Register(path, "Spread");
            }
        }

        internal void IncludeImportedGroups(ReportFilePolicy imported)
        {
            foreach (var group in imported.groups)
            {
                if (groups.Any(g => g.QueryPath.Equals(group.QueryPath, StringComparison.OrdinalIgnoreCase))) continue;
                groups.Add(group); Register(group.QueryPath, "ReportQueryInfo");
                foreach (var path in group.LayoutPaths) Register(path, "Spread");
            }
        }

        private List<ReportSyncGroup> DiscoverNewGroups(IDictionary<string, byte[]> files)
        {
            var result = new List<ReportSyncGroup>();
            foreach (var file in files.Where(f => !kinds.ContainsKey(f.Key)))
            {
                XElement root;
                try
                {
                    using (var stream = new MemoryStream(file.Value, false))
                    using (var xml = XmlReader.Create(stream, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                        root = XDocument.Load(xml).Root;
                }
                catch { throw new SyncSafetyException("ReportXmlInvalid"); }
                if (root == null || root.Name != "ReportQueryInfo") continue;
                var group = new ReportSyncGroup { QueryPath = file.Key };
                const string suffix = "查询设置.xml";
                // Same convention as Core; a naming association is still a candidate, never menu evidence.
                if (!file.Key.EndsWith(suffix, StringComparison.Ordinal)) throw new SyncSafetyException("NewReportCompanionUnresolved");
                string main = file.Key.Substring(0, file.Key.Length - suffix.Length) + "报表设置.xml";
                if (!files.ContainsKey(main)) throw new SyncSafetyException("IncompleteReportBatch");
                group.LayoutPaths.Add(main);
                bool detail;
                var info = root.Element("ReportInfo");
                if (bool.TryParse((string)info?.Element("IsDetail"), out detail) && detail)
                {
                    var raw = ((string)info.Element("DetailDirectory") ?? "").Replace('\\', '/');
                    if (raw.StartsWith("//", StringComparison.Ordinal)) throw new SyncSafetyException("CompanionRejected");
                    string reference = ReportWorkDirectory.Normalize(raw.TrimStart('/'));
                    var parent = file.Key.LastIndexOf('/') < 0 ? "" : file.Key.Substring(0, file.Key.LastIndexOf('/') + 1);
                    var candidates = new[] { reference, raw.StartsWith("/", StringComparison.Ordinal) ? reference : parent + reference }
                        .Distinct(StringComparer.OrdinalIgnoreCase).Where(files.ContainsKey).ToArray();
                    if (candidates.Length != 1) throw new SyncSafetyException("CompanionRejected");
                    group.LayoutPaths.Add(candidates[0]);
                }
                result.Add(group);
            }
            return result;
        }
    }
}
