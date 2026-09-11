// Adapted from the supplied FS.Core.UI.dll, SHA256 49E74C98EC905E05AB20EA6507FF78CAEB4C2FD786C7E45F867CD191CA9E98C7.
// Retains branch-specific HIS calculations. No HIS binaries are loaded at runtime.
#nullable disable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
namespace ReportDesk.Core;
public sealed class HisResultRules
{
    private readonly CancellationToken token;
    private HisResultRules(CancellationToken cancellation) { token = cancellation; }
    public static IEnumerable<string> Validate(XElement source)
    {
        var q = Read(source);
        if(q.IsCross && (Split(q.CrossRows).Length == 0 || Split(q.CrossColumns).Length != 1 || Split(q.CrossValues).Length != 1))
            yield return "交叉统计待适配（IsCross）：当前支持一个交叉列字段和一个指标；原引擎的多指标来源映射分支需另行核对。";
        foreach(var g in q.RowGroup)
        {
            if(g.GroupCondition.Length == 0) yield return "行分组小计待适配（RowGroup）：分组字段为空。";
        }
    }
    public static void Complete(QueryResult result, QueryDefinition query, CancellationToken token, bool displayRows = true)
    {
        if(string.IsNullOrEmpty(query.ResultRulesXml)) { TabularReportAdapter.Complete(result.Table,query,token); return; }
        var definition = XElement.Parse(query.ResultRulesXml);
        var unsupported = Validate(definition).ToArray();
        if(unsupported.Length > 0) throw new InvalidOperationException(string.Join("；", unsupported));
        var common = Read(definition);
        var engine = new HisResultRules(token);
        var original = result.Table;
        DataTable table = original;
        try
        {
            if(common.IsCross) { object[,] provenance = null; table = engine.queryCross(original,common,null,ref provenance); }
            else engine.columnGroup(table,common);
            var rows = displayRows ? engine.RowGroup(table,common) : new Dictionary<int,DataRow>();
            foreach(var row in rows.OrderBy(x=>x.Key)) { token.ThrowIfCancellationRequested(); table.Rows.InsertAt(row.Value,row.Key); }
            result.Table = table;
            if(!ReferenceEquals(table,original)) original.Dispose();
        }
        catch(OperationCanceledException) { if(!ReferenceEquals(table,original)) table.Dispose(); throw; }
        catch(Exception ex)
        {
            if(!ReferenceEquals(table,original)) table.Dispose();
            ErrorLog.Write("HisResultRules",ex,includeMessage:false);
            throw new InvalidOperationException("数据源 " + query.Name + " 的交叉/合计/分组计算失败（" + ex.GetType().Name + "）。请核对 XML 字段与实际结果类型；未返回简化结果。");
        }
    }
    static string[] Split(string value) => value.Split(new[]{'|'},StringSplitOptions.RemoveEmptyEntries);
    static QueryDataSource Read(XElement n) => new QueryDataSource {
        IsCross=(string)n.Element("IsCross")=="true", CrossRows=(string)n.Element("CrossRows")??"", CrossColumns=(string)n.Element("CrossColumns")??"", CrossValues=(string)n.Element("CrossValues")??"",
        CrossCombinColumns=(string)n.Element("CrossCombinColumns")??"", CrossGroupColumns=(string)n.Element("CrossGroupColumns")??"", SumRows=(string)n.Element("SumRows")??"", SumColumns=(string)n.Element("SumColumns")??"", IsSumRow=(string)n.Element("IsSumRow")!="false",
        RowGroup=(n.Element("RowGroup")?.Elements("RowGroup")??Enumerable.Empty<XElement>()).Select(g=>new RowGroupInfo {
            GroupCondition=Split((string)g.Element("GroupConditionStr")??""), GroupDependColumn=(string)g.Element("GroupDependColumn")??"", CustomShowInfo=(string)g.Element("CustomShowInfo")??"",
            ShowInfoType=(EnumGroupShowInfoType)Enum.Parse(typeof(EnumGroupShowInfoType),(string)g.Element("ShowInfoType")??"GroupColumn"), GroupShowLocation=(EnumGroupShowLocation)Enum.Parse(typeof(EnumGroupShowLocation),(string)g.Element("GroupShowLocation")??"Header") }).ToList()
    };
    sealed class QueryDataSource { public bool IsCross, IsSumRow; public string CrossRows,CrossColumns,CrossValues,CrossCombinColumns,CrossGroupColumns,SumRows,SumColumns; public List<RowGroupInfo> RowGroup; }
    sealed class RowGroupInfo { public string[] GroupCondition; public string GroupDependColumn,CustomShowInfo; public EnumGroupShowInfoType ShowInfoType; public EnumGroupShowLocation GroupShowLocation; }
    enum EnumGroupShowInfoType { Custom, CustomAndGroupColumn, GroupColumn, GroupColumnAndCustom }
    enum EnumGroupShowLocation { Header, Footer }
private DataTable queryCross(DataTable dt, QueryDataSource common, Dictionary<string, object> map, ref object[,] crossSource)
	{
		var dataSetHelper = this;
		string[] array = common.CrossRows.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0)
		{
			throw new Exception("交叉报表没有设置交叉行，请设置！");
		}
		DataTable dataTable = dataSetHelper.SelectDistinctByIndexs("dtCrossRows", dt, array);
		string[] array2 = common.CrossColumns.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		if (array2.Length == 0)
		{
			throw new Exception("交叉报表没有设置交叉列，请设置！");
		}
		DataTable dataTable2 = dataSetHelper.SelectDistinctByIndexs("dtCrossColumns", dt, array2);
		string[] array3 = common.CrossValues.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		if (array3.Length == 0)
		{
			throw new Exception("交叉报表没有设置交叉值，请设置！");
		}
		DataTable dataTable3 = new DataTable();
		dataTable3 = new DataTable();
		string[] array4 = common.CrossCombinColumns.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		string[] array5 = common.CrossGroupColumns.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (DataColumn column2 in dataTable.Columns)
		{
            token.ThrowIfCancellationRequested();
			dataTable3.Columns.Add(column2.ColumnName, column2.DataType);
		}
		foreach (DataRow row in dataTable2.Rows)
		{
            token.ThrowIfCancellationRequested();
			foreach (DataColumn column3 in dataTable2.Columns)
			{
            token.ThrowIfCancellationRequested();
				if (array3.Length == 1)
				{
					dataTable3.Columns.Add(new DataColumn(row[column3].ToString(), dt.Columns[array3[0]].DataType));
					continue;
				}
				string[] array6 = array3;
				foreach (string text in array6)
				{
            token.ThrowIfCancellationRequested();
					dataTable3.Columns.Add(new DataColumn(row[column3].ToString() + text, dt.Columns[text].DataType));
				}
			}
		}
		columnGroup(dataTable3, common);
		crossSource = new object[dataTable.Rows.Count, dataTable3.Columns.Count];
		StringBuilder stringBuilder = null;
		StringBuilder stringBuilder2 = null;
		foreach (DataRow row2 in dataTable.Rows)
		{
            token.ThrowIfCancellationRequested();
			DataRow dataRow3 = dataTable3.NewRow();
			foreach (DataColumn column4 in dataTable.Columns)
			{
            token.ThrowIfCancellationRequested();
				dataRow3[column4.ColumnName] = row2[column4.ColumnName];
			}
			stringBuilder = new StringBuilder("1=1");
			string[] array7 = array;
			foreach (string text2 in array7)
			{
            token.ThrowIfCancellationRequested();
				stringBuilder = dt.Columns[text2].DataType.ToString() switch
				{
					"System.Decimal" => stringBuilder.Append(" AND ").Append(dt.Columns[text2].Caption).Append(" = ")
						.Append(row2[text2].ToString()),
					"System.DateTime" => stringBuilder.Append(" AND ").Append(dt.Columns[text2].Caption).Append(" = #")
						.Append(row2[text2].ToString())
						.Append("# "),
					_ => stringBuilder.Append(" AND ").Append(dt.Columns[text2].Caption).Append(" = '")
						.Append(row2[text2].ToString())
						.Append("'"),
				};
			}
			if (array5.Length != 0)
			{
			}
			foreach (DataRow row3 in dataTable2.Rows)
			{
            token.ThrowIfCancellationRequested();
				stringBuilder2 = new StringBuilder(stringBuilder.ToString());
				foreach (DataColumn column5 in dataTable2.Columns)
				{
            token.ThrowIfCancellationRequested();
					switch (dt.Columns[column5.ColumnName].DataType.ToString())
					{
					case "System.Decimal":
						stringBuilder2.Append(" AND ").Append(dt.Columns[column5.ColumnName].Caption).Append(" = ")
							.Append(row3[column5].ToString());
						break;
					case "System.DateTime":
						stringBuilder2.Append(" AND ").Append(dt.Columns[column5.ColumnName].Caption).Append(" = #")
							.Append(row3[column5])
							.Append("#");
						break;
					default:
						stringBuilder2.Append(" AND ").Append(dt.Columns[column5.ColumnName].Caption).Append(" = '")
							.Append(row3[column5])
							.Append("'");
						break;
					}
					DataRow[] array8 = dt.Select(stringBuilder2.ToString());
					string[] array9 = new string[array3.Length];
					int num = 0;
					string[] array10 = array3;
					foreach (string text3 in array10)
					{
            token.ThrowIfCancellationRequested();
						num = Array.IndexOf(array3, text3);
						array9[num] = "0";
						if (array8.Length != 0)
						{
							DataRow[] array11 = array8;
							foreach (DataRow dataRow5 in array11)
							{
            token.ThrowIfCancellationRequested();
								switch (dataRow5.Table.Columns[text3].DataType.ToString())
								{
								case "System.Decimal":
									array9[num] = (decimal.Parse(array9[num]) + decimal.Parse(dataRow5[text3].ToString())).ToString();
									break;
								case "System.DateTime":
									array9[num] = DateTime.Parse(dataRow5[text3].ToString()).ToString();
									break;
								case "System.String":
									array9[num] = dataRow5[text3].ToString();
									break;
								default:
									array9[num] = (int.Parse(array9[num]) + int.Parse(dataRow5[text3].ToString())).ToString();
									break;
								}
							}
						}
						if (array3.Length == 1)
						{
							dataRow3[row3[column5].ToString()] = array9[num];
							crossSource[dataTable3.Rows.Count, dataTable3.Columns[row3[column5].ToString()].Ordinal] = array8;
						}
						else
						{
							dataRow3[row3[column5].ToString() + text3] = array9[num];
							crossSource[dataTable3.Rows.Count, dataTable3.Columns[row3[column5].ToString()].Ordinal] = array8;
						}
					}
				}
			}
			dataTable3.Rows.Add(dataRow3);
		}
		if (dataTable3.Rows.Count > 0)
		{
		}
		if (common.IsSumRow)
		{
		}
		return dataTable3;
	}
private void columnGroup(DataTable dt, QueryDataSource common)
	{
		if (!string.IsNullOrEmpty(common.CrossCombinColumns))
		{
			string[] array = common.CrossCombinColumns.Split(new string[1] { "|" }, StringSplitOptions.RemoveEmptyEntries);
			string[] array2 = array;
			foreach (string text in array2)
			{
            token.ThrowIfCancellationRequested();
				string[] array3 = text.Split(new string[1] { ":" }, StringSplitOptions.RemoveEmptyEntries);
				if (array3.Length <= 1)
				{
					continue;
				}
				string text2 = "0";
				string[] array4 = array3[1].Split(new string[1] { "," }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string text3 in array4)
				{
            token.ThrowIfCancellationRequested();
					if (dt.Columns.Contains(text3))
					{
						dt.Columns[text3].SetOrdinal(dt.Columns.Count - 1);
						dt.Columns[text3].Caption = array3[0];
						text2 = text2 + "+([" + text3 + "])";
					}
				}
				if (common.SumRows.Contains(array3[0]))
				{
					DataColumn dataColumn = new DataColumn(array3[0] + "合计");
					dataColumn.Caption = array3[0];
					dataColumn.DataType = dt.Columns[dt.Columns.Count - 1].DataType;
					dataColumn.Expression = text2;
					dt.Columns.Add(dataColumn);
				}
			}
		}
		if (string.IsNullOrEmpty(common.SumRows))
		{
			return;
		}
		string[] array5 = common.SumRows.Split(new string[1] { "|" }, StringSplitOptions.RemoveEmptyEntries);
		string[] array6 = array5;
		foreach (string text4 in array6)
		{
            token.ThrowIfCancellationRequested();
			string[] array7 = text4.Split(new string[1] { ":" }, StringSplitOptions.RemoveEmptyEntries);
			if (array7.Length > 1)
			{
				string text5 = "0";
				Type dataType = typeof(decimal);
				string[] array8 = array7[1].Split(new string[1] { "," }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string text6 in array8)
				{
            token.ThrowIfCancellationRequested();
					if (dt.Columns.Contains(text6))
					{
						dataType = dt.Columns[text6].DataType;
						text5 = text5 + "+(" + text6 + ")";
					}
				}
				DataColumn dataColumn2 = new DataColumn(array7[0]);
				dataColumn2.Caption = array7[0];
				dataColumn2.DataType = dataType;
				dataColumn2.Expression = text5;
				dt.Columns.Add(dataColumn2);
				continue;
			}
			string text7 = "0";
			Type typeFromHandle = typeof(decimal);
			foreach (DataColumn column in dt.Columns)
			{
            token.ThrowIfCancellationRequested();
				if (column.DataType.Name == typeFromHandle.Name)
				{
					text7 = text7 + "+(" + column.ColumnName + ")";
				}
			}
			DataColumn dataColumn4 = new DataColumn(array7[0]);
			dataColumn4.Caption = array7[0];
			dataColumn4.DataType = typeFromHandle;
			dataColumn4.Expression = text7;
			dt.Columns.Add(dataColumn4);
		}
	}
private Dictionary<int, DataRow> RowGroup(DataTable dt, QueryDataSource common)
	{
		Dictionary<int, DataRow> dictionary = new Dictionary<int, DataRow>();
		if (common.RowGroup != null && common.RowGroup.Count > 0)
		{
			string[] groupCondition = common.RowGroup[0].GroupCondition;
			string text = string.Empty;
			string filter = "1=1";
			int i = 0;
			int num = 0;
			for (; i < dt.Rows.Count; i++)
			{
                token.ThrowIfCancellationRequested();
				DataRow dataRow = dt.Rows[i];
				string text2 = string.Empty;
				string text3 = "1=1";
				string[] array = groupCondition;
				foreach (string text4 in array)
				{
            token.ThrowIfCancellationRequested();
					text2 += dataRow[text4].ToString();
					text3 = text3 + " AND " + text4 + " = '" + dataRow[text4].ToString() + "'";
				}
				if (string.Empty.Equals(text))
				{
					text = text2;
					filter = text3;
				}
				if (text.Equals(text2))
				{
					continue;
				}
				DataRow dataRow2 = dt.NewRow();
				string text5 = string.Empty;
				string[] array2 = groupCondition;
				foreach (string columnName in array2)
				{
            token.ThrowIfCancellationRequested();
					text5 += dt.Rows[i - 1][columnName].ToString();
				}
				GetGroupShowInfoAndLocation(dataRow2, common.RowGroup[0], text5, "小计：");
				foreach (DataColumn column in dt.Columns)
				{
            token.ThrowIfCancellationRequested();
					if (column.DataType.IsValueType)
					{
						string text6 = "[" + column.ColumnName + "]";
						dataRow2[column.ColumnName] = dt.Compute("sum(" + text6 + ")", filter);
					}
				}
				if (common.RowGroup[0].GroupShowLocation == EnumGroupShowLocation.Footer)
				{
					dictionary.Add(i + dictionary.Count, dataRow2);
				}
				else if (common.RowGroup[0].GroupShowLocation == EnumGroupShowLocation.Header)
				{
					dictionary.Add(num + dictionary.Count, dataRow2);
				}
				num = i;
				text = text2;
				filter = text3;
			}
			if (dt.Rows.Count > 0)
			{
				DataRow dataRow3 = dt.Rows[dt.Rows.Count - 1];
				if (groupCondition.Length != 0)
				{
					dt.AcceptChanges();
					DataRow dataRow4 = dt.NewRow();
					string text7 = string.Empty;
					string[] array3 = groupCondition;
					foreach (string columnName2 in array3)
					{
            token.ThrowIfCancellationRequested();
						text7 += dataRow3[columnName2].ToString();
					}
					GetGroupShowInfoAndLocation(dataRow4, common.RowGroup[0], text7, "小计：");
					foreach (DataColumn column2 in dt.Columns)
					{
            token.ThrowIfCancellationRequested();
						if (column2.DataType.IsValueType && column2.DataType == typeof(decimal))
						{
							string text8 = "[" + column2.ColumnName + "]";
							dataRow4[column2.ColumnName] = dt.Compute("sum(" + text8 + ")", filter);
						}
					}
					if (common.RowGroup[0].GroupShowLocation == EnumGroupShowLocation.Footer)
					{
						dictionary.Add(dt.Rows.Count + dictionary.Count, dataRow4);
					}
					else if (common.RowGroup[0].GroupShowLocation == EnumGroupShowLocation.Header)
					{
						dictionary.Add(num + dictionary.Count, dataRow4);
					}
				}
			}
			if (common.IsSumRow)
			{
				DataRow dataRow5 = dt.NewRow();
				if (!string.IsNullOrEmpty(common.SumColumns) && dt.Columns.Contains(common.SumColumns))
				{
					dataRow5[common.SumColumns] = "合计：";
				}
				else
				{
					dataRow5[0] = "合计：";
				}
				foreach (DataColumn column3 in dt.Columns)
				{
            token.ThrowIfCancellationRequested();
					if (column3.DataType.IsValueType)
					{
						string text9 = "[" + column3.ColumnName + "]";
						dataRow5[column3.ColumnName] = dt.Compute("sum(" + text9 + ")", "");
					}
				}
				dictionary.Add(dt.Rows.Count + dictionary.Count, dataRow5);
			}
		}
		else if (common.IsSumRow && !string.IsNullOrEmpty(common.SumColumns) && dt.Columns.Contains(common.SumColumns))
		{
			DataRow dataRow6 = dt.NewRow();
			dataRow6[common.SumColumns] = "合计：";
			foreach (DataColumn column4 in dt.Columns)
			{
            token.ThrowIfCancellationRequested();
				if (column4.DataType.IsValueType && column4.DataType == typeof(decimal))
				{
					dataRow6[column4.ColumnName] = dt.Compute("sum(" + column4.ColumnName + ")", "");
				}
			}
			dictionary.Add(dt.Rows.Count + dictionary.Count, dataRow6);
		}
		return dictionary;
	}
private void GetGroupShowInfoAndLocation(DataRow dr, RowGroupInfo rowGroup, string value, string defaulValue)
	{
		string value2 = value;
		switch (rowGroup.ShowInfoType)
		{
		case EnumGroupShowInfoType.Custom:
			value2 = rowGroup.CustomShowInfo;
			break;
		case EnumGroupShowInfoType.CustomAndGroupColumn:
			value2 = rowGroup.CustomShowInfo + value;
			break;
		case EnumGroupShowInfoType.GroupColumn:
			value2 = value;
			break;
		case EnumGroupShowInfoType.GroupColumnAndCustom:
			value2 = value + rowGroup.CustomShowInfo;
			break;
		}
		if (string.IsNullOrEmpty(value2))
		{
			value2 = defaulValue;
		}
		string[] array = rowGroup.GroupDependColumn.Split(new char[1] { '|' }, StringSplitOptions.RemoveEmptyEntries);
		string[] array2 = array;
		foreach (string text in array2)
		{
            token.ThrowIfCancellationRequested();
			if (dr.Table.Columns.Contains(text))
			{
				dr[text] = value2;
			}
			else
			{
				dr[rowGroup.GroupCondition[0]] = value2;
			}
		}
	}
public DataTable SelectDistinctByIndexs(string tableName, DataTable sourceTable, string[] fieldIndexs)
	{
		DataTable dataTable = new DataTable(tableName);
		object[] array = new object[fieldIndexs.Length];
		string text = "";
		for (int num = 0; num < fieldIndexs.Length; num++)
		{
			dataTable.Columns.Add(sourceTable.Columns[fieldIndexs[num]].Caption, sourceTable.Columns[fieldIndexs[num]].DataType);
			text = text + sourceTable.Columns[fieldIndexs[num]].Caption + ",";
		}
		text = text.Remove(text.Length - 1, 1);
		DataRow dataRow = null;
		DataRow[] array2 = sourceTable.Select("", text);
		foreach (DataRow dataRow2 in array2)
		{
            token.ThrowIfCancellationRequested();
			if (dataRow == null || !RowEqual(dataRow, dataRow2, dataTable.Columns))
			{
				dataRow = dataRow2;
				for (int num3 = 0; num3 < fieldIndexs.Length; num3++)
				{
					array[num3] = dataRow2[fieldIndexs[num3]];
				}
				dataTable.Rows.Add(array);
			}
		}
		return dataTable;
	}
    private bool RowEqual(DataRow a, DataRow b, DataColumnCollection columns) => columns.Cast<DataColumn>().All(c=>Equals(a[c.ColumnName],b[c.ColumnName]));
}
