using System;
using System.Collections.Generic;
using System.Linq;

namespace ReportDesk.Core;

// Rules traced through FS.Core.UI ucCommonWindow and FS.Manager BLL/DAL, 2026-09-11.
// These are option queries only. The report SQL and its department restrictions stay intact.
public static class ParameterOptions
{
    public static bool IsDictionary(ParameterDefinition p) =>
        p.OptionSource == "Dictionary" && !string.IsNullOrWhiteSpace(p.Dictionary) ||
        p.OptionSource == "DepartmentType" && new[] { "ALL", "C", "I", "F", "L", "PI", "T", "O", "D", "P", "N", "OP", "U", "WZ", "UC", "UL" }.Contains(p.Dictionary) ||
        p.OptionSource == "EmployeeType" && new[] { "ALL", "O", "D", "N", "F", "P", "T", "C" }.Contains(p.Dictionary);

    public static string LookupSql(ParameterDefinition p)
    {
        if (!IsDictionary(p)) return p.LookupSql;
        if (p.OptionSource == "Dictionary")
            // QueryConst calls QueryList(type), which does NOT add VALID_FLAG.
            return "select DICTIONARY_ID, DICTIONARY_NAME from COM_BD_DICTIONARY where DICTIONARY_TYPE = '&lookupType'";
        if (p.OptionSource == "DepartmentType")
            return "select DEPARTMENT_ID, DEPARTMENT_NAME from COM_BD_DEPARTMENT where VALID_FLAG = '1'" +
                (p.Dictionary == "ALL" ? "" : " and BUSINESS_FLAG = '1' and DEPARTMENT_TYPE = '&lookupType'");
        return "select EMPLOYEE_ID, EMPLOYEE_NAME from COM_BD_EMPLOYEE where VALID_FLAG = '1'" +
            (p.Dictionary == "ALL" ? "" : " and EMPLOYEE_TYPE = '&lookupType'");
    }

    public static Dictionary<string, string> LookupValues(ParameterDefinition p) =>
        new(StringComparer.OrdinalIgnoreCase) { ["lookupType"] = p.Dictionary };

    public static string Initial(ParameterDefinition p) => p.Kind == "CheckBoxType" ?
        (string.Equals(p.DefaultValue, "true", StringComparison.OrdinalIgnoreCase) ? "True" : "False") :
        p.Kind == "TextBoxType" && p.OptionSource == "Custom" ? p.DefaultValue : "";

    public static string Validate(ParameterDefinition p, string value)
    {
        if(p.Kind=="RegisterIdType" && (!long.TryParse(value,out var registerId) || registerId<=0))
            throw new InvalidOperationException("请输入已确认的住院流水号 RegisterID；不能填写住院号或姓名。");
        if (p.Kind == "CheckBoxType" && value != "True" && value != "False")
            throw new InvalidOperationException("复选框值无效：" + p.Label);
        if (p.Kind == "ComboBoxType" && p.OptionSource == "Custom" &&
            !(p.HasAll && value == p.AllValue) && !(p.Options?.Any(o => o.Value == value) ?? false))
            throw new InvalidOperationException("请选择配置中的选项：" + p.Label);
        return value;
    }
}
