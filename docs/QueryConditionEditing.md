# 查询条件在哪里修改

目前「查询条件」页面只能填写已有条件，没有新增或编辑条件定义的图形界面。「SQL 编辑」只改 SQL；「同步报表列」只处理结果列，不生成筛选控件。

## 配置位置

以 `(病案)观察室工作日志` 为例，配置在 `E:\his\LIB\Config\Xml\(病案)观察室工作日志查询设置.xml`，不是同名的「报表设置.xml」。可以从 SQL 编辑的「打开原报表位置」找到文件，用 XML 编辑器打开。

| 要修改什么 | XML 位置 |
| --- | --- |
| 条件名称、类型、选项、日期格式 | `ReportQueryInfo / List / List / ControlType`（名称 `Name` 和文字 `Text` 在外层 `List`） |
| 条件实际怎样筛选数据 | `ReportQueryInfo / QueryDataSource / QueryDataSource / Sql` |
| 结果列宽、表头 | 另一个「报表设置.xml」，可用「同步报表列」处理 |

## 新增科室筛选

需要同时配置控件和 SQL，不能只加一个按钮。建议使用科室下拉选择框。

下列是现有导入器支持的主要字段示例，不是可直接替换整个 HIS 查询文件的完整 XML。正式修改时应参考同一 LIB 已有的科室控件，保留 HIS 所需的其他序列化、尺寸和位置属性。先在副本验证，再确认覆盖目标文件。

```xml
<List Type="FS.Core.UI.Report.Common.Setting.QueryControl,FS.Core.UI">
  <Index>2</Index>
  <Name>cmbDept</Name>
  <Text>科室：</Text>
  <IsAddText>true</IsAddText>
  <ControlType Type="FS.Core.UI.Report.Common.ControlType.ComboBoxType,FS.Core.UI">
    <QueryDataSource>DepartmentType</QueryDataSource>
    <DataSourceTypeName>ALL</DataSourceTypeName>
    <IsAddAll>false</IsAddAll>
    <Enabled>true</Enabled>
  </ControlType>
</List>
```

放在最外层 `<List>` 内，与开始、结束日期控件同级；`Name` 必须唯一。`DepartmentType / ALL` 在 ReportDesk 中读取有效科室的编码和名称，不表示自动添加一个“全部”选项，也不会替代原 SQL 的权限限制。

如果要按这张报表已有的开方科室 `a.RECIPE_DEPT_ID` 筛选，在主 SQL 的 WHERE 条件中增加（放在 GROUP BY 之前）：

```sql
and a.RECIPE_DEPT_ID = '&cmbDept'
```

这里是假设筛选“开方科室”；若要筛选执行科室、就诊科室或申请科室，必须先核实业务字段。直接编辑 XML 时 `&` 要写成 `&amp;`；在程序 SQL 编辑框中使用普通 `&cmbDept`。程序会做参数绑定，不要手动拼接用户输入。

若要“全部科室”，还须明确一个不与真实编码冲突的全部值，配置 `IsAddAll / AllValue`，并给 SQL 增加对应的不过滤分支。不要仅添加选项而不修改 SQL。多选也需另行适配，不能把逗号分隔编码直接塞进等号条件。

改完后重新加载报表；只有当前 SQL 引用的参数才会出现在条件页。验证单个科室与未选状态，再在本机数据库检查筛选结果。

## 修改日期格式

修改日期控件内的 `<CustomFormat>`，并核对 SQL 中的 `TO_DATE` 格式。

| 目的 | CustomFormat | 对应 Oracle 格式 |
| --- | --- | --- |
| 日期 | `yyyy-MM-dd` | `yyyy-mm-dd` |
| 日期和可选择的时分秒 | `yyyy-MM-dd HH:mm:ss` | `yyyy-mm-dd hh24:mi:ss` |
| 当天开始时刻 | `yyyy-MM-dd 00:00:00` | `yyyy-mm-dd hh24:mi:ss` |
| 当天结束时刻 | `yyyy-MM-dd 23:59:59` | `yyyy-mm-dd hh24:mi:ss` |

当前观察室报表使用后两种：传给 SQL 的时间固定为当天开始/结束，并非用户选择的任意时分秒。`AddDays`、`AddMonths` 则决定初始日期偏移，当前两项日期的 `AddDays` 都是 `-1`。

ReportDesk 使用系统日期输入控件：`CustomFormat` 决定日期/日期时间类型及提交值格式，但不保证输入框按任意字符格式显示，例如 `yyyy年MM月dd日`；显示分隔符还受系统区域设置影响。若要完全自定义显示格式，需要另外改日期控件。

只保留日期时，现有 `<= 结束日期` 常会只查到结束日零点，不能当作全天。是否改用“次日零点之前”的范围，须结合实际列类型和统计口径单独确认。若只是调整结果表格里的“日期”列格式，应检查 SQL 的 `TO_CHAR`，不是查询控件的 `CustomFormat`。

## 本次边界

以上依据当前导入器、条件页、参数转换代码及本机 XML 核对。此说明没有新增条件编辑器，也没有修改原 HIS XML、数据库或日期统计逻辑；完整 HIS 客户端兼容性仍需实际验证。
