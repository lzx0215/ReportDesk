# 独立查询条件页实施与验证（2026-09-14）

## 交付和范围

- 新客户端：`artifacts/desktop/ReportDesk-conditions-view-20260914/ReportDesk.exe`，分发须保留整个目录。
- 增量包：`artifacts/updates/ReportDesk-0.2.0-conditions-view-20260914.zip`，精确基线为 `artifacts/desktop/ReportDesk-query-uniform-20260914`。
- ZIP SHA256：`0F75BA86DD533F340B574353650A1CAED2A6059C9C8241777F5F41D7C9173FE2`。
- 本轮开始前文件备份：`artifacts/verification/conditions-view-before`。原客户端保留；未 commit、push 或部署。
- 保留此前两项任务的页面样式、报表位置、检查新增报表、导航及结果功能。

## 1. 修改文件

- `src/ReportDesk.Desktop/ui/query-form.js`：QueryState、字段工厂、QueryConditionsView。
- `src/ReportDesk.Desktop/ui/renderer.js`：会话管理、View 切换、统一查询入口、摘要/快照及 lookup 状态连接。
- `src/ReportDesk.Desktop/ui/index.html`：独立 View 容器、条件摘要和条件页动作。
- `src/ReportDesk.Desktop/ui/styles.css`：两列表单、独立滚动、紧凑摘要及结果空间。
- `tests/desktop/lib-query-conditions-checks.cjs`：新增真实 LIB 条件页回归。
- `tests/desktop/lib-query-layout-checks.cjs`：旧入口转发到新的交互检查，移除过时的宽度/行数断言。
- `tests/desktop/lib-new-reports-checks.cjs`：更新主页面重新显示紧凑状态/来源的断言。
- `tests/desktop/ui-checks.cjs`：替换过时的折叠按钮断言；其余历史离线演示检查未运行。
- `docs/Desktop.md` 和本验证记录：交互说明、边界及证据。

## 2. currentReportSession 结构

```js
{
  reportId,
  source,
  details,                       // 引用原 Details，不复制参数定义
  parameterValues: Map,          // name -> { value, text, selections? }
  textParameters: [],            // 原报表定义中引用的 .Text 名称
  lastExecutedQuerySnapshot: null // 成功后保存实际 { reportId, source, values }
}
```

普通值为原提交字符串或未选择的 null；checkbox 使用原有 True/False；multiple 为原有编码数组并保留显示名称。所有值及快照只在当前会话内存中存在，不持久化，不缓存跨报表状态。

## 3. 从 DOM 提升参数状态

原 renderer 的创建分支提取到 `createControl()`，保留原 initial、options、日期类型/step、checkbox、multiple、treeSelect 和 implicit 行为。首次创建时由控件的真实初始状态初始化 Map；随后 input/change 及 lookup 回填统一调用 `sync()`。提交和摘要读取 Map，不再临时遍历 DOM 作为唯一数据来源。

普通 View 切换保持 DOM 挂载。额外验证了主动重建条件组件时可从同一会话恢复，避免重建后返回默认参数。

## 4. DOM 与布局

右侧 workspace 内为同级 `#report-view` 与 `#query-conditions-view`；共同保留外部应用栏、左侧报表列表及执行状态。

- ReportView：标题、单行状态/来源、报表位置/查询条件/说明/SQL、36px 条件摘要条及开始查询、轻量 dirty 提示、结果工具栏、结果表格和分页。
- QueryConditionsView：返回报表、标题及报表名、独立滚动表单、固定可达的底部“应用并查询”。没有单独“应用”、取消或 Draft。
- 表单默认两列，数据源在首位，参数按 Details 原顺序从左到右、从上到下排列。控件高 36px，最大表单宽度 920px；窄空间可降一列。加载按钮保留文字，日期拥有完整显示空间。

## 5. 后端及原资料保持不变

本轮开始前记录并最终核对了 27 个 Core/Host/IPC 源文件 SHA256，全部一致。新旧客户端的 `ReportDesk.Host.exe` 与 `ReportDesk.Core.dll` 也逐字节一致；主进程、bridge、preload 未改，IPC contract 未改。原 XML/LIB 只读，没有 SQL 改写或生产数据库调用。

额外使用现有只读 `definition` IPC 识别 `.Text` 引用，不扩展 Details 或参数模型。客户端 ASAR 中 7 个前端/IPC 文件与当前源码逐字节一致。

## 6. 返回报表保留值

进出条件页仅设置两个容器的 hidden 和焦点，保留同一 session 与控件节点。不调用 select、lookup，不清空结果，不重读默认值。返回不执行查询。

## 7. 共用查询入口

主页面开始查询与条件页应用并查询绑定同一个 `executeQuery()`，经 `buildQueryPayload()` 生成原 IPC 参数。提交后只在 query 成功时克隆当次真实请求作为快照。busy/disabled 阻止重复执行；延迟成功响应下重复调用也仅提交一次。

明确的已有 Host 参数错误进入条件页并展示原错误，不在前端推测 Required 或错误字段；取消和非参数错误保持原提示行为。开始新查询时沿用 Host 清空旧结果的语义，失败/取消不替换上次成功快照。

## 8. Query Summary

实时从会话参数与 Details Label 派生，不保存另一份 summary state。数据源显示名称，日期仅改变展示格式，选择项显示名称，多选显示名称列表；跳过空值和 implicit。保留有效 0 和 checkbox 的“否”。摘要单行省略，title 保留完整内容；不改变实际提交字符串。

## 9. dirty state

仅在存在旧结果且当前规范化 payload 与成功快照不同时显示“条件已修改，结果尚未更新”。不比较 HTML、Label 或摘要文字；不自动查询、不因编辑清空旧结果。改回成功查询的参数后自动消失。

比较 reportId、source、排序后的参数键及原编码/值，保留字符串与 null、空值及数组的区别。DateTime 缺省秒统一为 `:00` 进行比较，不改变提交值。纯显示伴随值 `.Text` 排除；原 SQL 中引用的 `.Text` 会影响业务结果，必须纳入。第一版依据整张报表的 definition 保守纳入文本引用，包含该报表其他数据源的引用；未新增 SQL 解析或后端规则。

## 10. lookup 状态同步

原 lookup、lookupPage、lookupAll IPC 和选项弹窗继续使用。单选后、多选增删、清空、全选后均调用同一 sync，保证 UI、Map、摘要及 payload 一致。multiple 仍为只读输入，通过选择弹窗编辑；implicit 保留原先可编辑/只读语义，不自动填写未知上下文。

## 11. 报表与数据源切换

- 选择另一报表：退出旧条件页，按原流程 select，创建新 session，清空旧结果，显示新报表 ReportView，不保留跨报表参数。
- 切换数据源：按原流程重新获取 Details、重建参数并清空结果/快照，但完成后停留在条件页。
- 报表加载失败时旧字段已清除，查询动作不可用，不混合新标题与旧参数。

## 12–14. 真实报表与三窗口效果

完整真实 LIB 加载 1284 张可见报表。最终检查共 10 张不同报表、11 个报表/数据源案例、3 个窗口，即 33 组布局检查。

| 重点报表 | 参数数 | 1040×700 | 1280×720 | 1920×1080 |
| --- | ---: | --- | --- | --- |
| (病案)观察室工作日志 | 2 | PASS | PASS | PASS |
| 按科室(出院) | 5 | PASS | PASS | PASS |
| 门诊中药处方查询 | 5 | PASS | PASS | PASS |
| 住院各科有效收入统计（含 CurrentDeptID） | 4 | PASS | PASS | PASS |
| 科室口服执行单查询 | 18 | PASS / 条件页滚动 | PASS / 条件页滚动 | PASS / 条件页滚动 |

上述报表在同一窗口尺寸下，主页面表格区高度均分别约 217px、235px、595px，不再随参数数量变化。18 参数在两列中排列，表单内滚动，顶部返回和底部查询动作均在可见区域；没有横向页面溢出或字段重叠。

另外覆盖零参数、多数据源、TreeViewType/TreeSelect、Multiple、Checkbox、RegisterIdType、implicitValue。11 个案例在新旧实际客户端设置相同 UI 值后，提交参数完全一致。

## 15. 已删除旧实现

移除 setConditionsExpanded/toggle、折叠入口及事件、aria-expanded、主页面 QueryFieldsPanel flow、144/176/200 字段宽度 token、复合字段 110+4+30 限制、查询按钮作为字段末尾 flow item 的逻辑及对应失效测试。保留字段 Label/控制关联、title、控件业务规则和原查询结果工具栏。

清理在首轮新 View 真实 LIB 检查通过之后进行，清理后的打包客户端再次通过完整检查。

## 16. 验证证据与后续边界

- 构建：`scripts/build-update.ps1`，0 警告、0 错误。
- 首轮源码 UI：`artifacts/verification/desktop/lib-query-conditions-1789374685633/results.json`。
- 最终打包客户端及实际旧包参数对照：`artifacts/verification/desktop/lib-query-conditions-1789374995018/results.json`，PASS；同目录含各窗口条件页和主页面截图。
- 新增报表及已合并功能：`artifacts/verification/desktop/new-reports-1789374920728/PASS.txt`，PASS。
- 精确基线更新、幂等、真实 XML 导入、回退/再应用及依赖哈希：`artifacts/verification/desktop/report-update-1789374871782/PASS.txt`，PASS。
- `git diff --check` 通过，仅有既有 LF/CRLF 提示。
- 测试启动曾遇到窗口尚未创建和完整 LIB 扫描超出 20 秒等待：调整测试顺序为 firstWindow 后安装拦截，并将启动等待放宽至 120 秒；最终完整重跑通过。没有调整应用连接/查询计时器。
- 成功、取消、参数错误和连接错误使用明确标记的 UI IPC 测试响应。成功响应为空结果信封，不包含伪造 HIS 结果行。lookup 交互复用真实 LIB 静态选项作为 UI 测试输入，不代表这些选项属于实际数据库字典查询结果。
- 真实 Oracle 查询、真实 lookup 返回值、HIS 结果等价、Windows 10 x86 验收：NOT RUN。历史 `ui-checks.cjs` 包含旧离线演示场景，本轮没有运行或用其替代真实 LIB 检查。
- 后续如需精确错误字段定位或仅按当前 SQL 的文本绑定识别 dirty，需要独立评估结构化后端 metadata；本轮没有改变后端协议。第一版不提供跨报表缓存或取消回滚，符合确认范围。
