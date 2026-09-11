# ucCommonWindow 原引擎规则核对

核对日期：2026-09-11。范围：静态反编译及与 ReportDesk 当前代码对照；未加载运行 HIS 组件，未连接数据库，未修改原 DLL/XML 或程序实现。

## 证据

- 原程序集：`E:\sql\yljhis\LIB\LIB\FS.Core.UI.dll`
- SHA256：`49E74C98EC905E05AB20EA6507FF78CAEB4C2FD786C7E45F867CD191CA9E98C7`
- 类型：`FS.Core.UI.Report.Common.Implement.ucCommonWindow`。用户提供的逗号后 `FS.Core.UI` 是程序集名称。
- 本次反编译：`artifacts/verification/his-materials/ucCommonWindow-review.cs`、`Report.Function-review.cs`。
- 配套核对：同目录 `Setting.ReportQueryInfo.cs`、`Implement.ucMainFpReport.cs`、`FS.Manager.BLL.DataManager.cs`。
- 行号均指上述反编译文件，不是厂商原始源码行号。反编译证明当前 DLL 的静态实现，不能替代现场结果核对。

## 实际处理流程

1. **菜单绑定查询配置和版式配置**：`LoadReportFile`（510）使用窗口 `Tag` 中的菜单 ID 调用 `GetSystemReportSetting`，读取 `QuerySettingFile` 和 `FpSettingFile`，拼接程序启动目录。查询配置反序列化为 `ReportQueryInfo`。本类不负责递归扫描整个安装目录。原代码在已指定但不存在的查询配置路径上会创建默认 XML；本次没有运行该代码。
2. **创建界面和条件**：`OnLoad`（1494）依次加载文件、初始化参数、初始化界面和明细方式。`InitInterface`（551）建立主报表、明细、顶部条件和左侧树。控件自身仍有独立规则，不能只凭窗口类声称覆盖全部控件语义。
3. **准备默认上下文**：`ReportQueryInfo.GetDefualtSetting`（153）提供当前操作员 ID/名称、医院 ID/名称、科室 ID/名称和系统时间。XML 不包含所有现场上下文；离线程序不能随意猜这些值。
4. **加载条件选项**：`queryDataByControlType`（742）支持 SQL、科室、机构、字典、员工、其他数据源、科室统计关系等。`PermissionDept` 调用时传入当前角色 ID、操作员 ID 和配置类型，不能当成任意科室全集。
5. **收集控件值到共享 map**：`queryCondition`（984）写入 `控件名`、`控件名.Value`、`控件名.Text`；树另有 `Values`、`Texts`。`IsLike` 使用配置的 `LikeStr` 格式化。过滤控件在此步骤跳过。`&控件.Value` 是普通控件值引用，不能与跨数据源依赖混为一谈。
6. **按用途和顺序执行数据源**：`queryDataTable`（1027）遍历 XML 中的数据源列表，只处理当前 `SqlType`，同用途按列表顺序执行。条件选项可用 `ConditionUsing`；查询入口先处理 `TableGroupUsing`，再处理 `MainReportUsing`；明细入口处理 `DetailReportUsing`。每个数据源处理后更新同一个 map，后续项可以引用前面的结果。
7. **转换结果并建立映射**：SQL 结果先按 `IsCross` 选择交叉转换，否则进入 `columnGroup`；随后处理 AddMap，最后把结果表及配置存入 `map[数据源名]`。不含 SELECT 的数据源在该路径中作为替换后的字符串保存，未调用数据库执行。
8. **显示、分组及明细联动**：`query`（868）把 map 传给主报表组件。表分组模式先取得主查询结果，再按组表的每一行和 `GroupCondition` 过滤已取得的表、重建映射，生成多个显示分组。明细按配置从条件或选中数据取得值；行选择/单击/双击处理见 1533、1563、1603。

## 容易误判的 XML 字段

| 字段 | 当前 DLL 的实际作用 | 对 ReportDesk 的意义 |
| --- | --- | --- |
| `AddMapData` | 为每个单元格建立 `数据源.Rows[行号][列号或列名]` 变量 | 开启映射本身不表示 SQL 结果不能显示；使用者依赖这些变量时才需要解析依赖 |
| `AddMapRow` | 保存各行 DataRow 对象 | 与单个标量值不同，不能直接当普通参数 |
| `AddMapColumn` | 保存单列数据表及列名/标题表 | 可能供版式或后续处理使用 |
| `AddMapSourceData` | 保存结果单元格对应的原始 DataRow 数组 | 交叉表下钻需要保留来源关系 |
| `IsCross`、`CrossRows/Columns/Values` | 以行键和列键生成交叉表 | `queryCross`（1134）按类型处理：decimal 累加，字符串/日期取遍历到的最后值，其他类型走整数转换累加；不能简单假定所有列都是 SUM |
| `CrossCombinColumns` | 按组调整列顺序、列标题，并可配合 SumRows 增加组内合计列 | 兼有列展示和计算语义，不是所有情况都有计算 |
| `SumRows` | `columnGroup`（1284）增加横向计算列。例如 `总额:药费,检查费` 生成每行的总额列；无冒号形式按已有 decimal 列构造求和表达式 | 当前 ReportDesk 提示“指定行汇总”不准确，应改为横向合计列，并单独实现验证 |
| `RowGroup` | `ucMainFpReport.RowGroup`（436）处理分组小计，当前实现使用第一个分组配置 | 不只是改变排列样式，也会产生计算行 |
| `IsSumRow`、`SumColumns` | 同一 RowGroup 方法内控制合计行；无分组分支对 decimal 列求和，并在指定列写合计标签 | 与 SumRows 横向合计列不同；各分支的类型选择也有差异 |

以上是静态代码的操作方式，不表示异常值、空值或所有类型组合都能正确执行。原实现的边界行为需用实际定义和离线数据样例核对，不能把推测当成 HIS 的业务口径。

## SQL 替换的真实边界

`Report.Function-review.cs` 中 `ReplaceValues`（11）使用共享 map 替换 `&变量`，一般替换分支还移除被替换值中的分号；完整字符串恰好匹配一个 map 键时直接返回该值。`HasSelect`（39）仅判断字符串是否包含 SELECT，不是完整 SQL 语法或只读校验。`DataManager.ExecuteDataTable(string, ref DataSet)`（2997）继续将 SQL 字符串交给下层执行。

因此，“多语句、原生绑定变量、数据库链接或全角括号”这一组待适配提示是 ReportDesk 的本地校验规则，不是此 HIS 窗口类定义的拒绝条件。这也不证明 Oracle 能成功执行包含这些内容的任意 SQL。

ReportDesk 应移植经确认的取值、依赖和计算语义，仍把用户值转换成绑定参数，不能直接照搬原类的字符串替换方式或 SELECT 子串判断。

## 与当前 ReportDesk 的差距和处理顺序

当前 `ReportImporter.cs`（146）通过 `TabularReportAdapter.HasMapReference` 把带点号/方括号的变量统一标为属性映射待适配，包含普通控件 `.Value/.Text`；非主表/明细用途也会阻塞。`TabularReportAdapter.cs`（18）对交叉、组合列、SumRows、RowGroup 给出阻塞提示。这些是兼容能力边界，不是 XML 文件缺失的证据。

建议按如下顺序实现，当前分析没有修改运行代码：

1. 分清普通控件属性、HIS 身份上下文、数据源单元格、整行/整列对象，避免一条正则统一判定；未知上下文明确提示缺什么。
2. 建立按 SqlType 和 XML 列表顺序执行的计划，支持共享变量及来源追踪；依赖未满足时只说明具体缺项，不静默跳过。
3. 分别实现组合列、横向合计、交叉表、分组小计，使用原 DLL 规则及边界样例核对。
4. 保留主表到明细的参数传递和交叉来源关系；统一表格展示无需复刻打印版式，但不可丢掉影响数值的版式计算。
5. 对每个报表输出精确到节点、数据源、依赖或函数的剩余阻塞原因。真实 Oracle 执行与 HIS 结果对照仍须在内网验收。

本次结论：已有 DLL 和 XML 足以进一步确定大量兼容规则；这些待适配项不能继续统一归因于未导入配套 XML。菜单绑定、现场身份权限、数据库对象和数据结果则仍需各自的证据。
