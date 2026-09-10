using System;
using System.Collections.Generic;
using System.Data;

namespace ReportDesk.Core;

public static class DemoData
{
    public static ReportDefinition Report() => new()
    {
        Id = "built-in-demo", Name = "体验查询流程（模拟数据）", Category = "演示", IsDemo = true,
        Notes = "完全本地生成的虚构数据，不连接数据库。用于体验条件、筛选、排序和导出。",
        Parameters = new List<ParameterDefinition>
        {
            new() { Name = "begin", Label = "开始日期", Kind = "DateTimeType", Format = "yyyy-MM-dd", AddDays = -6 },
            new() { Name = "end", Label = "结束日期", Kind = "DateTimeType", Format = "yyyy-MM-dd" },
            new() { Name = "department", Label = "科室关键词（可空）" }
        },
        Queries = new List<QueryDefinition> { new() { Name = "模拟明细", Kind = "MainReportUsing", Sql = "select '&begin' 开始日期, '&end' 结束日期, '&department' 科室 from dual" } }
    };

    public static QueryResult Execute(IReadOnlyDictionary<string, string> values, System.Threading.CancellationToken cancellation = default)
    {
        var result = new QueryResult { Demo = true };
        var start = DateTime.ParseExact(values["begin"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(values["end"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (start > end) throw new InvalidOperationException("开始日期不能晚于结束日期。");
        var table = result.Table;
        table.Columns.Add("日期", typeof(DateTime)); table.Columns.Add("科室", typeof(string));
        table.Columns.Add("模拟编号", typeof(string)); table.Columns.Add("数量", typeof(int)); table.Columns.Add("金额", typeof(decimal));
        for (var offset = 0; offset <= (end - start).Days; offset++)
        {
            cancellation.ThrowIfCancellationRequested(); var day = start.AddDays(offset);
            foreach (var dept in new[] { "演示科室 A", "演示科室 B" })
                if (dept.IndexOf(values["department"], StringComparison.OrdinalIgnoreCase) >= 0)
                    table.Rows.Add(day, dept, "000" + (table.Rows.Count + 1).ToString("D6"), table.Rows.Count % 8 + 1, 12.50m * (table.Rows.Count % 8 + 1));
        }
        return result;
    }
}
