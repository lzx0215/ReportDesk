# 查询条件弹窗与单行工具栏验收（2026-09-14）

按用户最新图片，将独立条件页改为居中模态弹窗，并将条件摘要、修改条件、开始查询、结果内查找、筛选、复制选中行、导出 Excel 放在同一行。报表标题旁保留报表位置、报表说明、查看 SQL。原独立 View 切换代码已移除。

## 实现

- 使用当前窗口中的原生 dialog，不新增 BrowserWindow 或依赖。宽 760px，最大高度为窗口内容高度减 80px；标题为“当前报表名 - 查询条件”，内容独立滚动。
- 数据源在前，随后按 Details 原顺序逐行显示，每个条件一行。标签列统一 180px，控件右侧对齐；包括复选框。lookup 控件保留“加载选项”按钮及原选择弹窗。
- 底部“关闭 / 应用 / 应用并查询”，右上角关闭、Esc 可用。关闭和应用均保留内存条件并关闭弹窗，不查询；没有取消回滚。忙碌时阻止 Esc 关闭条件弹窗。
- 主页面摘要及工具栏不换行，所有控件垂直居中；窄窗口由摘要省略腾出空间，title 提供完整条件。
- 原 currentReportSession、QueryState、字段工厂、lookup、序列化、成功快照和 dirty 比较均复用。打开/关闭弹窗不 select、不重新默认赋值、不清空结果。开始查询与应用并查询仍为同一入口。
- 数据源切换后按原行为重建参数和清空结果，再显示条件弹窗。明确参数错误在弹窗显示原信息。模态打开时背景不可操作，切换左侧报表须先关闭弹窗。

## 修改范围

- `src/ReportDesk.Desktop/ui/index.html`
- `src/ReportDesk.Desktop/ui/styles.css`
- `src/ReportDesk.Desktop/ui/renderer.js`
- `tests/desktop/lib-query-conditions-checks.cjs`
- `tests/desktop/ui-checks.cjs`（更新旧入口选择器；历史演示套件本轮未运行）
- `docs/Desktop.md`、本验证记录

本轮开始前备份位于 `artifacts/verification/conditions-modal-before`。27 个 Core/Host/IPC 源文件哈希一致；query-form.js 状态/工厂文件未改；新旧客户端 Host EXE、Core DLL 相同；ASAR 内 7 个前端/IPC 文件与当前源码完全一致。原 XML/LIB 只读，未改 SQL 或 IPC contract。

## 验证结果

- Build：0 警告、0 错误。
- 首轮源码检查：`artifacts/verification/desktop/lib-query-modal-1789376717894/results.json`，PASS。
- 最终打包客户端检查：`artifacts/verification/desktop/lib-query-modal-1789376863303/results.json`，PASS。
- 完整 LIB 加载 1284 张可见报表；10 张真实报表、11 个数据源案例 × 1040×700、1280×720、1920×1080，共 33 组布局检查。
- 五张重点报表：`(病案)观察室工作日志`、`按科室(出院)`、`门诊中药处方查询`、含 CurrentDeptID 的`住院各科有效收入统计`、18 参数的`科室口服执行单查询`，三种窗口均通过。另覆盖零参数、Multiple、TreeSelect、Checkbox、RegisterID、多数据源。
- 检查每条件一行、控制列对齐、无重叠、表单滚动、底部按钮可达，以及主页面工具栏单行居中和摘要保留可用宽度。
- 检查关闭、应用、右上角关闭、Esc 后值不丢失且不提交；再次打开不重建 Session/节点；lookup UI/状态/提交一致；共用查询仅执行一次；错误/取消不更新成功快照；条件变更和恢复时 dirty 正确。
- 与上一版实际客户端对照的 11 组参数提交值全部一致。仅一个 BrowserWindow。
- 更新和回退：`artifacts/verification/desktop/report-update-1789376838592/PASS.txt`，精确基线、三文件更新、幂等、真实 XML 导入、回退/再应用及依赖哈希全部 PASS。
- `git diff --check` 通过，仅有既有 LF/CRLF 提示。

截图在最终检查目录，例如 `0-1040-report.png` 和 `4-1040-conditions.png`。

## 交付

- 完整客户端：`artifacts/desktop/ReportDesk-conditions-modal-20260914/ReportDesk.exe`，分发须包含整个目录。
- 增量包：`artifacts/updates/ReportDesk-0.2.0-conditions-modal-20260914.zip`，仅适用于精确基线 `artifacts/desktop/ReportDesk-conditions-view-20260914`。
- ZIP SHA256：`CCF648285DB94AD519E0C928F7E5CB8DA80849F4A0C16D02959E55C860F56B76`。

未连接真实 Oracle；成功/失败/取消使用 UI 测试响应，成功为空结果信封，没有伪造 HIS 结果行。lookup 测试复用真实 LIB 静态选项驱动 UI 回填，不代表实际数据库字典结果。真实 Oracle/HIS 等价与现场平台验收仍未执行。未 commit 或 push。
