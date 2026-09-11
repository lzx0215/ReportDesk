using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ReportDesk.Core;

public static class TabularReportAdapter
{
    public const string Notice = "按原定义处理查询结果、交叉及合计分组；主表与明细分别查询。未复刻 HIS 打印版式与版式公式，实际结果需在内网与 HIS 同条件核对。";
    public static bool HasTransform(XElement source) =>
        (bool.TryParse(source.Element("IsCross")?.Value, out var cross) && cross) ||
        new[] { "CrossCombinColumns", "SumRows", "RowGroup" }.Any(n => !string.IsNullOrWhiteSpace(source.Element(n)?.Value));

    public static IEnumerable<string> TransformationIssues(XElement source)
    {
        if (bool.TryParse(source.Element("IsCross")?.Value, out var cross) && cross)
            yield return "交叉统计待适配（IsCross）：需要按原定义将数据转为交叉表。";
        if (!string.IsNullOrWhiteSpace(source.Element("CrossCombinColumns")?.Value))
            yield return "组合列计算待适配（CrossCombinColumns）：需要处理交叉列之间的计算。";
        if (!string.IsNullOrWhiteSpace(source.Element("SumRows")?.Value))
            yield return "横向合计列待适配（SumRows）：需要按指定列及公式计算每行合计。";
        if (!string.IsNullOrWhiteSpace(source.Element("RowGroup")?.Value))
            yield return "行分组小计待适配（RowGroup）：需要按分组字段插入小计及合计。";
    }

    // ucMainFpReport.RowGroup's non-grouped branch: append a label plus decimal sums only.
    // Do not sum integer IDs, dates or strings; all-null decimal columns remain DBNull.
    public static void Complete(DataTable table, QueryDefinition query, CancellationToken token = default)
    {
        if (!query.IsSumRow || string.IsNullOrEmpty(query.SumColumns) || !table.Columns.Contains(query.SumColumns)) return;
        var marker = table.Columns[query.SumColumns]!;
        if (marker.DataType != typeof(string)) throw new InvalidOperationException("合计标签列不是文本类型，需核对报表定义。");
        var total = table.NewRow(); total[marker] = "合计：";
        foreach (DataColumn column in table.Columns)
        {
            if (column.DataType != typeof(decimal)) continue;
            decimal sum = 0; var any = false;
            foreach (DataRow row in table.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.IsNull(column)) continue;
                sum = checked(sum + (decimal)row[column]); any = true;
            }
            total[column] = any ? (object)sum : DBNull.Value;
        }
        token.ThrowIfCancellationRequested(); table.Rows.Add(total);
    }

    // Compound Map references are not scalar parameters. Never turn &dt.Rows[0] into &dt.
    public static bool HasMapReference(string sql) => Regex.IsMatch(sql, @"&[\p{L}_][\p{L}\p{Nd}_]*[.\[]");
}
