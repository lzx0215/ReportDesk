# Electron 桌面版（0.2.0 开发版）

目标环境：Windows 10 / Windows 11 x64，安装 .NET Framework 4.8。Win7、Win8、32 位系统和网页版不在本轮实现范围。旧 WinForms 源码及构建入口保留用于回归验证；新界面在 `src/ReportDesk.Desktop`，不再用 WinForms 展示主界面。

## 使用与构建

```powershell
powershell -File scripts/build.ps1
powershell -File scripts/build-desktop.ps1
```

第一个命令验证既有 Core / WinForms 基线。第二个命令构建 x64 后台、运行后台离线检查、还原固定版本 npm 依赖、执行真实 Electron UI 检查，生成文件夹包后再执行独立 EXE 启动及模拟查询检查。构建机需要 .NET SDK、Node.js/npm，以及访问 NuGet、npm 和 Electron 下载源的网络；目标用户电脑不需要 Node.js、npm 或 Oracle Client 安装。

输出：`artifacts/desktop/ReportDesk-win32-x64/ReportDesk.exe`。复制整个文件夹，不能只复制 EXE。首次构建下载 Electron 运行时；离线电脑使用预先打好的包，不在现场下载安装前端资源。

开发运行：

```powershell
dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -o artifacts/host
cd src/ReportDesk.Desktop
npm ci
npm start
```

真实 Electron 离线 UI 检查：在项目根目录执行 `node tests/desktop/ui-checks.cjs`。该检查使用隔离目录和后台 `--offline` 开关，禁止真实数据库连接；测试环境变量仅开发模式有效，不用于生产配置。

## 操作

- 左侧导入 XML 或文件夹；选择报表后，右侧直接显示该报表条件，无报表库首页。
- 查询条件在表格上方横排，空间不足自动换行，可收起。文本、日期/时间及 SQL 下拉条件按定义生成。原日期格式在后台转换，不让 JavaScript 改变 SQL 参数格式。
- 下拉条件点击“加载选项”，按需搜索、分段读取，选择编码。“全部”仅原定义提供时出现，不自动选择。
- 查询进度采用不定百分比动画，显示后台当前操作；不提供虚假的 Oracle 完成百分比。手动取消保留；握手阶段仍可能需要等待。无应用查询计时器、行数或内存截断。
- 全量结果留在后台内存，前端每次最多读取 200 行展示。这是传输窗口，不是查询上限。筛选和排序在后台作用于完整结果；导出当前完整视图，不重新查询，不只导出当前段。
- 金额/长数字/日期通过带列类型的字符串传输，避免 JavaScript Number 损失精度。前导零保留；排序使用 DataView 的原始类型。可勾选当前段多行复制。
- 切换报表或数据源、保存连接设置、重新执行查询时清除旧结果。失败/取消不把部分数据显示为完成。
- 元数据、收藏、最近使用、核对状态沿用原 catalog。导入源变更仍由 Core 重置核对状态。不支持语义仍明确待适配。

## 数据、安全与配置

- 继续使用 `%LOCALAPPDATA%/ReportDesk/catalog.json` 及备份；不自动迁移或改格式。更新前备份用户目录。不要同时运行旧 WinForms 与新版并编辑同一目录，以免互相覆盖。
- `report-visibility.xml` 在用户启动的 `ReportDesk.exe` 同目录，启动时读取。开发模式在仓库根目录读取。缺文件显示全部、selected 空清单显示零张、错误配置停止启动。所有列表与后台报表操作按 ID 验证清单；不增加岗位编辑入口。它仍是显示偏好，不是授权。
- 密码不回传渲染器；连接窗口可保留后台当前密码，或明确填写替换。只有勾选保存密码才用原 CurrentUser DPAPI 存储。连接测试使用未保存的当前输入，不自动保存。
- HTML/CSS/JS 全部随程序发布，不加载远程资源。渲染进程沙箱、contextIsolation、禁用 Node 集成、严格 CSP、禁用额外窗口/导航/权限。Electron 主进程核对 IPC 发起窗口、frame 和来源。
- .NET 是当前用户的隐藏子进程，通过私有标准输入/输出通信，不开 HTTP 端口。查询只能按已导入报表与数据源标识执行，前端没有通用 SQL 执行入口。
- 查询结果、参数、密码不进入 localStorage、浏览器持久会话或临时结果文件。Electron 使用非持久会话；UI 运行目录在用户目录下，不存业务结果。用户明确导出的 Excel 除外。
- 本地日志沿用按日追加。后台有上下文的 Oracle 错误由 Core 脱敏；未知异常只写类型/方法/错误码。新增主进程日志只写固定操作标识，不写原始异常、协议包或输入。日志不上传。
- 关闭时如果仍在执行，会请求取消并提示等待，不按时长强杀查询。后台退出时停止界面操作并提示重启。

## 架构边界

`Electron renderer → preload 白名单桥 → main → ReportDesk.Host (.NET 4.8) → ReportDesk.Core → Oracle.ManagedDataAccess 19.32.0`

Host 负责报表操作、参数转换、结果句柄、筛选排序、导出及连接状态。结果句柄与视图版本防止把旧页面导出为新结果。保留 Core 的 SQL 绑定、TNS 解析、类型拒绝和 Excel 原子导出策略。

未来网页版需要新增经过认证的服务器与会话/权限隔离。本轮不暴露后台网络接口，不把桌面显示清单当作服务端授权。

## 验收边界

真实 Oracle 19c、TNS/SID/多地址、服务器取消行为、HIS 口径、Win10 x64 干净机以及 Excel/WPS 人工打开检查：**NOT RUN**，沿用 Acceptance.md 现场清单。运行时支持 Windows 10 不等于所有 Windows 10 build 均已实测。

当前开发版本不是已完成现场验收的正式发布。本轮没有 commit、push、PR 或自动部署。
