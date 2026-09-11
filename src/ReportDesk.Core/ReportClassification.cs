using System.Linq;

namespace ReportDesk.Core;

public static class ReportClassification
{
    // A condition-only HIS configuration is not a standalone query. Keep the original
    // catalog entry for compatibility; every entry point filters through this predicate.
    public static bool IsStandalone(ReportDefinition report) => report.IsDemo ||
        report.Queries.Any(q => !string.IsNullOrWhiteSpace(q.Sql));
}
