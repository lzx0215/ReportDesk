> 后续实现说明：本次 HIS 程序集与 Excel 核对已扩展实际适配和菜单候选分类，见 [HIS-Adaptation-20260911.md](HIS-Adaptation-20260911.md)。以下原验证证据保留；精确三文件组合不再是普通 SQL 表格查询的唯一准入条件。

# 门诊处方患者明细：已核对文件组合的表格适配

本适配解决四项 AddMap 标记导致本地已核对版本整张报表被拦截的问题。适配范围是 ReportDesk 的统一表格查询与导出，不声称复刻 HIS/FarPoint 引擎或已经验证 Oracle 业务结果。

## 原始证据（只读）

来源：`D:/系统知识库/00_Inbox/yljhis/reports/`。

| 文件 | SHA256 |
| --- | --- |
| 门诊处方患者明细查询设置.xml | 7AAC5C3C2ADB9E63714309F0F81AB28F510C59E81AE1E25595BEDD7AB111F35C |
| 门诊处方患者明细报表设置.xml | D29DC84D85488F48B5A80F2B56B703EB8BEF748128D27231C3A9317199DAECDA |
| 门诊处方患者明细的明细报表设置.xml | 67BA5534D94024E7FD391126FD07423250BA21C4741710724BD66A0AEAC600D5 |

- 查询 `dtMain` 四项 AddMap 均为 true，但 `IsCross=false`，CrossRows/Columns/Values、SumRows/Columns、RowGroup 为空。没有分组数据源 SQL。
- 主模板 `Data/Sheets/Sheet/DataArea` 为 1 行、5 列，唯一单元格标记为 `dtMain`，无公式。ColumnHeader 的五列为处方号、唯一号、患者姓名、诊断、科室名称，与主 SQL 的五个输出列对应。
- 主 SQL 自带 UNION ALL 统计行；沿用其原始过滤和统计逻辑，不重新计算、不改 SQL。统计值位于原 SQL 的第五列，不擅自改名或认定与明细行数必然相等。
- 明细 `dtALL` 是原有独立查询，AddMap 均为 false。配套模板标记为 `dtALL`，无公式；其药品编码/名称列的打印标题与 SQL 别名不同，ReportDesk 沿用数据库返回的列名。
- 明细引用 `ColumnCondition=处方号|唯一号`，原 SQL 也用这两个值过滤。ReportDesk 沿用已有的独立数据源模式，切换 dtALL 后填写主表对应编码；不增加 HIS 右键联动、不删除明细过滤。

上述证据支持对该文件组合输出原 SQL 的表格结果；没有证据支持把所有 AddMap 报表统一放行。对完整 HIS 映射引擎内部行为的判断仍有限，因此用三个精确哈希限定本次适配。

## 实现与兼容

`OutpatientPrescriptionAdapter` 核对查询定义及同目录两份版式的实际内容哈希。仅这组已核对文件组合可以免除 AddMap 拦截；原有 SQL、控件、上下文等其他检查保留。改名不能让其他定义获得适配；文件内容变化、配套缺失/不可读/链接均保持待适配。即使只改变编码或换行导致哈希变化，也不自动扩大适配范围。

不修改原 XML，不新增持久化字段，不改变报表 ID 或用户配置。旧 catalog 中的待适配状态需要**重新导入**，不会在启动时静默改变；重新导入保留收藏、说明等原有元数据。

验证发现原参数解析正则仅识别 ASCII 名称，会漏掉明细 SQL 中的 `&唯一号`、`&处方号`。本次同时支持 Unicode 字母/数字参数名，仍生成 ASCII `:pN` 绑定；参数值绝不拼入 SQL。缺参数拒绝执行，注释忽略，中文动态表名/列名仍拒绝。此修复影响重新导入的其他中文参数定义，因此执行通用模板和整个本地报表集回归。

界面显示适配说明：主表包含原 SQL 的列表及统计行，主明细独立查询，不复刻右键交互和打印版式。它仍是“可试查 · 未核对”，只有用户完成内网对照后才能标记业务已核对。

## 验证方式

`powershell -File scripts/build.ps1 -ScanDirectory 'D:/系统知识库/00_Inbox/yljhis/reports'`：核心回归和实际三文件适配检查。没有提供原资料目录时，真实文件检查明确 NOT RUN。

`node tests/desktop/prescription-checks.cjs 'D:/系统知识库/00_Inbox/yljhis/reports' '上一份已交付的完整程序目录'`：旧后台复现拦截，更新后台重新导入后复测，验证原 SQL、参数集合、说明/ID保留与真实 Electron 界面。后台 --offline 阻止真实数据库连接，不能把到达离线边界表述为 Oracle 执行成功。

更新仍采用三个文件的小包；用户关闭程序、备份后合并 payload/resources 到程序目录，替换三文件，再重新导入上述三份 XML 所在目录。原截图的“普通门诊处方记录”未找到同一份原文件，不在此适配承诺范围内。
