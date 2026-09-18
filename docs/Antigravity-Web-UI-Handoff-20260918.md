# ReportDesk Web 页面修改交接（Antigravity）

日期：2026-09-18。工作区：`D:\aiproject\ReportDesk`。本次任务是调整现有 Web 页面和交互；用户要求内网、少量使用者、简单实用。视觉风格尚未指定，可先提出页面效果，但不扩大为后端重写或复杂系统改造。

## 1. 先了解当前状态

- 前端为原生 HTML/CSS/JavaScript，无 React/Vue 构建链。浏览器经 HTTP API 调用后端，同一个 IIS 站点提供页面与接口，无需独立前端服务器。
- Web 后端是 ASP.NET / .NET Framework。服务器实际 Release=394802，即 4.6.2；已经生成独立 net462 兼容包。桌面客户端默认仍为 net48，必须保留。
- 最新已交付包：`artifacts/web/ReportDesk-Web-net462-20260918-150154.zip`，SHA256：`CEB374046D8F494103E81EB27F0EA17303C5C0DF9661FB0AC2595533F757B768`。
- 用户最后截图中 IIS 站点/池为 `ReportWeb`，HTTP 端口为 8083，站点目录显示为 `C:\a_userspace\ReportTool\ReportDesk-Web-net462-20260918-150154`。这些是截图时的服务器信息，修改前应现场确认。
- 最新浏览器响应为“请使用 HTTPS 地址访问”：表明请求已到应用的协议检查，不能据此宣称目录、查询、数据库或全部启动流程验收成功。
- 已指导用户把部署副本的 `ReportDesk.AllowLoopbackHttp` 改为 `true` 以便 `http://localhost:8083/` 本机检查，但尚未收到修改后成功进入页面的证据。此设置不允许其他电脑使用 HTTP。远程 HTTPS 尚未确认配置完成。
- 默认包 `Offline=true`，维护 IP 清单为空，同步关闭；不能将默认包当作已配置的生产站点。
- 工作区存在多项未提交修改与未跟踪 Web 文件，包含本轮全部 Web 实现。先看 `git status --short`，不要 reset/clean/restore 或仅从远端旧提交开始修改而丢失这些功能。

## 2. 从哪些文件开始改

所有相对路径均以工作区根目录为基准。先阅读根目录 `AGENTS.md`。

| 文件 | 作用与修改提示 |
| --- | --- |
| `src/ReportDesk.Web/UI/index.html` | Web 页面骨架，共用查询/维护入口；包含大量已绑定事件的 ID |
| `src/ReportDesk.Web/UI/web.css` | Web 专属样式、维护能力隐藏和错误状态；优先在这里调整布局/颜色/间距 |
| `src/ReportDesk.Web/UI/web.js` | 等待会话能力后加载共享脚本；维护界面、同步状态/冲突、Web 帮助与导入适配 |
| `src/ReportDesk.Web/UI/transport.js` | `window.reportDesk` 适配器；会话、CSRF、任务轮询/取消、下载、定义版本和编辑令牌，尽量保持协议不变 |
| `src/ReportDesk.Desktop/ui/renderer.js` | 查询、列表、结果表格、连接、弹窗等共享业务交互 |
| `src/ReportDesk.Desktop/ui/query-form.js` | 真实报表条件、动态选项、依赖、多选等共享组件 |
| `src/ReportDesk.Desktop/ui/sql-editor.js` | SQL 与列设置编辑器，共享实现 |
| `src/ReportDesk.Desktop/ui/styles.css`、`execution.css`、`sql-editor.css` | 默认从桌面复制到 Web 包的样式 |
| `src/ReportDesk.Web/WebRuntime.cs` | HTTP 路由、静态资源白名单、任务/会话、安全边界 |
| `src/ReportDesk.Web/Services/API.md` | 业务桥接接口说明；实际代码优先 |
| `scripts/build-web.ps1` | 完整 Web 包构建、复制 UI、生成配置重定向/哈希清单 |

打包顺序：先复制指定桌面 UI 资源，再复制 `src/ReportDesk.Web/UI` 全部顶层文件。因此需要 Web 独有版本时，可在 Web/UI 下提供同名资源覆盖包内文件，避免改变桌面效果；如复制共享 JS，记录来源和差异，避免形成两份失同步业务逻辑。一般视觉调整优先使用 web.css。

不要仅改 `artifacts/.../UI` 发布副本，否则下次构建会丢失修改。

## 3. 页面结构与必须保留的功能

普通入口 `/` 免登录；维护入口 `/admin` 由后端校验实际连接 IP。二者共用 HTML，通过服务端授予能力决定界面。

| 区域 | 必须保留 |
| --- | --- |
| 报表导航 | 真实报表列表、搜索、分类、数量、选中状态；报表位置与来源、说明、待适配提示 |
| 查询条件 | 修改条件弹窗、数据源切换、日期/文本/静态选项/动态字典、多选、依赖条件；编码与显示文本分开处理 |
| 条件动作 | “应用/关闭”只保留编辑，“应用并查询”才执行；条件已修改但结果未更新的提示 |
| 查询执行 | 阶段进度、忙碌状态、取消、失败提示；取消要等待后端实际完成，不能点击后直接显示已取消 |
| 结果表格 | 分段显示、筛选、排序、选择/复制、完整 Excel 下载、空结果、主/明细可用入口 |
| 维护功能 | 导入批准目录内 XML/文件夹、检查新增、静态复核/重载、连接设置/TNS/连接测试、日志提示 |
| SQL 编辑 | 打开/复制/静态检查/保存、只读与待适配提示、未保存草稿确认、并发文件变化检查 |
| 列设置 | SchemaOnly 列识别、主/明细模板、预览/保存、SQL 已保存后补齐模板；不能只保留 SQL 文本框 |
| 自动同步 | 状态、立即检查、失败/待处理、冲突确认、采用 HIS/保留本地；显示真实后台状态 |

列描述、动态字典、真实查询需 Oracle；页面验收不能用假患者结果代替。同步从 HIS 发布表读取并处理批准的 XML/模板，不执行 HIP，不更新整个 HIS 客户端。保存本程序 XML 不等于发布回 HIS。

## 4. DOM 与加载约定

当前顺序：HTML 加载 `transport.js` → `web.js` 等待 `api.ready` → 顺序加载 `query-form.js`、`renderer.js` → 维护模式加载 `sql-editor.js` → 初始化维护增强。共享脚本之间存在全局函数/状态依赖，不能直接改成异步乱序或模块脚本。

关键 ID 例子（不是完整清单，改结构前必须搜所有引用）：

- 导航：`report-search`、`report-list`、`library-count`、`report-title`、`issues`。
- 查询/结果：`summary-edit`、`query`、`query-summary`、`query-dirty`、`source`、`parameters`、`table-wrap`、`result-count`、`filter-form`、`result-filter`、`previous`、`next`、`copy`、`export`。
- 条件弹窗：`query-conditions-view`、`conditions-dismiss`、`conditions-back`、`conditions-apply`、`conditions-query`。
- 进度：`execution`、`operation-status`、`progress`、`cancel`。
- 维护：`import-file`、`import-folder`、`check-new`、`settings-open`、`logs`、`sql`、`connection`。
- 其他弹窗：`notice`、`locations`、`metadata`、`lookup`；同步与编辑器部分节点由 JS 动态创建。

JS 也依赖 `.nav strong`、`footer span`、`footer span:last-child` 等结构选择器。不要只保留 ID 就认为结构一定兼容。`hidden`、`dialog.showModal()`、焦点、Escape、脏状态确认要正常工作。

`web.css` 中的 `[hidden]`、普通入口隐藏维护控件、未授权/失败状态不可被新样式覆盖。隐藏按钮不能替代后端权限校验。

## 5. 前后端协议要点

优先继续通过 `window.reportDesk` 调用，沿用现有 renderer/query-form 的调用模式。不要把适配器包装后的 `{ok,data/message,cancelled}` 与原始 HTTP 响应结构混为一谈。

- `GET /api/session` 初始化会话、CSRF 与能力；所有修改/提交请求携带 `X-ReportDesk-CSRF`，请求采用同源 Cookie。
- 普通方法：`bootstrap/list/select/definition/relatedFiles/query/view/page/lookup/lookupPage/lookupAll/clear/export`。
- 维护方法：`settings/saveSettings/testConnection/import/checkNewReports/recheck/reloadReport/sqlEditorOpen/sqlEditorCheck/sqlEditorSave/sqlEditorReveal/layoutPreview/layoutSave/discoverTns/tnsAliases/syncStatus/syncNow/resolveSyncConflict/openLogs`，由 `/api/admin/` 路由处理。维护页的 relatedFiles 也走维护路由。
- 提交主体为 `{args, tabId}`；可能返回异步任务，适配器轮询 `/api/tasks/{id}`、DELETE 取消、从 `/api/downloads/{id}` 下载。
- tabId 每页独立；不得改成所有浏览器/标签共享的常量。
- select 的 `definitionHash` 由适配器记录，query/lookup 提交时必须带当前定义版本。不能去掉过期定义拦截。
- SQL 保存/列保存沿用编辑会话、预览令牌和确认流程；冲突处理保留文件指纹及过期确认校验。
- POST 中断可能已经被服务器受理，不自动重试查询或保存；会话失效不能自动重跑。
- 密码不回填明文、不存 localStorage/sessionStorage。参数值与查询结果不新增持久化。

## 6. 资源与浏览器约束

- 资源 URL 为 `/assets/...`，实际文件位于网站 `UI`。后端会处理 IIS 虚拟目录基路径，不能把 API 地址写死为 localhost:8083。
- 后端静态白名单当前包含：index.html、styles.css、execution.css、sql-editor.css、query-form.js、renderer.js、sql-editor.js、close-emblem.svg、transport.js、web.css、web.js。
- 新增文件除了打包还需检查白名单与 MIME 类型。仅把图片/字体放入 UI 可能仍返回 404 或错误类型。
- CSP 限制同源脚本/样式/图片/字体；使用外部 JS/CSS 文件，不增加 CDN、内联脚本、远程字体或外部分析服务。
- 使用支持现代 JS、原生 dialog 的 Chromium 内核；服务器截图为 360 浏览器，但内核版本未确认。现场异常先检查实际浏览器模式和控制台。
- 重点检查宽表横向滚动、长报表名、长条件、多选列表、小屏/缩放、弹窗底部按钮可见性。新的视觉风格尚未由用户指定，不凭空增加业务导航或未实现功能。

## 7. 构建与本地预览

在仓库根目录运行：

```powershell
powershell -NoProfile -File scripts/build-web.ps1 -TargetFramework net462
```

必须传 `net462`，默认仍为 net48。脚本输出一个新的完整目录，**不自动生成 ZIP**；保留旧包，不覆盖旧输出。新包 Web.config 的两个目标版本与 DLL 都是 4.6.2；源码 Web.config 仍保留 4.8，构建时转换并合并绑定重定向。

无需真实 Oracle 的预览：

```powershell
dotnet build tests/ReportDesk.Web.Checks/ReportDesk.Web.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 --force
```

再运行 EXE（替换两个占位路径为绝对路径）：

```powershell
& .\tests\ReportDesk.Web.Checks\bin\Release\net462\ReportDesk.Web.Checks.exe --serve '<新构建的网站目录>' '<独立预览数据目录>'
```

访问 `http://localhost:18765/` 和 `/admin`。该预览采用离线模式、允许本机维护，使用实际 Web dispatcher，但不是 IIS。预览数据使用已有真实 LIB 的隔离副本；原库 `D:\系统知识库\00_Inbox\yljhis\LIB\LIB` 保持只读。查询不能联网执行；预览界面不能被标注为真实 Oracle 验收。

浏览器查看需同时确认控制台与网络请求，不只看静态截图。纯静态双击 HTML 无法验证会话和接口。

## 8. 按改动范围验收

- 页面：普通/维护入口均检查，初始加载/空目录/长列表/查询条件/忙碌/失败/空结果/结果表格/弹窗/编辑草稿/同步关闭状态，重点验证实际修改路径。
- 隔离：普通页仍无维护能力，未授权 IP 请求仍拒绝；两个标签不相互覆盖任务。
- 共享 JS 有修改时，补桌面回归；Web 专属 CSS 不需要无关的数据库测试。
- 可用现有检查：`node tests/desktop/sql-editor-checks.cjs`；net462 的 `ReportDesk.Web.Checks.exe`（HTTP/安全）；`Services/BridgeChecks.csproj`（业务桥接）；`tests/ReportDesk.Web.Sync.Checks`（同步）。按实际变动选择。
- 包检查：`powershell -NoProfile -File tests/Test-WebPackage.ps1 -PackageDirectory '<新包绝对目录>'`。若修改包内容，重新构建生成 manifest，不沿用旧哈希。
- 交付改动文件清单、前后截图、实际通过/失败/未执行项以及完整新包。明确服务器替换范围，不只交一张效果图或修改后的 index.html。

既有基线：net462 编译、HTTP/安全 30 项、业务桥接 17 项（1,284 报表元数据）、同步 17 组、SQL 编辑前端 29 项、包哈希与本机 ASP.NET 预编译通过。测试机运行时高于 4.6.2；这些是上轮结果，页面改完后不能直接复用为本轮已测。

未验证：真实 Oracle/HIS 业务等价、服务器完整使用流程、服务身份 DPAPI、远程 HTTPS、实际发布 BLOB。已知限制：刷新页面不恢复旧任务；16 页面/会话、64 会话、默认空闲 60 分钟；同步每轮仍读取历史 BLOB，不能承诺大历史量下 30 秒完成。

## 9. 交付和参考材料

页面修改不授权连接真实数据库、修改 HIS SQL 口径、部署、提交或推送代码。保留当前维护 IP、CSRF、同源和 HTTPS 边界；用户若要改变访问方式，单独明确该需求。

现场更新需备份网站与配置，使用完整新包并迁入现场 ReportDesk.* 设置；保留新包绑定重定向及 4.6.2 目标。只操作 ReportWeb 自己的应用池；不做全局 iisreset。旧 net48 包无法作为当前 4.6.2 服务器的可运行回退版本，应保留已交付 net462 基线。XML 数据变动与程序回退分别处理。

进一步阅读：

- `docs/ReportDesk-Web-IIS-Minimal-Plan-20260918.md`：当前简化方案与全功能清单。
- `docs/Web-QuickStart.md`：部署与功能边界。
- `docs/Web-net462-Compatibility.md`：4.6.2 编译、验收与回退。
- `docs/Verification-Web-20260918.md`：首版历史验证及未完成项。
- `docs/ReportDesk-Web-IIS-Technical-Plan-20260918.md` 为较早详细方案，不作为扩大本次任务范围的依据。

## 10. 可直接发给 Antigravity 的任务说明

> 请在 D:\aiproject\ReportDesk 修改现有 ReportDesk Web 页面。先阅读 AGENTS.md 和 docs/Antigravity-Web-UI-Handoff-20260918.md，检查未提交改动，并以当前工作区为基线。目标是内网少量用户的实用页面优化，优先修改 Web/UI 的 HTML/CSS/必要交互，保留现有查询、条件、结果表格、导出、SQL/列维护、连接与报表同步功能。桌面版默认 net48 保留，Web 交付须使用 net462。遵守文档中的 DOM、加载顺序、接口和权限约定；需要变动时同步更新调用点并验证。依据我提供的样式要求或参考图实施；未指定的视觉细节采用简洁一致的风格。完成后实际预览普通页和维护页，提供截图、验证记录、改动清单及完整兼容包，不连接真实 Oracle、不自动部署或提交推送。
