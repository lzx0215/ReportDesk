# Web 设置分区、多连接与导入入口（2026-11-06）

范围：`D:\aiproject\ReportDesk` 当前工作区，网页版。本次只修改本地源码、测试和说明，保留任务开始前已有未提交改动。未修改桌面版、真实连接配置、原始 LIB、数据库或服务器部署；未提交或推送 Git。

## 行为

- 设置拆为数据库连接、本机报表目录、HIS 发布同步三个页签。页签切换保留连接草稿；目录及同步快捷入口不依赖读取数据库配置。左右方向键、Home、End 可切换页签。
- 数据库连接支持新增、选择、编辑、测试保存及显式设为当前连接。新增及非当前档案保存不改变活动连接；切换需要确认。每组密码独立处理，沿用服务器 CurrentUser DPAPI；接口不回传密码，浏览器不持久化密码。
- 左侧两个导入按钮合为一个入口，展开后选择 XML 文件或文件夹。沿用 `uploadImport` → `import` 链路和现有识别规则。
- Web 专属连接存储兼容旧 `connection.json` 根层字段，以原子替换同时保存活动配置及多连接列表。回退说明见 [Web-QuickStart.md](Web-QuickStart.md)。
- 跨进程命名锁及保存前文件指纹检查防止旧列表覆盖新列表。查询切换仅保证同应用进程后续操作；其他实例主动读取设置时可刷新，没有跨实例实时广播。

## 已执行的验证

- `node --check src/ReportDesk.Web/UI/web.js`、`transport.js`：通过。
- `node src/ReportDesk.Web/UI/verification/settings-checks.cjs`：通过。验证页签及草稿、连续键盘导航、新增/编辑/显式切换、失败保留输入、密码隔离、公共页面拒绝、原生文件/目录选择器及上传导入契约。
- `node src/ReportDesk.Web/UI/verification/browser-checks.cjs`：通过。包含普通/维护/拒绝/虚拟目录入口、同步冲突处理及原有浏览器回归。
- `node src/ReportDesk.Web/UI/verification/transport-checks.cjs`、`xml-transport-checks.cjs`：通过。
- `node src/ReportDesk.Web/UI/verification/local-directory-checks.cjs`：39 项通过。浏览器文件 API 模拟，使用留存真实 XML 校验文件字节与恢复路径。
- `local-directory-e2e.cjs` 已按新页签调整入口并通过语法检查；本轮未重跑服务端 OPFS 端到端流程。
- net462 HTTP 检查构建及运行通过，71 项 HTTP/权限检查通过；后续仅完善连接档案内部故障保护，未重复运行无关 HTTP 项。
- 最终当前 Core 与精确旧 Core 的 net462 构建均为 0 警告/错误，且各自实际运行连接管理专用检查 42 项通过：旧格式兼容、独立密码及 DPAPI 重载、非当前保存不激活、测试失败/取消/写失败保留旧值、TNS 批准撤销及公共调用拒绝、旧查询快照、过期写入拒绝与重新读取后的显式重试、首次读取活动连接一致性、坏配置保留。最终旧 Core 输出为 `artifacts/verification/settings-20261106-review-legacy-final`，全部原依赖 SHA-256 保持一致。
- 新设置界面在 Chromium 1440 与 390 宽度下截图检查，通过本次弹窗/菜单范围检查；不将此结果扩大为整个旧主界面的移动端适配。
- Impeccable detector 对 `web.js`、`web.css` 返回空问题列表；相关已跟踪文件 `git diff --check` 通过。

界面证据：`output/playwright/settings-20261106/`，含三页签和导入菜单的桌面/窄屏截图及 `summary.json`。这些是明确标注的离线协议夹具，不是 Oracle 查询结果。

专用连接验证复跑（仓库根目录；输出改用未占用目录，使用合成连接配置，不访问 Oracle）：

```powershell
dotnet build src/ReportDesk.Web/ReportDesk.Web.csproj -c Release -p:ReportDeskWebTargetFramework=net462 -o artifacts/verification/settings-20261106-backend
dotnet build tests/ReportDesk.Web.ConnectionProfiles.Checks/ReportDesk.Web.ConnectionProfiles.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 -o artifacts/verification/settings-20261106-profile-checks
& artifacts/verification/settings-20261106-profile-checks/ReportDesk.Web.ConnectionProfiles.Checks.exe artifacts/verification/settings-20261106-backend
```

成功时末行显示 `PASS 42 connection profile checks; Oracle NOT RUN`。检查 EXE 以参数目录中的实际 Web DLL 运行，可用于精确旧 Core 构建的同一回归；不是向真实数据库发送查询。

## 未验证

真实 Oracle 登录、报表权限与业务结果、现场 IIS 4.6.2 运行、生产配置迁移及跨进程现场行为均未验证。本地 net462 编译和高版本 .NET 运行时上的测试不替代现场验收。
