# 查询结果工具栏验收（2026-09-14）

## 本次修改

- 移除主页面报表标题下的状态及 XML 路径文字；“报表位置”弹窗仍保留完整来源。
- “查询结果”标题与修改条件、开始查询、结果查找、筛选、复制和导出保持同一行。
- 当前条件摘要移至工具栏下方，独占一行；超长内容省略并保留悬停全文。
- 保留前序条件弹窗、当前报表内存状态、报表位置、导航及检查新增报表功能。

本次产品代码仅改动 `ui/index.html`、`ui/styles.css`、`ui/renderer.js`。同步现有布局检查、报表位置检查断言及 Desktop 文档。

## 已执行验证

- `scripts/build-update.ps1`：编译 0 警告、0 错误。
- `tests/desktop/report-update-checks.cjs`：隔离旧包的精确基线校验、三文件更新、重复执行、真实 XML 导入、回退和再次更新通过。证据：`artifacts/verification/desktop/report-update-1789377524833/PASS.txt`。
- `tests/desktop/lib-query-conditions-checks.cjs`：使用真实 LIB 的 11 个报表/数据源组合，在 1040×700、1280×720、1920×1080 共 33 组布局检查通过；覆盖标题与按钮同排、摘要在下方、标题元信息移除，以及现有弹窗、lookup、参数状态、错误/取消路径。与上一版客户端的 11 组参数载荷一致。证据及截图：`artifacts/verification/desktop/lib-query-modal-1789377560711/results.json`。
- 人工查看 1040、1280 宽度截图，标题、按钮、条件摘要位置符合要求。
- 新包内前端及 IPC 文件与当前源码一致；Host EXE、Core DLL、query-form.js、main.cjs、bridge.cjs、preload.cjs 与上一版逐字节一致。
- JavaScript 语法检查及 `git diff --check` 通过。未重复运行与本次布局无关的新增报表全套检查。

## 客户端

- 完整目录：`artifacts/desktop/ReportDesk-results-toolbar-20260914`，启动根目录 `ReportDesk.exe`。
- 更新 ZIP：`artifacts/updates/ReportDesk-0.2.0-results-toolbar-20260914.zip`。
- 当前交付基线：`artifacts/desktop/ReportDesk-results-toolbar-20260914`；旧客户端和旧更新包已清理。
- ZIP SHA256：`0DB684105F4A2356A819CEBC7901A50E94F6771E658CB9EDCF68B1B16AF8D434`。

真实 Oracle / HIS 查询结果等价及 Win10 现场验收：**NOT RUN**。状态回归使用明确标记的 UI 空结果/错误响应及真实 LIB 静态选项，不作为数据库执行或业务结果证据。本轮未 commit、push 或部署。
