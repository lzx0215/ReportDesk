# SQL 原文件保存验证（2026-09-17）

## 工具栏后续：打开原报表位置

按最新截图去掉可见“数据源”标签（下拉框保留无障碍名称），在重新加载按钮右侧加入“打开原报表位置”。主进程只使用 Host 已返回的当前报表路径，并核对编辑会话和文件存在性；通过系统资源管理器定位原 XML。点击不会保存、查询、重新加载或丢弃 SQL 草稿。

20 项前端/主进程检查通过，包括外部传入路径被忽略、失效会话/演示/文件丢失拒绝、未保存草稿保持。实际 EXE 使用“(病案)观察室工作日志”副本，在 1040、1440、1920 宽度下工具栏均单行无溢出；原生文件定位与连续保存测试通过。证据目录：`artifacts/verification/desktop/sql-editor-e2e-1789655683423/`、`sql-editor-e2e-1789655729944/`。

当前程序：`artifacts/sql-editor-location/ReportDesk-win32-x64/ReportDesk.exe`。

## SQL 编辑工具栏布局调整

按用户红色标注：数据源标签位于下拉框左侧；下拉框与复制 SQL 按钮宽度均为 112px；全部工具栏控件高 40px；复制、静态检查、重新加载、关闭、保存移到数据源同一行。未保存状态保留在编辑区下方。沿用原 CSS 和颜色变量。

实际 EXE 在 1040×700、1440×930、1920×1080 下均检查通过：六个控件同一水平线，无横向溢出，SQL 编辑区在工具栏下方。17 项前端回归、真实 E 盘副本连续保存两次和无备份检查通过。证据：`artifacts/verification/desktop/sql-editor-e2e-1789655318573/`，包括 `toolbar-1040.png`、`toolbar-1440.png`、`toolbar-1920.png` 和 `PASS.txt`。

布局检查采用 make-interfaces-feel-better 技能的现有样式复用、光学对齐和桌面 40px 点击区域原则；没有新增动画、图标或视觉主题。本次仅改界面布局，未重复数据库测试。

当前程序：`artifacts/sql-editor-toolbar/ReportDesk-win32-x64/ReportDesk.exe`。

## 22:19 后续：普通会诊及时完成率与取消备份

- 直接读取用户截图指向的 `E:\his\LIB\Config\Xml\普通会诊及时完成率查询设置.xml`：`dt7` 主表 SQL 末尾已是 `--order by SORT_ID`，文件修改时间为 22:13:30，大小 7991 字节。`dt7Det` 明细仍是 `order by vd.SORT_ID`。因此这次截图的主表修改已落盘；不能把明细 SQL 未改变认定为主表保存失败。外部窗口是否缓存旧内容尚未直接确认。
- 按用户要求取消 SQL 保存备份：原文件替换时不提供备份目标，不再返回备份路径；确认按钮改为“覆盖并保存”。保留冲突检测、编码保持、暂存完成再替换和磁盘回读校验。旧备份不删除。
- 专项 Core/Host 17 组、前端 17 项通过。Host 构建 0 警告、0 错误。
- 真实“普通会诊及时完成率”副本在 E 盘上连续两次改变 ORDER BY 注释状态、保存、磁盘读取和重新打开通过；其他数据源 SQL 未变，无 `.bak` 和残留 `.tmp`。源码证据：`sql-editor-e2e-1789654730082/PASS.txt`；实际新 EXE：`sql-editor-e2e-1789654754591/PASS.txt`。
- 本机 Oracle 保存后执行该报表 SQL（零行包装、绑定日期）通过：`local-oracle-20260917-221836/PASS.txt`。
- 当前新程序：`artifacts/sql-save-direct/ReportDesk-win32-x64/ReportDesk.exe`。

以下为此前增强回读校验时的历史记录，其中备份行为已由上述变更取代。

基线：`d74c1be`，包含本次未提交修改。

## 结论与原因边界

用户反馈保存后原 XML 中 SQL 未改变。本机使用修改前源码，分别在 D 盘、E 盘副本上通过真实 Electron 界面保存，均实际改变文件、生成正确备份并可重新打开读取。原反馈未复现，尚不能断言根因。用户原故障的 EXE 路径、报表路径、所选数据源和保存提示待对照。

E 盘为 exFAT；同盘文件替换已实测通过。原有 `artifacts/desktop` 包时间为 2026-09-10，不能作为当前功能的验证对象；这不是用户故障根因的证明。

本次增强：文件替换完成后重新从磁盘读取 XML，同时核对整文件哈希与目标 SQL；成功结果使用回读快照。若回读失败，明确提示替换已完成但校验未通过，保留草稿并阻止旧定义继续查询。界面显示已核对的完整文件路径；无内容变化时明确提示无需写入。

## 测试结果

| 检查 | 结果 | 证据 |
|---|---|---|
| Core/Host SQL 编辑 | PASS，17 组 | `dotnet run --project tests/ReportDesk.SqlEditing.Checks -c Release` |
| 编辑器前端与主进程 | PASS，17 项 | `node --test tests/desktop/sql-editor-checks.cjs` |
| 修改前 D、E 盘真实界面回写 | PASS | `sql-editor-e2e-1789652702291`、`sql-editor-e2e-1789652784706` |
| 增强后源码界面写入 E 盘 | PASS | `sql-editor-e2e-1789653016551/PASS.txt` |
| 增强后实际 EXE 写入 E 盘 | PASS | `sql-editor-e2e-1789653038400/PASS.txt`、`saved.png` |
| 本机 Oracle 保存后 SQL 执行 | PASS，真实报表 SQL 零行包装、绑定日期 | `local-oracle-20260917-215047/PASS.txt` |
| Host 通信、结果与导出回归 | PASS | `host-1789653038687/PASS.txt` |
| 连接配置持久化 | PASS | `connection-persistence-9bdc034da2344cc4b84258967baa726c` |
| XML 来源持久化 | PASS | `report-persistence-1789653043402` |
| Host 构建 | PASS，0 警告、0 错误 | 本轮构建输出 |

证据目录共同前缀：`artifacts/verification/desktop/`。

独立新程序目录：`artifacts/sql-save-verified/ReportDesk-win32-x64/`。原打包目录保留。

没有修改 HIS 原始资料，没有修改数据库数据，没有提交、推送或发布版本。真实业务结果与全部报表兼容性未作验收；旧综合套件失败边界见 `LocalTesting.md`。
