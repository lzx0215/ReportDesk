using System;
using System.Collections.Generic;
using System.Data;

namespace ReportDesk.Core;

public sealed class ReportDefinition
{
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
    public string Status => IsDemo ? "模拟演示" : Issues.Count > 0 ? "待适配" : Verified ? "用户已核对" : "可试查 · 未核对";
}

public sealed class ParameterDefinition
{
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

public sealed class QueryDefinition
{
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
    public List<string> Errors { get; } = new();
    public int Skipped { get; set; }
}

public sealed class QueryResult
{
    public DataTable Table { get; set; } = new();
    public bool Truncated { get; set; }
    public long Milliseconds { get; set; }
    public bool Demo { get; set; }
}
