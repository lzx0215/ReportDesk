namespace ReportDesk.Core;

public sealed class AdaptationAdvice
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
}

public static class AdaptationGuidance
{
    public static AdaptationAdvice For(string message)
    {
        AdaptationAdvice A(string code, string title, string action) => new() { Code = code, Title = title, Action = action };
        if (message.Contains("（IsCross）")) return A("cross", "交叉统计", "开发处理：复现原引擎的行键、列键、指标及空值规则，再以代表报表核对交叉表；普通 SQL 明细不能代替此结果。");
        if (message.Contains("（CrossCombinColumns）")) return A("cross-columns", "组合列计算", "开发处理：解析配置中的组合列公式，核对计算顺序、类型与缺列行为，并用原引擎结果验证。");
        if (message.Contains("（SumRows）")) return A("sum-rows", "横向合计列", "按原引擎的列选择和表达式计算每一行的合计；与底部合计行分别核对。");
        if (message.Contains("（RowGroup）")) return A("row-group", "行分组小计", "开发处理：核对分组字段、小计位置、标签、数值类型以及重复组的处理，再实现分组结果。");
        if (message.Contains("树形多选")) return A("tree-multi", "树形多选条件", "开发处理：复现勾选节点及全部节点的展开规则，为每个编码独立绑定并适配 IN 条件；不需要逐张重新导入 XML 来解决。");
        if (message.Contains("树形条件")) return A("tree", "树形节点及全部选项", "开发处理：普通节点返回自身编码，但全部选项可能展开为多个编码；须根据原引擎支持节点展开及参数绑定。");
        if (message.Contains("属性映射")) return A("dependency", "数据源或控件属性依赖", "开发处理：查明引用的是控件值、显示名还是前置查询的行列，建立有顺序的依赖及类型检查；不能把整个引用当作一个文本参数。");
        if (message.Contains("启用了") || message.Contains("属性映射")) return A("mapping", "交叉/合计/依赖映射", "纯 AddMap 结果映射已支持。此报表还含交叉、列合计、行分组或数据源属性依赖，需要实现这些数据转换并对照 HIS 同条件结果；不能直接取消拦截。");
        if (message.Contains("字典/自定义选项")) return A("dictionary", "字典或自定义选项", "根据控件的字典标识查找字典数据或 HIS 取值实现，确认 ID、显示名、全部值和筛选范围后添加选项适配；不要把显示名当作编码。");
        if (message.Contains("自定义选项")) return A("dictionary", "字典或自定义选项", "静态选项已支持，但此定义的选项集合为空或缺少 ID/Name；需核对原配置，不能编造选项编码。");
        if (message.Contains("使用尚未适配的控件")) return A("control", "尚未支持的控件", "核对 TreeView、CheckBox 或自定义控件的取值及联动逻辑，再实现对应输入控件和绑定；不能统一替换成文本框。");
        if (message.Contains("多选逻辑")) return A("multi", "多选参数", "明确多选值及全部值含义，为每个值生成独立绑定参数，核对 IN/组合条件；禁止拼接用户值。");
        if (message.Contains("禁用控件")) return A("disabled", "禁用控件的上下文", "核对 HIS 对禁用控件注入的科室、人员或固定值。只有取得明确来源后才能传参，不启用为空白可编辑输入。");
        if (message.Contains("默认数据源")) return A("default", "默认值及上下文", "查明默认数据源来自当前患者、科室、人员还是其他控件；实现明确的上下文输入/联动并保留原过滤条件。");
        if (message.Contains("执行顺序/用途")) return A("source", "数据源用途和执行顺序", "核对 ConditionUsing 等数据源与主查询的依赖、输入输出和执行时机，再实现依赖执行；不能当作独立主表查询。");
        if (message.Contains("分组数据源")) return A("group", "分组查询", "核对分组 SQL、分组条件与主查询之间的参数传递及汇总口径，适配后对照 HIS 的分组结果。");
        if (message.Contains("参数名为空或重复")) return A("parameter", "参数定义冲突", "核对原 XML 的参数名、控件名及 SQL 引用，区分重复定义和真实联动；取得一致定义后重新导入。");
        if (message.Contains("没有查询 SQL")) return A("incomplete", "仅条件/不完整查询定义", "查找包含 QueryDataSource/Sql 的完整定义，或提供 HIS 窗口的数据获取实现。这类文件单列保留，不显示为独立报表。");
        if (message.Contains("发现全角括号")) return A("sql-parentheses", "SQL 中文括号", "按提示行列核对括号。在修正副本中只修改该标点后重新导入；不要替换字符串、列名或注释中的中文符号。已核对的危重抢救报表版本由程序修正导入副本，原文件保留。");
        if (message.Contains("发现原生绑定变量")) return A("sql-bind", "SQL 原生参数尚未适配", "开发处理：按提示行列查明冒号参数的来源和类型，再实现绑定；不能将参数值直接拼入 SQL，也不能直接删除冒号。");
        if (message.Contains("发现数据库链接")) return A("sql-link", "SQL 跨库链接尚未适配", "核对 @ 后的链接、目标数据库及现场账号权限；不能直接删除链接或改为当前库的同名表。");
        if (message.Contains("发现语句中间的分号")) return A("sql-statements", "SQL 含语句分隔符", "开发处理：核对是否包含多条独立语句或前置处理；不能简单删掉中间分号后执行。");
        if (message.Contains("只支持查询") || message.Contains("只允许 SELECT")) return A("read-only", "只读关键字检查（可能误拦截）", "先核对语句上下文。TRUNCATE 也可能来自合法的 LISTAGG ON OVERFLOW TRUNCATE，需要定点扩展解析器；真正的写入或临时表操作需要另行确认只读方案。不能直接移除关键字防护。");
        if (message.Contains("SQL") || message.Contains("多语句") || message.Contains("模板语法") || message.Contains("引用字符串") || message.Contains("标识符")) return A("sql", "SQL/模板语法", "定位具体的数据源和语法：多语句、原生绑定、数据库链接、全角括号或动态片段需分别处理。核对原定义后扩展解析或提供已确认的只读版本，禁止简单替换拼接。");
        if (message.Contains("主表/明细版式")) return A("companions", "已适配版本的配套文件不符", "将此版本的查询、主版式、明细版式放在同一目录重新导入；内容必须与核对版本一致。");
        return A("unknown", "其他待核对内容", "保留原提示并检查对应 XML 和 HIS 实现，证据不足时继续待适配。");
    }
}
