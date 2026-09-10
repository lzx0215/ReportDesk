# Electron 桌面开发版验证记录 · 2026-09-10

对象：ReportDesk Desktop 0.2.0 开发版，Windows x64。界面采用已确认的石墨灰、暖白文字、金色强调和细分割线；左侧选择报表直接进入查询，条件横排换行，底部显示执行状态和取消入口。

## 本机检查：PASS

验证环境为 Windows 11 家庭中文版 x64（10.0.22631），Node.js 24.14.1、Electron 44.3.0、.NET Framework 4.8 后台及 Oracle.ManagedDataAccess 19.32.0。以下为开发验证，不代表现场业务验收。

| 检查 | 实际结果与证据（相对仓库根目录） |
| --- | --- |
| 原有构建回归 | `scripts/build.ps1` 成功；x86、x64 各 60 项离线检查及 WinForms smoke 通过。证据：`artifacts/verification/0.1.4/` |
| 新后台构建 | Release x64 构建成功，0 错误、0 警告 |
| 后台离线检查 | 全量导出、分段读取、类型与前导零、过期句柄拒绝、取消及重试、DPAPI、XML 导入、显示清单、日志脱敏通过。证据：`artifacts/verification/desktop/host-1789049680561/PASS.txt` |
| 真实 Electron UI | 模拟报表动态条件、查询、筛选、排序、多行复制、导出、条件收起、元数据文本安全、连接设置通过；待适配导入不可查询；1440/1046 宽度检查及渲染器错误检查通过。证据：`artifacts/verification/desktop/ui-1789049684439/` |
| 打包后独立 EXE | 实际启动 `ReportDesk.exe`，确认 packaged 模式、x64，模拟查询返回 14 行。证据：`artifacts/verification/desktop/package-1789049769125/` |
| Excel 文件结构 | 后台导出包含 181 条筛选结果及表头；UI 导出包含 7 条筛选结果及表头，已核对 ZIP 内工作表 XML |
| 依赖与差异检查 | 固定 npm 锁文件；本轮 npm audit 为 0 漏洞；`git diff --check` 通过 |

开发后台/UI 检查使用隔离数据与配置目录及 offline 开关。打包检查使用隔离 LOCALAPPDATA，只执行 Core 模拟查询。所有检查均未连接真实 Oracle；未使用真实密码、患者或业务结果。

## 现场验收：NOT RUN

- Windows 10 x64 干净机的安装前提、启动与实际使用。
- 真实 Oracle 19c 连接、TNS/SID/多地址、数据库下拉条件、长查询取消及服务器释放行为。
- HIS 报表真实参数、业务口径、科室限制和复杂模板适配验收。
- Excel / WPS 人工打开、格式与现场大结果集性能检查。

继续按 `Acceptance.md` 记录现场结果。当前交付为可体验的开发包，未完成正式发布验收；不宣称新版已在 Win10 或真实数据库上通过。

## 交付边界

新实现位于 `src/ReportDesk.Desktop`、`src/ReportDesk.Host`，复用现有 Core；既有 Core 与 WinForms 源码保持不变。不增加查询超时或结果截断，不暴露 HTTP 接口；岗位显示清单继续由 EXE 同目录文件手工配置。

解压整个 Windows x64 包后启动 `ReportDesk.exe`，目标电脑需要 .NET Framework 4.8。更新真实用户目录前备份 catalog；不要同时运行旧版和新版编辑同一目录。本轮未 commit、push、创建 PR 或部署。
