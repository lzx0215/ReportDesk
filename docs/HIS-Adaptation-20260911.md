# HIS 导出资料的实际适配

> 后续进展：本页保留早期适配记录。2026-09-11 已继续实现交叉/分组/多选等规则，最新为 1262 张无静态阻塞、22 张仍阻塞；真实结果仍未核对。详见 [Verification-LibCompatibility-20260911.md](Verification-LibCompatibility-20260911.md)。本地 LIB 已迁至 `D:\系统知识库\00_Inbox\yljhis\LIB`。

本次输入：`E:\sql\yljhis\SYS_BD_RESOURCE.xlsx`（715 条资源）、`E:\sql\无标题.xlsx`（2477 条菜单）、`E:\sql\yljhis\LIB`。原文件只读；没有连接真实 Oracle、运行 HIS 程序或复制 HIS DLL 到更新包。

## 实际解决的查询问题

同一批 3687 个 XML：1284 张含查询 SQL 的报表；1570 个版式、765 个其他 XML、68 个无查询 SQL 的定义不作为独立报表显示。更新前可试查 363、待适配 921；更新后可试查 944、待适配 340，净解除 581 张。可试查表示程序可以绑定条件并执行 SQL，不表示内网结果已经核对。

- 纯 AddMapRow/Column/Data/SourceData：原引擎只把结果放入版式映射字典，保留原表格结果；解除统一拦截。IsCross、列合计/重排、行分组、数据源属性引用仍单独判定，不能把它们当普通参数。
- Custom 下拉框：解析原 XML 的 ID/Name，保留前导零、顺序及自定义 AllValue 名称。例如“入库”不再一律显示成“全部”。不自动选择范围。
- CheckBox：按原引擎 Checked.ToString() 使用 True/False，并保持原默认值；固定 Custom 文本默认值同样保留。
- DepartmentType、EmployeeType、Dictionary：根据 BLL/DAL 实现补上选项查询，类型值使用绑定参数。指定科室类型保留 VALID_FLAG 和 BUSINESS_FLAG；科室 ALL 仅保留 VALID_FLAG；人员保留 VALID_FLAG；QueryConst(type) 原本没有有效标志过滤，不能自行添加。未知枚举、角色权限选项和复杂联动仍待适配。
- LISTAGG ON OVERFLOW TRUNCATE：仅在 LISTAGG 括号内部允许该语法，仍拒绝 TRUNCATE TABLE、写语句与多语句。原报表 SQL、科室过滤和汇总 SQL 未改写。
- 无分组的 SumColumns 合计：按 ucMainFpReport.RowGroup 原规则追加“合计：”，仅累加 decimal 列，不累加整数编码、日期或文本；全空金额仍为 NULL。IsSumRow 关闭或标签列不存在时不追加。空结果、精度、取消和类型异常均有离线检查。

逐文件对比实际有 602 张从待适配转为可试查，同时新识别出 21 张旧版漏拦截的转换/上下文问题，所以可试查净增加 581 张。不能只统计解除数量而漏报新增保护。

“普通门诊处方记录”和“门诊处方患者明细”本次原文件均已恢复可试查。前者显示起止日期；后者主表显示起止日期、药品类型，dtALL 明细要求处方号和唯一号。统一表格不包含 HIS 打印版式中的公式及右键联动。旧的精确三文件版本审查保留作历史证据，不再作为普通 SQL 表格查询的唯一准入规则。

## 未分类与完整位置的真实边界

已确认位置自动派生分类，保留用户手工分类。普通门诊处方记录采用用户已确认的“报表中心 → 各职能科室用表 → 药剂科 → 抗菌药物查询”，分类为药剂科。

这两份 Excel 可找到 666 张报表的 845 条**同名通用报表菜单候选**。按本次查询 XML 哈希关联；所有候选路径显示来源、菜单 ID，并标明已停用记录。存在有效菜单候选时提供“候选 · 分类”，不把同名当成明确 XML 绑定。当前 519 张有分类显示（含候选），其余仍未分类；用户手工分类优先。

无法从现有两表恢复全部真实位置的原因已由代码确认：

1. `ucCommonWindow.LoadReportFile` 用 `GetSystemReportSetting(menuID)` 查 `SYS_BD_REPORTSETTING` 的 `MEMU_ID` 和 `QUERYSETTINGFILE`。
2. `ucPrivReport.InitTreeList` 从 `SYS_BD_MENU_CONTROLS` 读取窗口内部子菜单；这部分不在 SYS_BD_MENU 导出中。
3. `SystemManager.QueryControlsSetting` 读取 `SYS_BD_RESOURCE_SETTING.SETTING`，其中可配置 Report 属性。
4. SYS_BD_MENU 的顶层记录可自指 parent；GROUP_ID 的名称在 SYS_BD_GROUP。不能把自指记录循环展开，也不能凭空补“报表中心”。

因此，尚缺四份配置表结果，补导出命令见 [Export-HIS-ReportLocations.sql](Export-HIS-ReportLocations.sql)。不需要患者数据、密码或重新导出整个 LIB。当前提供全部已知/候选路径，但不承诺全部真实 HIS 入口已经覆盖。现有 report-locations.xml 的精确 ID/哈希覆盖机制仍可用。

## 剩余待适配如何处理

340 张报表存在下列问题，数量可重叠；另有 68 份不完整定义不进入报表列表。

| 类型 | 报表数 | 下一步 |
|---|---:|---|
| 交叉、合计、行分组、属性依赖 | 176 | 按原引擎实现数据转换及依赖顺序，需用脱敏输入/预期结果核对；不能直接去除标记 |
| TreeView / 自定义控件 | 112 | 实现树选中值、多选与特殊控件行为；不能改成随意输入文本 |
| SQL/模板语法 | 64 | 分别处理动态片段、数据库链接、原生绑定、全角括号；业务 SQL 修改需确认 |
| ConditionUsing 等数据源用途 | 33 | 实现条件数据源输出与主查询依赖，不能独立当主报表运行 |
| 字典/自定义选项 | 30 | 处理角色选项、联动数据源或原定义中的空/不完整选项 |
| 默认值/上下文 | 17 | 明确 HIS 注入来源，保留原过滤范围 |
| 禁用控件 | 7 | 核对实际固定/注入值，不能启用成空白输入 |
| 分组查询 | 4 | 实现分组过滤与汇总顺序 |
| 参数冲突 | 2 | 核对原定义的重复/空参数名 |

这些不是通过重复导入就能全部消除。菜单补表用于完成分类和位置，不会自动解决剩余报表引擎功能。当前可先使用已经解除拦截的报表，其余逐项保留确切提示；完整逐报表清单在本次审计文件中。

## 实现与回退

Core 增加静态选项及选项源元数据，catalog 为向后兼容的可选字段扩展；旧条目无需迁移即可读取。重新导入后保存新定义，保留 ID、收藏、备注、手工分类；只有原哈希未变才保留用户核对标记。新的选项字段属于配置，不保存用户输入或查询结果。旧程序忽略新增字段，但回退后应重新导入恢复旧版适配判断。

只替换 app.asar、ReportDesk.Host.exe、ReportDesk.Core.dll；保留原程序及 catalog 备份。Oracle、Win10 x86/x64 现场结果验收仍为 NOT RUN。界面和离线验证记录见同目录验证文档。
