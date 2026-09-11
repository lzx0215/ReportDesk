using System.Collections.Generic;
using System.Linq;

namespace ReportDesk.Core;

public static class ReportReadiness
{
    // Old catalogs have no scoped evidence: retain the original whole-report guard.
    public static List<string> IssuesFor(ReportDefinition report, QueryDefinition query) =>
        !report.ScopedValidation ? report.Issues.ToList() :
        (report.SharedIssues ?? new()).Concat(query.Issues ?? new()).Distinct().ToList();
}
