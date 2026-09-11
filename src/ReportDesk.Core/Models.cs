using System;
using System.Collections.Generic;
using System.Data;

namespace ReportDesk.Core;

public sealed class ReportDefinition
{
    public bool ScopedValidation { get; set; }
    public List<string> SharedIssues { get; set; } = new();
    public string AdaptationNote { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string Category { get; set; } = "未分类";
    public string Aliases { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
    public bool Verified { get; set; }
    public DateTime? LastUsed { get; set; }
    public List<ParameterDefinition> Parameters { get; set; } = new();
    public List<QueryDefinition> Queries { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public bool IsDemo { get; set; }
    public string Status => IsDemo ? "模拟演示" : Issues.Count > 0 ?
        (Queries.Exists(q => q.Kind!="ConditionUsing" && ReportReadiness.IssuesFor(this, q).Count == 0) ? "部分可试查 · 仍有待适配" : "待适配") : Verified ? "用户已核对" : "可试查 · 未核对";
}

public sealed class ParameterDefinition
{
    public bool Multiple { get; set; }
    public bool TreeSelect { get; set; }
    public string LookupSourceName { get; set; } = "";
    public string OptionSource { get; set; } = "";
    public List<ParameterOption> Options { get; set; } = new();
    public string AllLabel { get; set; } = "全部";
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "TextBoxType";
    public string Format { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public int AddDays { get; set; }
    public int AddMonths { get; set; }
    public string LookupSql { get; set; } = "";
    public string Dictionary { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public bool HasAll { get; set; }
    public string AllValue { get; set; } = "";
    public bool Implicit { get; set; }
    public bool IsLike { get; set; }
    public string LikeFormat { get; set; } = "%{0}%";
    public bool PadLeft { get; set; }
    public int PadLength { get; set; }
    public string PadCharacter { get; set; } = "0";
}

public sealed class ParameterOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class QueryDefinition
{
    // Optional metadata; old catalogs retain the legacy completion path.
    public string ResultRulesXml { get; set; } = "";
    public List<string> Issues { get; set; } = new();
    public bool IsSumRow { get; set; }
    public string SumColumns { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Sql { get; set; } = "";
    public override string ToString() => Name + (Kind == "DetailReportUsing" ? " · 明细" : " · 查询数据源");
}

public enum ConnectionMode { Direct = 0, Tns = 1 }

public sealed class ConnectionSettings
{
    public string Name { get; set; } = "";
    public ConnectionMode Mode { get; set; }
    public string TnsFile { get; set; } = "";
    public string TnsAlias { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1521;
    public string Service { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    // Legacy serialized fields only. Ignored since 0.1.3; retained for catalog compatibility.
    public int TimeoutSeconds { get; set; }
    public int MaxRows { get; set; }
}

public sealed class Catalog
{
    public List<ReportDefinition> Reports { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
}

public sealed class ImportSummary
{
    public List<ReportDefinition> Reports { get; } = new();
    public List<ReportDefinition> IncompleteReports { get; } = new();
    public List<string> Errors { get; } = new();
    public int Skipped { get; set; }
    public ReportFileInventory? Inventory { get; set; }
    public Dictionary<string, List<RelatedXmlFile>> RelatedFiles { get; } = new();
}

public sealed class QueryResult
{
    public DataTable Table { get; set; } = new();
    public bool Truncated { get; set; }
    public long Milliseconds { get; set; }
    public bool Demo { get; set; }
}
