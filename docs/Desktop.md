# Electron 桌面版（0.3.0）

目标环境：Windows 10 / Windows 11 x64，安装 .NET Framework 4.8。Win7、Win8、32 位系统和网页版不在本轮实现范围。旧 WinForms 源码及构建入口保留用于回归验证；新界面在 `src/ReportDesk.Desktop`，不再用 WinForms 展示主界面。

当前 Electron 首次导入后将目录/XML 路径写入 `import-sources.json`，后续启动自动从原路径读取报表。无收藏、最近、分类和说明编辑；报表定义、查询参数和结果不落盘。连接成功后自动保存 `connection.json`，默认加密保存密码，可取消勾选；旧 catalog 只兼容读取连接设置并保持原样。验收见 [持久化验证](Verification-Persistence-20260914.md)。

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

- 左侧导入 XML 或选择报表根目录；递归按 XML 根节点识别查询和 FarPoint 版式文件，显示导入及关联摘要。报表说明中的「检查关联 XML」显示显式路径匹配、名称候选、冲突、缺失或拒绝原因。匹配文件不代表支持其交叉/映射规则，源目录应保持可访问，具体边界见 [OfflineUpdate.md](OfflineUpdate.md)。选择报表后，右侧直接显示该报表条件，无报表库首页。
- 查询条件在表格上方横排，空间不足自动换行，可收起。文本、日期/时间及 SQL 下拉条件按定义生成。原日期格式在后台转换，不让 JavaScript 改变 SQL 参数格式。
- 下拉条件点击“加载选项”，按需搜索、分段读取，选择编码。“全部”仅原定义提供时出现，不自动选择。
- 查询进度采用不定百分比动画，显示后台当前操作；不提供虚假的 Oracle 完成百分比。手动取消保留；握手阶段仍可能需要等待。无应用查询计时器、行数或内存截断。
- 全量结果留在后台内存，前端每次最多读取 200 行展示。这是传输窗口，不是查询上限。筛选和排序在后台作用于完整结果；导出当前完整视图，不重新查询，不只导出当前段。
- 金额/长数字/日期通过带列类型的字符串传输，避免 JavaScript Number 损失精度。前导零保留；排序使用 DataView 的原始类型。可勾选当前段多行复制。
- 切换报表或数据源、保存连接设置、重新执行查询时清除旧结果。失败/取消不把部分数据显示为完成。
- “报表位置”按钮弹窗展示报表状态、原 XML 路径及已知 HIS 菜单位置，包含来源依据、候选及停用标记；无对应资料时明确显示位置未确认。主界面保留紧凑的状态/来源行（长路径省略，悬停显示完整内容），不展示位置区块或报表用途说明；查询阻塞提示仍保留。“报表说明”展示适配建议及关联文件检查。
- 左侧功能按钮集中在“全部报表”下方，已移除离线演示入口。“检查新增报表并添加”扫描已记住的导入来源，目录来源递归发现新文件，单文件来源仅检查该 XML；按原报表 ID 去重，只添加含 SQL 的独立报表，保留当前报表定义、查询条件和结果。已有 XML 的修改仍可通过重新导入或重启读取。
- 新增检查完成后报告新增总数、当前可显示数量、不完整定义及警告；新条目仍按 report-visibility.xml 过滤。来源失效不删除原条目或已记住的路径，取消时不提交本次新增。

## 数据、安全与配置

- `%LOCALAPPDATA%/ReportDesk/import-sources.json` 仅保存成功导入的目录/XML 路径及类型，支持多来源与重复导入去重；启动自动读取，仍按显示清单过滤。来源不可访问时提示并保留路径，恢复访问后重启重试；配置损坏时提示并保留原文件，不自动覆盖。原 XML 必须仍可访问，不缓存报表定义。
- `%LOCALAPPDATA%/ReportDesk/connection.json` 保存连接配置；「连接并保存」测试登录成功后自动保存，「保存设置」仍可仅保存而不连接。失败或取消测试不覆盖旧配置；保存失败明确提示。首次无此文件时只从旧 catalog 读取连接设置，旧文件原样保留。
- `report-visibility.xml` 在用户启动的 `ReportDesk.exe` 同目录，启动时读取。开发模式在仓库根目录读取。缺文件显示全部、selected 空清单显示零张、错误配置停止启动。所有列表与后台报表操作按 ID 验证清单；不增加岗位编辑入口。它仍是显示偏好，不是授权。
- 密码不回传渲染器；连接窗口可使用后台已保存/当前会话密码，或取消「使用已保存或当前会话的密码」后填写替换。「保存密码」默认勾选，以 CurrentUser DPAPI 加密；取消勾选后密码只在本次会话使用，取消选择也会保存。旧连接文件缺少可选 `RememberPassword` 字段时默认勾选，但只在用户点击连接/保存后写入。换 Windows 用户/机器或无法解密时提示重新输入。
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

真实 Oracle 19c、TNS/SID/多地址、服务器取消行为、业务口径、Win10 x64 干净机以及 Excel/WPS 人工打开检查：**NOT RUN**，沿用 Acceptance.md 现场清单。运行时支持 Windows 10 不等于所有 Windows 10 build 均已实测。

本版本为 Windows x64 完整发布包。它不替代 HIS 现场验收：真实 Oracle 业务口径、HIS 显示/打印及目标机器兼容性仍须按 [Acceptance.md](Acceptance.md) 验证。

同一运行时/依赖的离线小更新可用 `powershell -File scripts/build-update.ps1 -BaseDirectory '上一份完整程序目录'` 生成，再用 `node tests/desktop/update-checks.cjs '生成的更新目录'` 验证隔离旧包的替换、真实 EXE 启动及回退。交付仅包含 app.asar、Host EXE 和 Core DLL；安装脚本检查基线哈希并备份，不替换用户数据或岗位显示配置。完整操作见 [OfflineUpdate.md](OfflineUpdate.md)。

## 查询条件弹窗

- 主页面标题下的一行包括：查询结果标题、修改条件、开始查询、结果内查找、筛选、复制选中行和导出 Excel，保持同一行和垂直居中。当前条件摘要独占其下方一行，超长省略并可悬停查看完整内容。报表标题下不再显示状态及来源路径，完整来源仍可通过“报表位置”查看。
- 点击“修改条件”打开当前 Electron 窗口内的居中模态弹窗，标题为“当前报表名称 - 查询条件”。宽度 760px；按 Details 原始顺序排列，每个条件一行，标签左侧统一宽度、控件右侧对齐。多参数时只滚动内容，顶部关闭和底部操作保持可达。
- 关闭、右上角关闭、Esc、应用均保留当前内存条件并关闭弹窗，不查询、不重新获取默认值；没有取消回滚。“应用并查询”和主页面“开始查询”复用同一个查询入口。操作忙碌时禁止关闭条件弹窗，lookup 仍使用原选项弹窗。
- 参数值、选项显示名、多选编码及成功查询快照仍只保存在当前报表内存 session。输入与 lookup 单选、多选、清空、全选回填同步同一状态；打开和关闭弹窗不请求 select，不清空旧结果。
- 摘要从当前状态及原 Details 派生，省略空值和内部 implicit，保留有效 0 与复选框否。查询成功后保存实际 IPC 快照；编辑后若仍有旧结果且条件不同，显示“条件已修改，结果尚未更新”；改回原值或成功重新查询后消失。
- dirty 比较编码和实际参数值，不比较 DOM、Label 或摘要文字。普通显示用 `.Text` 不参与；原 SQL 定义引用的 `.Text` 仍作为业务值保守纳入比较。文本引用通过既有只读 definition IPC 获取，不执行或改写 SQL。
- 切换报表按原行为新建 session、清空旧结果，不缓存跨报表条件。切换数据源按原行为重新获取 Details、重建参数及清空结果，完成后重新显示条件弹窗。
- 参数校验仍由 Host 负责。明确的现有参数错误打开条件弹窗显示原信息；不猜 Required 或错误字段。未知错误保留原提示。失败/取消不替换最后成功快照；新查询开始时仍沿用 Host 清空旧结果的行为。
- 回归入口：`tests/desktop/lib-query-conditions-checks.cjs <updated-exe> <baseline-exe>`；旧 `lib-query-layout-checks.cjs <baseline-exe> <updated-exe>` 保留转发兼容。验证记录见 `docs/Verification-QueryConditionsModal-20260914.md`。
