# LIB 全量适配与搜索列表 · 2026-09-11

## 结论与边界

目标仍是让 HIS 报表能够查询。本轮补充了原引擎执行规则，尚未达成“全部 HIS 报表已能正确查询”。

对照 `artifacts/audit/session-20260911/review.html` 基线，以 `E:\sql\yljhis\LIB\LIB` 为真实输入：

| 指标 | 基线 | 本轮 |
|---|---:|---:|
| 扫描 XML | 3687 | 3687 |
| 独立查询报表 | 1284 | 1284 |
| 无静态阻塞 | 949 | 1262 |
| 含待适配原因 | 335 | 22 |
| 无 SQL 的不完整定义 | 68 | 68 |
| 版式 / 其他 XML | 1570 / 765 | 1570 / 765 |

“无静态阻塞”只表示导入器和参数/规则检查允许尝试查询。不是 Oracle SQL 编译通过、数据库权限通过或结果已与 HIS 一致。原始查询 XML 1352 份的 SHA256 与基线完全一致。

新核查页：`artifacts/audit/lib-compatibility-20260911/review.html`。旧核查页保留作基线，未覆盖。

用户随后将资料复制到 `D:\系统知识库\00_Inbox\yljhis\LIB`，实际程序文件在其下一层 `LIB`。后续默认从此新路径只读获取。已重新全量导入，数量与 22 张阻塞名单不变，1352 份查询定义哈希及 FS.Core.UI.dll 哈希与旧路径一致。最新核查页为 `artifacts/audit/lib-current-path-20260911/review.html`，新路径验证在 `artifacts/verification/desktop/lib-corpus-1789125467452`。历史验证来源保留原 E 盘路径，不追溯改写。

## 代码与证据

- `HisResultRules.cs`：依据提供的 FS.Core.UI.dll 中 ucCommonWindow 的 queryCross/columnGroup、ucMainFpReport 的 RowGroup、DataSetHelper 的 SelectDistinctByIndexs 实现表格运算。包含取消检查；运算失败返回错误，不返回删减后的结果。仅支持一个交叉列字段、一个交叉指标。行分组沿用原引擎第一项配置、组边界和数据类型分支，未擅自重定义其业务口径。
- `ReportImporter.cs`：按根节点/SQL 识别；保留不完整定义单列。接入转换规则、树形选项及 ConditionUsing 引用，忽略原引擎不会使用的残留默认值、不可引用的空装饰控件和未使用的禁用日期条件。
- `SqlTemplate.cs` / `OracleQueryService.cs`：`.Value/.Text` 标量与多选值逐项使用 ASCII 绑定名；多选仅可占据完整 IN/NOT IN 参数位置。超过 1000 项改为绑定参数组成的子查询，不截断选项。尚无真实值执行验证。
- `SqlPunctuation.cs`：只将字符串/注释之外的全角括号转成半角，并在导入副本模板可编译时采用；不改原 XML，不改科室限制，不拼接用户值。不是完整 Oracle 语法修复器。
- Host：ConditionUsing 不列为用户查询数据源，加载选项按原顺序执行并处理所选表的 ID/NAME，参数依赖递归展开；表间数据依赖仍阻塞。实际 SQL 的属性依赖不以假值替代。
- Finance 的 ucInPatientNOForReport.RegisterID 经反编译为住院流水号；当前改为明确填写正整数流水号，没有实现按住院号自动找患者。Expense 命名空间的控件未找到同版本 DLL，不套用 Finance 实现。
- Electron：列表默认显示全部，输入即过滤，仅留搜索栏；多选编码保持跨页选择，可选全部实际选项。原 10px 圆角进度条不变。
- 旧 WinForms 入口对新增树形、前置数据源、属性引用明确拒绝，提示使用 Electron，防止按旧单值流程误查。本次小包只交付 Electron 所需文件。

DLL SHA256：`49E74C98EC905E05AB20EA6507FF78CAEB4C2FD786C7E45F867CD191CA9E98C7`。
反编译证据位于 `artifacts/verification/his-materials/` 的 `ucCommonWindow-review.cs`、`Implement.ucMainFpReport.cs`、`DataSetHelper-review.cs`、`Setting.RowGroupInfo.cs`、`ucInPatientNOForReport.cs`。没有运行 HIS 程序或连接真实数据库。

## 已执行验证

| 检查 | 结果 / 证据 |
|---|---|
| x64 Host 构建 | PASS，0 错误 0 警告，build-update 使用 --no-incremental |
| x86 WinForms/Core 编译 | PASS，0 错误 0 警告；不是 Win10 x86 实机运行验收 |
| real LIB 全量 Host | PASS：1284 报表、2347 主表/明细数据源，2270 无静态阻塞、77 阻塞；1352 原文件哈希一致；无 catalog 持久化 |
| Electron 源码 UI + LIB | PASS：根目录导入、搜索、普通门诊处方记录、交叉/树形条件、前置条件源隐藏、RegisterID 提示、清空搜索恢复全部、原进度条样式 |
| 精确旧包更新/回退 | PASS：三个文件校验、安装、幂等、打包 Host 导入全量 LIB、逐张选择、回退、再安装、依赖哈希一致 |
| 已更新打包 Electron UI + LIB | PASS，同一真实 LIB 界面流程 |
| 核查 HTML 浏览器检查 | PASS，22 张阻塞、1262 张无静态阻塞、展开原因及搜索；无页面脚本错误 |
| renderer.js 语法、git diff --check | PASS；Git 仅提示现有 LF/CRLF 转换信息 |

主要证据目录：

- `artifacts/verification/desktop/lib-corpus-1789125002381`
- `artifacts/verification/desktop/lib-ui-1789125009143`
- `artifacts/verification/desktop/lib-update-1789125221267`
- `artifacts/verification/desktop/lib-ui-1789125250615`

初次 UI 检查曾错误假定“天河区公费医疗SQL设置”仅一个数据源，并选择了首个数据源不引用 RegisterID 的患者报表；按真实 XML 修正断言/选择目标后通过。初次 x86 增量编译复用了 x64 Core，出现 CS8012；重新完整编译消除警告。没有把这些初次失败算作通过。

按用户要求，本轮未使用演示报表、虚构 SQL 或伪造结果行；未运行包含生成数据的旧 checks/build.ps1 流程。以前的旧测试通过记录不能代替本次验证。

## 尚未验证（NOT RUN）

- 真实 Oracle 登录、SQL 执行、数据库对象/函数/链接/权限。
- 同参数下 HIS 与 ReportDesk 的明细、交叉、合计、空值、重复组结果比对。
- 真实选项表、多选、全部选择、超过 1000 项的绑定执行，以及各控件显示文本与编码的一致性。
- Win10 x86/x64 现场兼容性；当前交付小包仅 Electron x64。
- 不完整定义的调用窗口取数、非 XML 硬编码报表的全覆盖。

LIB 中的规则文件不能替代这些真实结果验证，不应以伪造行数或假定数据类型补成 PASS。

## 剩余 22 张如何处理

核查页提供每张文件位置、提示和动作。原因可重叠：

1. 四份按金/收据报表需要 PermissionDept(EXP-REPORT-01) 与科室下人员。必须追踪原操作员、角色和科室上下文，不替换为全院人员。
2. 四份一日清单有 TableGroupUsing 和集合属性引用。原引擎先执行分组集合与主表，再按 GroupCondition 筛选各表并重新映射；三份按 register_id、一份按 daylist，不是每组重新执行 SQL。此链尚未实现，且四份患者树的实际来源有矛盾：两份配置为 EmployeeType=D（医生），另两份引用 dtPatientInfo 却没有对应 ConditionUsing。须先核对实际调用窗口如何赋值，不能猜患者集合后执行。
3. 四份定义引用 FS.Expense.UI 的住院控件；现有 LIB 未找到该 DLL，不能仅因属性也叫 RegisterID 就套用 Finance 类型。与第 2 项有重叠。
4. 四份报表存在禁用医生或 RegisterID/BalanceID，需要原调用方 SetParm 等上下文来源。
5. 其余包括空字典类型、空选项绑定、DataSource=abc、DepartmentType=N,I 和同名 dtUseType 对应不同控件。这些需要对照实际使用版本/调用窗口，不按名称猜值。

需要用户提供的只有本地资料不足部分：同版本 FS.Expense.UI.dll（如果内网仍在使用该组件），以及上述异常定义在 HIS 正常使用时的调用信息。未要求重新导入相同 XML。完整真实结果验收仍须在内网进行。

## 更新与回退

更新：`artifacts/updates/ReportDesk-0.2.0-lib-compatibility-20260911.zip`。
SHA256：`A5681DD4D417EC1C59953DD3CF51EC69F270491D83C6C02D8FE0E635CB4480ED`。

基线：上一份 `ReportDesk-0.2.0-session-final-20260911` 的安装结果。仅包含 app.asar、Host EXE、Core DLL 和校验/回退脚本。未部署到用户内网，没有改变连接配置、身份权限、数据库或原始 LIB。操作步骤见 `docs/OfflineUpdate.md`；基线不符拒绝更新。
