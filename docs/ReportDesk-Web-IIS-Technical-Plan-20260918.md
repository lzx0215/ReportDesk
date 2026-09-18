# ReportDesk Web / IIS 全功能技术方案

> 本稿已被用户要求简化。后续方案以 [内网小规模简化方案 v1.1](ReportDesk-Web-IIS-Minimal-Plan-20260918.md) 为准。本文保留为历史功能核对和证据索引；其中双站点、.NET 10 迁移、完整版本发布系统及 50 会话目标不再作为实施要求。旧表中的 Web 去向若与简化方案不同，以简化方案为准。

日期：2026-09-18。版本：评审稿 v1.0。范围：技术方案与功能核对，不是已实现的 Web 程序或上线验收报告。

## 1. 结论与本次确认

需要设计 Web 接口、服务端状态和维护入口；查询页面可沿用现有 Electron 页面风格与操作顺序，无需整体换一套界面或引入大型前端框架。

本方案采用两个访问边界：

- **查询站点**：一个院内网址，免登录，所有使用者查看同一报表目录；保留条件查询、结果筛选排序、复制、Excel 下载等能力。
- **维护站点**：用户本轮已明确要求提供网页维护入口，并与免登录查询隔离、限制访问；包含来源导入、连接配置、SQL 编辑、报表列同步、版本与同步维护。
- **原桌面客户端**：保留现有功能与运行方式。Web 新的配置、标识和版本格式独立，不直接改写桌面 catalog、connection 或 import-sources 文件。

“覆盖所有功能”在本文中指：对当前 Electron 0.3.0 的全部用户功能、Host 方法、Core 业务规则及保留的旧 WinForms 功能逐项给出去向；不把未实现的 HIS 打印引擎、条件设计器或自动明细联动写成已有能力。当前 Electron 功能全部纳入 Web 查询或网页维护，不能以“第一版只读”为由漏掉最新编辑功能。旧 WinForms 独有功能另列，避免无意恢复已从当前产品移除的入口。

## 2. 证据、版本与交接差异

### 2.1 本轮核对的基线

| 项目 | 核对结果 |
| --- | --- |
| 本地分支 | `main` |
| 本地 HEAD | `e54c88ffc6416911eaf6e3a62729b057b2a05552` |
| GitHub main | 本轮执行 `git ls-remote origin refs/heads/main`，与上述 HEAD 一致；不代表未合并开发分支内容 |
| 当前桌面版本 | `src/ReportDesk.Desktop/package.json` 为 `0.3.0` |
| Web 项目 | 尚不存在；本文所有新项目、HTTP 路由和配置均为设计 |
| 原交接 | `ReportDesk-Web-IIS-Handoff-20260918`，方案与证据包；保留原文件及 SHA256 清单 |
| 本轮变更前工作区 | 只有用户交接目录和 ZIP 未跟踪；不覆盖或纳入本方案修改 |
| 环境报告 | 用户提供的 `新建 文本文档 (2).txt`，采集时间 2026-09-18 10:06:59 +08:00；本轮已解析 JSON |

### 2.2 昨晚至今早的功能必须覆盖

| 提交 / 当前证据 | 变化 | Web 对应设计 |
| --- | --- | --- |
| `166c6a9`，2026-09-17 22:39 | SQL 原文件回写、磁盘回读校验、工具栏及来源定位改进 | 维护站点 SQL 编辑、保存状态、路径定位替代功能 |
| `190a23e`，2026-09-18 08:09 | Oracle 字段描述、主表/明细模板列同步、列预览、双 XML 保存 | 维护站点完整列同步流程 |
| 同一提交与 `ReportLayoutEditing.md` | SQL 已保存、模板列不足时按原表头可靠匹配并补齐；不重复插列 | 独立验收路径，不能只覆盖“SQL 和模板一起首次修改” |
| `SqlXmlEditor.Save` / `ReportLayoutEditor.Save` | 保存不生成 `.bak`；未变化文件不重写；外部变化拒绝覆盖 | 继续保留；不得照抄旧“每次保存自动生成 .bak”说明 |
| `sql-editor.js` / `close-emblem.svg` | 编辑器右上角关闭、草稿放弃确认、Ctrl+S、SQL 未变仍可同步模板 | 保留交互语义，浏览器关闭提示按能力实现 |
| `QueryConditionEditing.md` | 解释如何修改条件定义；并未新增图形化条件设计器 | 文档入口保留，不虚构条件设计功能 |
| `renderer.js` / `index.html` | 实际查询条件是模态弹窗，具有条件摘要和“条件已改、结果未更新”提示 | 以现代码为准；旧文档的横排条件描述不能作为 UI 基线 |

代码事实优先于旧说明：原始 .NET 公共版本仍为 `0.1.4`，不等于当前 Electron 产品版本；旧 README/验收表中“不自动保存连接”“只返回原始表”等历史描述不能覆盖当前实现。

### 2.3 服务器实际条件

| 项目 | 本次环境报告 | 设计影响 |
| --- | --- | --- |
| 系统 / IIS | Server 2016 Standard x64，10.0.14393；IIS 10 | 安装前核对完整补丁修订号与运行时依赖 |
| 资源 | 8 个逻辑处理器，16 GB 内存 | 现有业务负载未知，不能承诺 50 条大查询并行 |
| 磁盘 | 本次仅枚举 C 盘：99.5 GB，总剩余 73.7 GB | 示例改用 C 盘；上线时保留系统及既有业务空间 |
| 运行时 | PATH 未找到 dotnet；标准位置未找到 ASP.NET Core Module | 尚不能认定已具备 Web 运行环境，也不能据此断言机器任何位置都没有 .NET |
| 现有网站 | Default Web Site：80；InfectUpload：8080；ManagementPortal：8081，均 Started | 不覆盖现有站点或绑定；安装/重启要安排维护窗口 |
| 补丁 | 报告仅列出 3 个 KB | 安装日期不等于补丁新旧；列表不足以证明更新完整 |

目标为 50 个使用会话及代表性混合负载。查询吞吐量、排队时间和内存预算须现场测试后填写。

## 3. 全功能覆盖矩阵

标识用于开发任务与验收追踪。Q＝免登录查询，M＝受限网页维护，D＝原桌面保留，N＝当前不存在/不默认新增。

### 3.1 报表来源、目录和说明

| ID | 当前能力与实现依据 | Web 去向及验收要点 |
| --- | --- | --- |
| F01 | 目录递归扫描、单 XML 导入：`ReportFileDiscovery`、`ReportImporter` | M：选择已登记服务器来源或其中的 XML；复用扫描器。Q 不提交任意服务器路径 |
| F02 | 按 XML 根节点识别查询/Spread/其他；无 SQL 定义单列；扫描不跟随链接 | M：保留成功、待适配、不完整、其他和错误统计；不删除原文件，不把无 SQL 定义判为非业务报表 |
| F03 | 多来源路径记忆、启动恢复、失效来源告警且保留记录 | M：服务器来源注册表；启动恢复。Q 展示最近可用目录和更新时间；扫描失败不清空目录 |
| F04 | `checkNewReports` 只添加新报表，去重、取消不提交；不刷新旧定义 | M：保留“只检查新增”；另设“重新扫描并更新定义”，界面明确二者不同；不因只新增而清空其他人的结果 |
| F05 | 全部报表、中文排序、名称/别名/用途/位置搜索、可显示数量 | Q：保留；路径搜索使用来源相对路径，普通接口不暴露服务器绝对路径 |
| F06 | 按数据源判定可查询/待适配；一张报表可部分可用 | Q/M：逐数据源显示状态与阻塞原因；不能因部分可用解除其他数据源的阻塞 |
| F07 | 说明、适配建议、已知 HIS 菜单位置，多路径/来源/候选/停用标记 | Q：保留说明与菜单来源；未知保持未知。M：查看完整技术诊断；不推测真实菜单 |
| F08 | 关联 XML：显式匹配、名称候选、多候选、缺失、拒绝 | M：完整关联诊断；Q：脱敏状态摘要。候选不能变成“已确认归属” |
| F09 | 手工 `report-visibility.xml` 显示清单，错误配置阻止启动 | D 保持；Web 当前全员相同全量目录，不照搬某岗位清单限制某用户。保留部署级校验工具，不新增岗位权限系统 |
| F10 | `recheck` 批量静态复核、`reloadReport` 单报表重载 | M：两种操作独立；复核不等于 Oracle 实测；新定义只能校验后发布到运行目录 |

### 3.2 查询条件、查询与结果

| ID | 当前能力与实现依据 | Web 去向及验收要点 |
| --- | --- | --- |
| F11 | 选择主表/明细等数据源，排除 `ConditionUsing` 作为普通查询入口 | Q/M 试查：保留；主明细各自执行，不承诺同一快照或自动联动 |
| F12 | 条件弹窗、按定义顺序、关闭/应用保留输入、应用并查询、Esc 与焦点 | Q：沿用现界面；关闭不是撤销，重开不重置默认值，忙碌时正确禁用 |
| F13 | 文本、日期/日期时间、复选框、自定义下拉、TreeSelect/多选、RegisterID | Q：逐类覆盖；树类目前以编码选择呈现，不虚构 HIS 患者树引擎；RegisterID 不拿住院号/姓名替代 |
| F14 | `CustomFormat`、AddDays/AddMonths、默认值、PadLeft、LIKE 转换 | 服务端复用规则；日期仍按定义格式绑定，不自动修改结束日范围或业务口径 |
| F15 | 静态选项、字典/科室/员工选项、条件数据源批次与递归参数依赖 | Q：按需加载、搜索、分页；保持 ID/NAME、原 SQL 顺序、`.Value`/`.Text` 和参数传递语义 |
| F16 | 选项单选/多选、清空、“全部编码”和原定义的全部值 | Q：保留；`lookupAll` 当前取整个已加载选项表而非只取筛选页。不得把“全部”擅自解释为删除科室条件 |
| F17 | 已执行条件快照、当前条件摘要、dirty 提示、参数错误回到条件弹窗 | Q：保留；恢复原条件应消除 dirty；失败和取消不更新成功快照 |
| F18 | 只读 SQL 校验、绑定参数、中文/多选参数转换、隐含上下文编码 | 服务端保留 `SqlTemplate`；Q 只接收报表/数据源标识及值，不接受 SQL；不伪造 admin、当前科室或当前角色 |
| F19 | 查询进度、不定百分比、取消、握手等待提示 | Q/M：异步任务及阶段文本；取消完成以资源释放为准；保持无固定应用查询超时、无静默行数截断 |
| F20 | 结果全量处理、每页最多 200 行、总数/筛选数、NULL | Q：200 是传输窗口，不是结果上限；失败或取消不显示部分数据为成功 |
| F21 | 完整结果搜索及原类型排序、视图 revision、错误时恢复旧视图 | Q：改成每任务独立且可冻结的视图；翻页/导出必须引用对应 viewId，防并发错页 |
| F22 | 当前页选择多行/全选、带表头复制、制表符和换行转义 | Q：使用浏览器剪贴板；失败提供可选择文本，不把患者内容送到日志接口 |
| F23 | 完整筛选排序视图导出，不重新查询；查询结果与导出说明双工作表 | Q/M 试查：浏览器下载；继承文本安全、日期文本、长数字精度、冻结表头和筛选设置 |
| F24 | XLSX 容量与字符校验 | 保留当前最多 1,048,575 数据行、16,384 列、单元格 32,767 字符等拒绝行为；不把格式限制当数据库查询限制，不静默截断或擅自拆表 |
| F25 | 结果数字/日期按类型字符串传输，前导零及高精度保留 | Q：金额、标识不经 JS Number 强制转换，NULL 与空串显示规则保持；服务器按原类型排序 |
| F26 | 切报表/数据源、重查、改连接、编辑后重载清除旧结果 | 当前标签页按原行为更新；Web 显式绑定版本，不能清除其他会话任务；跨版本展示/导出规则见第 8 节 |
| F27 | `HisResultRules` 交叉/组合/合计/行分组及 `TabularReportAdapter` 后处理 | 共用已支持规则；交叉目前限一个交叉列字段和一个指标等支持范围；失败不退化为原始表冒充结果 |
| F28 | 门诊处方特定版本的哈希/配套文件适配；纯 AddMap 与数据源引用边界 | 保留源文件字节及已核对适配条件；不能凭同名报表启用。未适配规则继续阻塞，主明细手工条件传递保持 |

### 3.3 SQL 编辑与报表列同步（本次重点）

| ID | 当前能力与实现依据 | Web 去向及验收要点 |
| --- | --- | --- |
| F29 | 打开原 XML SQL，多数据源；主表/明细可编辑，条件 SQL 与演示只读 | M：完整 SQL 编辑页，沿用编辑范围；编辑会话绑定维护身份、报表版本和文件哈希 |
| F30 | 复制完整 SQL、静态检查、未定义参数提示、Ctrl+S | M：完整保留；静态检查不连 Oracle。当前检查失败不阻止保存文件，但查询仍必须通过执行策略 |
| F31 | 单 SQL 精确回写、编码/BOM、XML 转义/CDATA、大小限制 | M：复用 `SqlXmlEditor` 写维护工作副本，不重序列化整份查询 XML；SQL 上限保持现有 1,048,576 字符，与查询结果容量无关 |
| F32 | 无变化不写、哈希冲突拒绝、磁盘回读、保存成功但重载失败单独说明 | M：保留并增加 HTTP 重试幂等；不能返回笼统失败诱导重复保存；默认不生成 `.bak` |
| F33 | 单报表重载、外部修改检测、旧定义阻止查询 | M：保留；重载不全 LIB 重扫。Q 中旧已完成结果可作为旧版本快照查看，新提交校验版本 |
| F34 | 草稿标志、切数据源/重载/关闭确认、取消保留草稿 | M：仅页面内存保存草稿；`beforeunload` 是尽力提示，不能保证浏览器崩溃恢复；不在 localStorage 放 SQL/参数 |
| F35 | “打开原报表位置”由 Electron 打开资源管理器 | M：改为查看来源、相对路径、哈希、关联文件及受控下载；浏览器不远程启动服务器资源管理器，不提供任意文件浏览 |
| F36 | `SchemaOnly` 分别描述原/新 SQL、绑定当前条件，不读取明细行 | M：异步字段描述任务，允许取消；需 Oracle 授权现场实测，不能把 SchemaOnly 视为无需数据库权限或零风险 |
| F37 | 主模板按命名候选、明细按显式引用，唯一性/Tag/结构校验 | M：保留匹配说明及确认，只有唯一且符合规则的模板可继续 |
| F38 | 前/中/后插列、表头/列宽修改、隐藏列和邻近样式保持 | M：列预览完整迁移；表头最多 128 字，列宽 24–1000 整数；列顺序依 SQL，不能单独拖表头改变映射 |
| F39 | SQL 已保存后补齐模板，原表头唯一按序匹配，重复同步不重复加列 | M：独立入口状态；SQL 未改时也能同步，仅模板变更不能重写查询 XML 或改变其修改时间 |
| F40 | 双 XML 准备、预览 token、确认、仅变更文件写入、回读、失败撤回 | M：工作区保留这套语义，运行版本采用完整版本提交；不把当前双文件替换描述成原子事务 |
| F41 | 拒绝删除/重排/重命名旧字段、重复字段、复杂表头/公式等 | M：原拒绝条件及原因全部保留；支持整行合并标题不等于支持局部/多层合并；不提供 FarPoint 打印预览 |

### 3.4 连接、运维与保留功能

| ID | 当前能力与实现依据 | Web 去向及验收要点 |
| --- | --- | --- |
| F42 | 直连主机/端口/Service Name、TNS 文件/别名，SID 经 TNS | M：完整配置；目标端点由部署白名单约束，Q 不提交数据库地址或凭据 |
| F43 | TNS 自动发现、选择文件、刷新别名，唯一来源才自动选择 | M：只检查服务器批准的 TNS 根及显式登记文件；原桌面发现逻辑保留。浏览器本机 TNS 不能当服务器路径 |
| F44 | 测试连接成功后保存、单独保存不连接、失败/取消不覆盖旧配置 | M：保留两种动作并显示“已测试/未测试”；保存具版本号，双人修改拒绝覆盖；测试成功不代表报表权限齐全 |
| F45 | 默认记住密码、取消记住、使用已保存密码、密文不可解密提示 | M：保留选择，密码只写不读；跨两个 Web 进程的新凭据机制见第 9 节，不复用桌面用户 DPAPI 密文 |
| F46 | 按日脱敏错误日志、界面错误上报、打开日志目录、帮助 | Q：关联号、帮助与自己的任务错误；M：脱敏日志筛选/下载和运行状态。不开任意路径读取接口 |
| F47 | 完全本地静态资源、CSP、来源检查、旧单实例/窗口关闭/隐藏 Host | Web 静态资源离线随包；IPC 校验替换为会话、权限、CSRF 校验；单实例改为服务生命周期；关闭页不终止他人任务 |
| F48 | 完整桌面包与精确基线三文件更新/回退 | D 继续保留；Web 单独发布包、manifest、IIS 配置和版本切换脚本，不能直接用桌面更新 ZIP 更新网站 |
| F49 | 本地离线模式、模拟验证分支、历史 demo 方法 | D/测试保留；生产 Web 不暴露演示入口，不把模拟结果用于 HIS 验收 |
| F50 | 源码有旧 WinForms 收藏、最近使用、分类、说明/别名编辑、已核对标记 | 当前 Electron 无这些入口；旧 WinForms 保留。Web 使用既有说明元数据，但默认不恢复个人收藏/历史/编辑标记功能；如需恢复，另定匿名身份与持久化规则 |
| F51 | 新增/编辑查询条件的图形界面、任意 HIS 模板设计、打印、右键主明细联动 | N：当前程序不存在完整实现；Web 提供已有使用文档，不虚构迁移完成，不承诺任意 HIS 报表等价 |

## 4. 页面与交互设计

### 4.1 查询站点

沿用暖浅色、左侧报表搜索列表、右侧标题/结果区、单行操作栏、底部任务状态的现有布局。首轮不做品牌重设计，不引入 React/Vue 等框架作为迁移前提。

页面组成：

1. `/`：报表工作台。保留报表位置、说明、条件摘要、修改条件、开始查询、筛选、复制、导出、分页；显示目录版本和最近成功同步时间。
2. 查询条件弹窗：复用 `QueryConditionsView`，沿用关闭/应用/应用并查询；数据源切换重建该页条件。
3. 选项弹窗：复用编码/名称、搜索、多选及全选行为；增加独立 lookupId 与版本。
4. 报表位置/说明弹窗：显示证据与适配状态；绝对路径、SQL、凭据只在维护站点可见。
5. 任务状态：排队、连接、执行、读取、后处理、导出、取消中、完成、失败、已中断、已过期。页面刷新不等于任务成功或重新执行。

关键代码改造：当前 `QueryState.create` 为判断 `.Text` 语义而读取整份 SQL。Web 改由服务端返回 `textParameterNames` 等必要元数据，普通页面不能为了保留 dirty 比较而下载全部 SQL。

工具栏桌面宽度 1040/1440/1920 为首批回归尺寸；普通浏览器缩放、窄窗口和键盘操作需新增检查。窄窗口允许局部滚动/合理折行，不挤压表格或让按钮不可达；不承诺旧 IE 兼容。具体院内浏览器版本在 P0 记录。

### 4.2 维护站点

独立受限网址，使用相同基础样式，页面为：

| 页面 | 功能与必要状态 |
| --- | --- |
| 维护首页 | 当前应用/目录版本、同步状态、失败项、冲突项、资源与健康状态 |
| 来源与目录 | 已登记 LIB 来源、扫描/只新增/重新加载、导入统计、不完整与关联文件诊断 |
| SQL 编辑 | 报表与数据源选择、复制、静态检查、重载、来源信息、保存、脏状态提示 |
| 报表列同步 | 识别字段、候选说明、表头/宽度/新增/隐藏状态、双文件确认、补齐模板 |
| 维护试查 | 对指定工作版本填条件、执行只读查询和导出；使用同一查询组件与执行限制 |
| 版本与同步 | 草稿/当前运行版本/上游发布版本、比较、显式启用、重试、冲突处理、回退 |
| 连接与 TNS | 设置、测试、保存、服务器来源选择、密码记忆选择、配置版本 |
| 日志与帮助 | 脱敏事件、关联号、维护审计、功能边界和操作说明 |

保存确认必须写清“保存到 ReportDesk 服务器维护副本”“是否已启用到网页查询”“未发布到 HIS”。原生文件选择改成已登记来源选择；原生保存对话框改为页面确认；原生资源管理器操作改为来源信息/受控下载。

网页维护是当前明确需求。账号系统、角色后台和按科室分权限不新增；维护身份利用现有 Windows 身份或现场已有认证设施，具体白名单由现场配置。

## 5. 架构、项目与复用策略

### 5.1 单服务器、两个 Web 进程

建议两个独立 IIS 网站和应用池，公开查询网址维持统一；新增维护网址只面向批准的维护人员。域名、证书、绑定待现场分配；不以“别人不知道维护端口”作为权限控制。

```text
普通浏览器 -> IIS 查询站点 -> ReportDesk.Web
                              | 只读读取当前报表版本、连接配置
                              | 独立查询/选项/导出任务 -> Oracle 只读账号
                              v
                         版本化报表存储
                              ^
维护浏览器 -> IIS 受限维护站点 -> ReportDesk.Web.Maintenance
                              | SQL/模板工作区、版本提交、连接配置
                              | 唯一同步工作者 <- HIS 发布表只读
                              | 初始导入 <- 服务器完整 LIB

现有 Electron/WinForms -> 原 Host / net48 Core（保留）
```

维护进程负责唯一的报表版本写入、来源导入和 HIS 发布同步；查询进程对报表/配置仅需读权限。二者通过完整版本清单和原子替换的指针协作，不共享内存中的 Service，不互相复用 result/lookup，不为每名用户启动 Host。

IIS 回收时可能短时存在旧新进程，因此“单工作进程数为 1”不够：同步和版本提交必须有跨进程排他锁、指针版本比较；进程内 Semaphore 不能充当全局锁。查询进程启动新任务前确认当前版本，旧版本引用释放前不得回收文件。

### 5.2 技术选型与兼容验证

| 层 | 建议 | 验证门槛 |
| --- | --- | --- |
| Web | ASP.NET Core 10，`net10.0-windows`，win-x64 | 两个最小站点在目标 Server 2016 补丁状态下启动；验证 Windows 认证与匿名查询隔离 |
| Core | 首选 `net48;net10.0-windows` 多目标，共用 XML/绑定/适配/导出规则 | `Directory.Build.props` 和 Framework 引用条件化；桌面仍用 net48 |
| Oracle | net48 保持 `Oracle.ManagedDataAccess 19.32.0`；Web 使用经验证的 `Oracle.ManagedDataAccess.Core` 固定版本 | 23.26.0 及以上有 .NET 10 官方支持；具体锁定版本由 P0 恢复与兼容试验确定 |
| 业务应用层 | 从 Host 提取无 UI、无全局会话状态的 Application 服务 | DTO、参数转换、报表选择、编辑状态明确归属；Host 通过适配继续工作 |
| 页面 | 现有 HTML/CSS/JS + transport 接口 + 查询/维护独立入口 | 不直接把 renderer 全局变量共享给多个任务；无 Electron/Node 运行依赖 |
| 存储 | 首版使用文件清单、原子版本切换、受限工作区和进程内结果 | 不新增业务数据库 schema；暂不要求 Redis/分布式部署；新生产依赖在实现前列清 |

.NET 10 的微软支持列表包含 Windows Server 2016 x64；Oracle 系统要求包含对应 Windows 平台、.NET 10 与 Oracle 19c。官方平台支持不代替本程序迁移或服务器补丁验收。[微软支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)、[Oracle 系统要求](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallSystemRequirements.html)。

若 Core 多目标出现阻塞，优先将 Windows 持久化/Oracle 实现拆到适配层，保留共享业务规则；不能仅为赶进度复制出另一套 SQL 编译器。net48 Web 仅作为重新评估的备选，不在本文同时承诺维护两套服务器实现。

### 5.3 文件级改造清单

下列新路径均为拟新增，本文不创建项目或修改代码。

| 文件/目录 | 拟改造及理由 |
| --- | --- |
| `Directory.Build.props`、Core csproj/lock | 目标框架和引用条件化；桌面与 Web 各自锁定驱动，避免升级破坏旧客户端 |
| `src/ReportDesk.Core/SqlTemplate.cs`、`ReportImporter.cs`、`ParameterOptions.cs` | 业务规则复用；仅必要兼容改造，不改变 SQL/字典/默认值语义 |
| `src/ReportDesk.Core/OracleQueryService.cs` | 提供实例化连接上下文、取消/阶段进度及 SchemaOnly；首版保留 Pooling=false 与 CommandTimeout=0 |
| `src/ReportDesk.Core/HisResultRules.cs`、`TabularReportAdapter.cs`、`OutpatientPrescriptionAdapter.cs` | 原适配能力保留；新增迁移回归，现有失败独立追踪 |
| `src/ReportDesk.Core/SqlXmlEditor.cs`、`ReportLayoutEditor.cs` | 继续做局部文件变换；跨人、跨版本和发布事务由外层服务负责 |
| `src/ReportDesk.Core/XlsxExporter.cs` | 抽取可写 Stream 的导出核心，保留桌面 path 包装、格式及容量检查；Web 不接收客户端目标路径 |
| `src/ReportDesk.Host/Program.cs` 与两个 Service.*Editing.cs | 抽取可复用用例；原 IPC 白名单和窗口语义保留，禁止改成全局 Web Service |
| 拟增 `src/ReportDesk.Application/` | Catalog、Parameters、Query、Lookup、ResultView、Export、SqlEditing、LayoutEditing 服务与契约 |
| 拟增 `src/ReportDesk.Web/` | 查询 API、匿名会话、任务队列、查询页面、关联号与错误转换 |
| 拟增 `src/ReportDesk.Web.Maintenance/` | Windows 身份授权、维护 API/UI、工作区、配置、HIS 同步、版本提交 |
| 拟增 `src/ReportDesk.Server/` | 共享版本读取/写入、路径约束、同步清单、凭据读写封装；只在维护程序注册写服务 |
| `src/ReportDesk.Desktop/ui/` 及拟增 Web UI | 提取可共享纯组件，拆开 ElectronTransport/WebTransport；不复制整份编辑器长期分别修复 |
| `src/ReportDesk.Desktop/main.cjs`、`preload.cjs`、`sql-editor-main.cjs` | 原生桌面能力继续工作；共享 UI 改造须回归现有 IPC 和打包资源清单 |
| 拟增 `tests/ReportDesk.Web.Checks/`、`tests/web/` | API 契约、会话、授权、冲突、浏览器、同步和重启专项 |
| 拟增 `scripts/build-web.ps1`、`deploy-web.ps1`、`verify-web.ps1` | 构建/离线包/显式参数部署/只读验收；不沿用桌面三文件更新 |

## 6. LIB、HIS 同步与网页编辑如何共存

### 6.1 完整 LIB 作为初始来源

按用户提出的方式，服务器保留一份完整 LIB，保持原目录和相对引用，不手工重新整理每张报表。从已更新且没有未发布编辑的客户端取得基线；ReportDesk 扫描 XML，不加载或执行其中的 HIS DLL/EXE，不向匿名浏览器暴露配置文件。

建议目录：

```text
C:\Apps\ReportDesk\releases\<应用版本>\query\
C:\Apps\ReportDesk\releases\<应用版本>\maintenance\
C:\ReportDeskData\HIS\LIB\                  初始来源，应用只读
C:\ReportDeskData\Reports\working\          维护工作副本
C:\ReportDeskData\Reports\versions\         校验完成的不可变运行版本
C:\ReportDeskData\Reports\staging\          导入/发布同步暂存
C:\ReportDeskData\State\                    来源、游标、配置版本、任务最小状态
C:\ReportDeskData\Secrets\                  受保护凭据
C:\ReportDeskData\Logs\                     脱敏日志/审计
```

并非每个运行版本复制整个 LIB：仅保留可识别的报表 XML、必要配套文件及清单；原相对结构和字节需保持。整份来源 LIB 的配置不公开，也不由 Web 自动读取其中密码。是否另设数据盘以现场为准。

### 6.2 稳定 ID 与版本

当前 `ReportImporter.ImportFile` 使用绝对路径大写后的哈希作为 report.Id。搬到另一盘或 `versions/<version>` 会改变 ID，所以 Web 不能直接沿用该算法作为对外稳定标识。

拟采用独立 Web 注册表：`sourceId + 标准化来源相对路径` 映射固定 `reportId`；原路径、Core legacyId、文件哈希保留在内部。路径比较遵循 Windows 大小写规则，拒绝链接/越界，同名不同路径保持不同报表；重命名不自动猜为同一报表。

`sourceId` 专指登记的 LIB/导入来源；`dataSourceId` 专指报表内的主表、明细或条件数据源，由服务端随指定 `definitionVersion` 返回。二者不能混用；数据源重排或定义更新后须重新取得标识，不凭显示名称或旧索引猜测映射。

`catalogVersion` 表示目录清单版本；`definitionVersion` 表示查询 XML、关联规则/必要模板及解析器规则版本的组合版本；`connectionVersion` 表示连接配置；`viewId` 表示一次结果筛选排序快照。不能只用查询 SQL 字符串判断报表版本。

位置/显示配置若使用旧路径 ID，需要维护端导入映射预览，不能按同名自动匹配。源 SQL、编码、文件内容不为生成新 ID 而重写；这只是新 Web 存储契约，桌面 ID 和文件格式不变。

### 6.3 同步策略

默认设计保留交接中的“初始完整 LIB + 后续只读 HIS 发布表同步”，约 30 秒为可配置建议间隔。仅复制 LIB 不会自动更新。如果现场另有可靠维护该目录的机制，可切换“外部维护目录模式”，但同一来源只允许一个更新方式负责，不能两个写者同时覆盖。

HIS 来源为 `SYS_UPDATE_RELEASE` / `SYS_UPDATE_RELEASE_FILE`；压缩 BLOB 不是明文 XML。按既有证据处理空 ZIP 条目名、可信化文件名/相对目录、目录包标志、缺失文件和异常压缩大小。只接受识别的报表及配套内容，绝不执行更新包。

同步要求：初始导入期间补取并发发布；重叠回查与周期性元数据复核，防较小 ID 晚提交；文件 ID/发布 ID/哈希幂等；失败单独记账和重试；版本完整才切换；没有确认删除语义时不因一次扫描/发布缺文件而删除。HIS 回收历史或发布内容变化需记诊断，不偷偷推进游标忽略。

目录扫描遇外部复制只能接受稳定完整批次；仅文件时间不变不能证明跨文件已复制完成。外部维护目录模式须约定“复制到暂存后切换”或明确完成标记；不以 FileSystemWatcher 单次事件作为生效依据。

### 6.4 网页保存、网页启用、HIS 发布是三件事

拟定流程：打开指定版本 → 创建维护工作副本 → SQL/列编辑并保存工作副本 → 校验/试查 → 明确“启用到网页查询” → 完整新版本切换。

- SQL 普通保存仍可存下静态检查不通过的工作稿，保留现有编辑能力；有阻塞的稿件不得作为可执行定义启用。诊断状态必须显示。
- 保存工作副本不自动改变普通网页正在使用的版本。相比桌面“保存即重载”，这是为多人运行增加的明确启用步骤，需在交付说明突出。
- “启用到网页查询”不写 HIS 发布表、不调用 SaveRelease，也不代表全院 HIS 客户端已更新。若要回 HIS，受限维护端导出准确的 XML/差异清单，由原 HIS 既有流程核对并发布。
- 未确认“任意网页修改是否可以长期覆盖 HIS”前，不自动采用本地优先或 HIS 优先。开启本地 Web 版本启用前需确认下面的冲突流程。

冲突规则建议：

| 情形 | 拟处理 |
| --- | --- |
| 没有本地修改，收到完整 HIS 发布 | 校验通过后自动启用 |
| 只有未启用草稿，收到新 HIS 版本 | 上游正常启用，草稿保留但标为基线过期；保存/启用须重新比较 |
| 有已启用本地版本，HIS 更新不同文件 | 更新不冲突文件，生成完整新版本；整个发布有关联依赖时整批处理 |
| 同一查询或配套模板两方都改 | 隔离整份相关报表更新，保留当前可用版，提示待维护处理；不擅自合并 SQL |
| 两方内容最终一致 | 标为已与 HIS 对齐，清除对应本地差异状态 |
| 外部覆盖工作区 / 预览后文件变更 | 哈希冲突拒绝保存；草稿仍可复制，重新打开比较 |

维护人员显式选择接受上游或保留本地并重新验证；记录版本、操作身份、理由和哈希，不记录患者参数。冲突不可隐藏在“同步成功”中。

## 7. HTTP 接口与桌面方法映射

全部路径为拟定 v1 契约，需随实现生成 OpenAPI。Q 路由只在查询站点注册；M 路由只在维护站点注册。不能直接提供 `/api/call?method=...` 转发 Host 的全方法能力。

### 7.1 查询站点接口

| 路由 | 用途及原方法 | 核心约束 |
| --- | --- | --- |
| `GET /api/v1/bootstrap` | `bootstrap`、目录/能力/CSRF 初始化 | 不包含 settings、原 SQL、绝对路径或密码；不触发全库扫描 |
| `GET /api/v1/reports` | `list`、搜索/分页/目录版本 | 仅独立报表，待适配保留 |
| `GET /api/v1/reports/{id}` | `select`、数据源/条件/位置/说明 | 返回 textParameterNames；无共享“当前所选报表”状态 |
| `POST /api/v1/query-jobs` | `query` | reportId/dataSourceId/definitionVersion/参数；返回 202、jobId；不接收 SQL 或路径 |
| `GET /api/v1/jobs/{jobId}` | `onProgress` / 查询状态 | 只返回本会话任务；阶段文本，不伪造进度百分比 |
| `POST /api/v1/jobs/{jobId}/cancel` | `cancel` | 幂等、校验归属；Cancelled 仅在执行端结束后确认 |
| `POST /api/v1/jobs/{jobId}/views` | `view` | 固定筛选与排序生成 viewId，不原地改变别人已持有视图 |
| `GET /api/v1/views/{viewId}/rows` | `page` | offset/take，take 最大 200；完整总数与类型列 |
| `POST /api/v1/lookup-jobs` | `lookup` | 参数/依赖版本绑定；任务机制、同等只读规则 |
| `GET /api/v1/lookups/{lookupId}/rows` | `lookupPage` | 独立选项视图，搜索不改变原始选项集合 |
| `POST /api/v1/lookups/{lookupId}/selection` | `lookupAll` | 对应原全部已加载编码；大的选择可返回 selectionToken 并由服务器按原集合绑定 |
| `POST /api/v1/export-jobs` | `export` | jobId/viewId，固定结果版本，不重新执行 SQL |
| `GET /api/v1/exports/{exportId}/download` | 浏览器下载 | 所属会话、有效期、完整导出状态；随机 ID 不代替授权 |
| `DELETE /api/v1/jobs/{jobId}` | `clear` | 只释放本任务及引用；Running 时先取消，不删除他人任务 |
| `POST /api/v1/client-events` | `logClient` | 仅固定事件码/关联号，限流，不接收 DOM、SQL、输入值或堆栈全文 |

### 7.2 受限维护接口

| 路由 | 对应能力 | 核心约束 |
| --- | --- | --- |
| `GET/POST /api/v1/maintenance/sources` | `import` 来源登记 | 仅批准根下的相对路径；POST 不接受任意 UNC/外部 URL/绝对路径 |
| `POST /api/v1/maintenance/sources/{id}/scan-jobs` | `checkNewReports`、`recheck`、重新导入 | 明确 mode：addOnly / refresh / validate；取消不提交半成品 |
| `GET /api/v1/maintenance/reports/{id}/files` | `relatedFiles`、`definition`、`sqlEditorReveal` | 受限完整诊断，输出服务器决定的文件 ID，不信客户端路径 |
| `GET /api/v1/maintenance/files/{fileId}/download` | 来源 XML 下载/回 HIS 工作流 | 只允许报表白名单文件，不下载 LIB 配置、DLL、EXE、Secrets |
| `POST /api/v1/maintenance/reports/{id}/reload` | `reloadReport` | 版本冲突校验；单报表操作，保留别人运行快照 |
| `POST /api/v1/maintenance/reports/{id}/edit-sessions` | `sqlEditorOpen` | 返回 editSessionId、来源版本、哈希、可编辑数据源 |
| `POST /api/v1/maintenance/edit-sessions/{id}/check` | `sqlEditorCheck` | 静态检查，不访问数据库，不意味着可执行 |
| `PUT /api/v1/maintenance/edit-sessions/{id}/sql` | `sqlEditorSave` | If-Match、sourceIndex、SQL、Idempotency-Key；只写工作稿 |
| `POST /api/v1/maintenance/edit-sessions/{id}/layout-jobs` | `layoutPreview` | 原/新 SQL 字段描述、参数快照、候选模板校验，返回预览任务 |
| `POST /api/v1/maintenance/edit-sessions/{id}/layout-commits` | `layoutSave` | 一次性 previewToken、完整列设置、两个文件哈希；保存后不自动查询 |
| `DELETE /api/v1/maintenance/edit-sessions/{id}` | `sqlEditorDiscard` | 放弃服务器编辑会话；是否丢弃浏览器草稿由明确确认决定 |
| `POST /api/v1/maintenance/query-jobs` | 指定工作版本试查 | 与 Q 相同执行规则；仅接受 editSession/version 标识，不提供任意 SQL 执行接口 |
| `GET/PUT /api/v1/maintenance/connection` | `settings`、`saveSettings` | 密码只写不回显，If-Match；批准端点；连接配置有版本 |
| `POST /api/v1/maintenance/connection/test-jobs` | `testConnection` | 成功且保存成功才切换配置；失败/取消保留原配置 |
| `GET /api/v1/maintenance/tns-sources` | `discoverTns`、`pickTns` | 返回登记的文件 ID/标签，不扫描任意磁盘 |
| `GET /api/v1/maintenance/tns-sources/{id}/aliases` | `tnsAliases`、刷新 | 服务器解析；保留当前 TNS 支持边界，不自动展开 IFILE |
| `GET /api/v1/maintenance/versions` | 新增版本状态 | 上游/本地/运行版本区分，显示冲突与最后成功时间 |
| `POST /api/v1/maintenance/versions/{id}/activate` | 工作稿启用 | 校验通过、基线匹配、显式确认；不回写 HIS |
| `POST /api/v1/maintenance/sync-jobs` | 手动补查/失败重试 | 单写者、幂等，不等于重新发布 HIS |
| `POST /api/v1/maintenance/versions/{id}/rollback` | 运行版本回退 | 新的有审计指针切换，协调同步暂停/隔离，防立刻被问题发布覆盖 |
| `GET /api/v1/maintenance/logs` | `openLogs` 的 Web 替代 | 按事件/日期限量读取，路径固定、脱敏，不返回凭据或患者数据 |

维护端另提供本身份任务/选项/视图/导出的同构路由，前缀均为 `/api/v1/maintenance/`，与 Q 的任务存储和 Cookie 隔离。同步冲突处理采用受限版本操作，不能靠通用文件写接口实现。

本地交互无需 HTTP：`copy` 用剪贴板；`sqlEditorDirty` 留在页面状态；`sqlEditorDiscard` 的确认在页面完成后才删除会话；`onFatal` 转为服务不可用/任务中断提示；`demo` 仅测试模式，不注册生产入口。

### 7.3 请求、响应与错误约定

- 查询请求：`reportId`、`dataSourceId`、`definitionVersion`、`clientTabId`、`values`，必要的 `.Text` 按原语义传递；服务器以报表元数据验证，不把“客户端来自下拉框”当可信证明。
- 响应示例：`{ jobId, state, stage, definitionVersion, connectionVersion, resultId, error: { code, message, correlationId } }`。字段按状态出现，结果数据单独分页。
- Q/M 中的任务标识都由服务器随机生成；clientTabId 只隔离界面，不是权限凭证。
- `400` 无效格式，`401/403` 维护身份问题，`404` 不存在或不属于当前主体的任务，`409` 版本/文件/配置冲突，`410` 已释放的本方结果，`422` 业务参数/不支持定义，`429/503` 排队容量或服务不可用。错误不包含原始连接串、SQL 或参数值。
- 创建查询、导出和保存均使用 `Idempotency-Key`。同一主体/键/请求内容返回同一操作；相同键不同内容拒绝。保存操作持久化最小完成记录与哈希，网络丢回包后可查结果，不重复覆盖。
- 未知字段拒绝或白名单忽略须写进 DTO；不接收任意 DataView 表达式/SQL 排序片段，排序只按已知列索引与方向。
- 匿名查询数据、维护 SQL/配置、任务结果响应使用 `Cache-Control: no-store`。日志层关闭请求体记录；URL 不放条件值或 SQL。无跨域开放策略。

## 8. 多会话、版本一致性与资源

### 8.1 会话与任务归属

查询站点分配不可预测的匿名 Cookie，会话状态在服务端；Cookie 使用 Secure、HttpOnly、SameSite 及明确 Path/Domain，和维护站点凭据分离。匿名会话不是医院员工身份，不能把匿名审计写成实名。

每个任务绑定会话、tabId、reportId、dataSourceId、definitionVersion、connectionVersion，并由服务器解析所属 sourceId。查询结果、选项结果、视图、导出、取消均校验归属。同一浏览器多标签共享会话时仍有独立任务/视图，互不覆盖；两台浏览器相互访问任务被拒绝。

状态机：`Queued -> Running -> Succeeded | Failed | Cancelled | Interrupted`；`CancelRequested` 为中间状态；成功结果释放后为 `Expired`。排队任务可以直接取消，执行任务等待 Oracle/后处理实际停止。断网和刷新只重新读取状态，不自动重发大查询。

### 8.2 版本变更的行为

| 事件 | 预期行为 |
| --- | --- |
| 打开条件时版本变化 | 提交返回 409，提示重新加载；只有名字/类型/语义兼容的输入可保留 |
| 查询排队期间版本变化 | 执行前重验版本；拒绝过期任务并提示，不悄悄执行新版 SQL |
| 查询已进入 Running 后新版本启用 | 固定原定义/连接/参数执行完，不更换中途口径 |
| 旧结果已完成 | 可按旧视图查看/导出，醒目标记版本；不能把新 SQL 标题/条件套在旧结果上 |
| 当前维护页保存并重载 | 当前页旧结果清除，保留“保存成功/重载失败”区别；别人的已开始任务不被清空 |
| 修改连接 | 配置版本切换只影响后续任务；已排队重验，已运行持有自己的连接，不共享可变全局 settings |
| IIS 回收/崩溃 | Running/Queued 标为 Interrupted，结果不可恢复则提示重查，不自动重跑 |

### 8.3 初始容量策略（建议值，不是正式承诺）

原客户端把全量结果存在 DataTable，筛选/导出会增加内存开销。50 会话不能无限持有 50 份大结果。首版继续保留当前无固定 SQL 超时和无行数截断，采用任务准入、排队、完整失败及显式释放。

首轮测试可从查询站点最多 2 个数据库工作任务、维护站点最多 1 个数据库工作任务、导出最多 1 个开始；lookup、SchemaOnly、连接测试、同步也应纳入相应站点预算，不无限创建额外 Oracle 会话。跨进程回收需用持有到连接释放的跨进程许可或不重叠执行安排，不能新旧进程各自再放行一套额度。

这些是待压测建议，不是已批准的生产并发值。队列容量、会话/结果/编辑会话有效期、总内存预算、磁盘低水位与清理周期在 P0/P4 确定，写入部署参数。内存压力超过准入条件时拒绝新任务；运行中无法完整承载则明确失败，不能返回截断的“成功”结果。

首版不新增患者结果或参数的服务器持久化：结果、筛选视图及导出 ZIP 均为受预算管理的内存对象，下载完成/释放按约定清理。`XlsxExporter` Stream 适配仍需完整性检查和取消测试；导出成功前完成受控生成，避免业务异常后还报告完整下载。大结果所需的落盘、断点下载或长期缓存属于单独的数据保留设计，未批准前不开启。

最小任务状态可持久化 jobId、报表/版本、所属主体的不透明标记、时间、终态和错误码，**不含参数、SQL 或结果**。浏览器不使用 localStorage/IndexedDB/Service Worker 保存业务数据；页面刷新是否恢复任务仅依服务器会话，草稿恢复不作保证。

## 9. 维护授权、凭据与文件保存

### 9.1 查询免登录，维护明确身份

建议维护站点启用 IIS Windows Authentication，关闭该站点匿名身份；应用再次校验明确允许的 Windows 用户/组。查询站点保持匿名，不承担维护 API。维护网段限制作为附加控制，不能替代身份校验。[微软 Windows 认证说明](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0)。

域环境、可用 Windows 账号/组、浏览器协商能力尚未确定，P0 先做最小验证；不假定已加域，不硬编码维护组或给所有 Windows 用户维护权限。若现场不具备条件，需另选现有认证方式，期间维护站点拒绝访问，不能降级为匿名。

所有更改操作做 CSRF/Origin 校验，维护界面防 iframe 嵌入；查询任务/取消/导出创建也防跨站请求滥用。SQL、报表名称、列名及异常文字都以文本输出，不插入可执行 HTML。功能按钮隐藏只是展示控制，直接 HTTP 调用仍须拒绝越权。

### 9.2 凭据保存与跨进程使用

桌面继续使用 CurrentUser DPAPI。Web 两个不同应用池不能简单共用开发者或另一应用池的 CurrentUser 密文。

建议服务器凭据单独放到 Secrets，采用服务器证书保护的共享加密存储：查询身份仅读取、维护身份可写；证书私钥仅授权这两个服务身份及批准的恢复管理员。证书、密钥、密码均不进入 Git/文档/发布包；P0 验证跨身份读写、轮换和离线恢复。也可使用现场现成秘密管理服务，不新建业务表。

“记住密码”默认与现桌面一致，但必须由维护人员确认后保存；关闭记忆时只能在受控运行会话中使用。两个站点的临时秘密传递需本机受 ACL 限制的 IPC，不能写明文临时文件，也不能在查询初始化接口返回密码。若尚未实现安全临时传递，明确将该配置标为不可供查询服务使用，不能把一次维护测试成功宣称为全站已连接。

配置提交过程：验证格式/批准端点 → 若选择“连接并保存”则测试 → 准备加密新配置 → 原子替换与回读 → 发布 connectionVersion。失败或取消保持旧配置。单独“保存设置”不连接，标注未验证。旧设置/备份不含明文。

### 9.3 XML 编辑与提交安全

- editSession 绑定维护身份、固定报表版本、数据源索引和两个文件哈希；token 不可跨身份使用。短时文件锁与乐观并发校验同时存在，不能只靠前端忙碌按钮。
- layoutPreview 绑定原/新 SQL、当前参数的非日志化校验摘要、连接版本、模板哈希、字段列表；任何一项变化必须重新预览。预览 token 单次提交，重试凭 Idempotency-Key 查询同一结果。
- 当前 Core 双 XML 保存不是崩溃安全事务。Web 先在独立工作目录准备完整修改，回读后写清单，最后在跨进程锁下比较旧指针并切换整个运行版本。只读查询进程永不读取半成品。
- 不生成源 XML 旁的 `.bak`，延续当前行为。运行版本历史是 Web 启用/回退所需的新机制，与每次编辑生成备份不同；保存数量、期限、空间和清理策略仍须确认，未确认不自动删除旧版本。
- 不允许通过客户端 path、文件 ID 猜测、链接、`..`、绝对路径或 ZIP 条目绕过根目录限制；下载只允许已登记报表文件。工作区不直接写原只读知识库或生产 HIS LIB。
- 没有持久化事务证据时不能宣称强制断电可恢复编辑草稿；只承诺正在使用的完整运行版本不因未完成稿件而被覆盖，并通过故障注入验证。

## 10. 部署、升级与回滚

### 10.1 安装前

确认 Server 2016 最新适用补丁、实际空闲资源、批准网段、查询/维护域名、证书、Oracle 端点和只读权限。先备份 IIS 配置并记录当前 80/8080/8081 绑定。报告只有 C 盘，不照抄旧 D 盘路径。

安装匹配的 .NET Hosting Bundle；已有 IIS 不等于已装 ASP.NET Core Module。安装可能需要 IIS 重启，须与既有网站一起安排维护窗口。ASP.NET Core 应用池设置 No Managed Code、x64、独立低权限身份；查询/维护各单工作进程。常驻同步使用 AlwaysRunning、预加载、合适空闲设置，并在启动补查；这不保证 IIS 停止时同步仍运行。[IIS 托管说明](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0)、[Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0)。

若必须在维护站点关闭/IIS 停止期间继续同步，再提取独立 Windows 服务；首版不隐含安装额外常驻服务。

### 10.2 发布包与现场操作

开发电脑交付：query/maintenance 两份完整 publish 产物、对应 web.config、本地前端资源、配置模板、固定依赖清单、SHA256 清单、部署/验证/回滚脚本及版本说明。生产服务器不安装 SDK、Node/npm 或全 HIS 开发工具链作为运行前提。

操作顺序：校验包 → 建两个独立站点/应用池与 ACL → 安全配置身份和凭据 → 初始 LIB 扫描 → 维护端核对目录/选项 → 代表报表查询导出 → 真实发布样本同步 → 并发/回收/回退测试 → 正式开放院内地址。

应用目录只读，公开查询站点对原 LIB/working/Secrets 写入均拒绝；仅开放必要读取。维护站点有批准数据目录的写权限，不能写应用发布目录。所有数据目录均不映射到静态网站。

部署脚本默认检查计划/路径/端口，不默认重启所有 IIS；实际部署步骤须带明确目标站点和发布版本。应用升级应停接新任务、等待或告知正在执行任务的中断，再切版本；不能承诺任意回收零影响。

### 10.3 回滚

应用：切回上一套 query/maintenance 兼容发布目录，核对配置/状态 schema 版本。报表：在受限入口切到上个完整可用版本，并暂停或隔离引起故障的同步发布。配置：恢复同一保护机制下的上一版本配置及必要密钥。

不回写 HIS 发布表、不回滚 Oracle 业务数据。升级应用不能自动撤销已经启用的报表编辑；应用、报表、配置三类版本应在交付记录分别列出。破坏性状态迁移未设计且未授权，不作为第一版部署步骤。

Server 2016 常规扩展支持将在 2027 年 1 月结束，长期运行需安排操作系统迁移；这不等于当前机器已被证明无法运行。[微软生命周期](https://learn.microsoft.com/en-us/lifecycle/products/windows-server-2016)。

## 11. 开发阶段与验收

所有阶段在实施前建立功能 ID 对应测试；模拟测试只验证状态和错误机制，真实报表兼容性使用只读真实 LIB/隔离副本。未经授权不连接生产 Oracle，不让旧 build.ps1 的演示数据替代真实报表验收。

| 阶段 | 交付 | 通过标准 |
| --- | --- | --- |
| P0 兼容与契约 | 当前清单、Core 多目标试编译、驱动固定版本、维护 Windows 认证验证、配置与标识契约 | 明确依赖阻塞、批准根/身份策略、数据生命周期；保留 net48 构建路径 |
| P1 查询 Web | 工作台、条件、字典、任务、结果视图、复制、Excel、会话 | F05–F28/F46–F47；两独立会话、多标签、错误/取消/断网验收 |
| P2 网页维护 | 来源导入、连接/TNS、SQL 编辑、列同步、维护试查 | F01–F04/F08–F10/F29–F45；最新编辑功能一项不漏；越权接口拒绝 |
| P3 版本与同步 | 初始完整 LIB、稳定 ID、只读发布同步、编辑冲突、版本启用/回退 | 晚提交/重复/损坏/缺配套/中断/外部写入测试；真实 BLOB 样本字节核对 |
| P4 IIS 联调 | 离线发布包、服务器安装、既有网站检查、资源压测、回收与恢复 | 目标服务器记录；Oracle 授权现场验证；50 会话混合场景，不仅是静态页面访问 |
| P5 交付 | 功能矩阵逐项结论、SOP、已知问题、升级/回滚记录、桌面回归 | Q/M 两端全范围验收，未运行明确 NOT RUN；不能用 P1 查询可用代替全功能完成 |

### 11.1 必测场景

| 编号 | 验收内容 |
| --- | --- |
| A01 | 真实 LIB 初始扫描/单 XML/多来源/路径失效/新增扫描/取消；报表、版式、不完整数量可追溯 |
| A02 | 搜索、菜单证据与未知状态；部分可查数据源不掩盖其他阻塞 |
| A03 | 零条件、多条件、多数据源、日期格式、默认偏移、静态/动态字典、`.Text`、TreeSelect、多选、复选框、RegisterID、隐含参数 |
| A04 | 弹窗关闭/应用/Esc 不丢输入、不自动查询；dirty 比较、参数错误、切数据源清空仅本页结果 |
| A05 | 参数绑定/中文绑定/多选/非法模板/科室语义；不拼接用户 SQL 值，不移除限制 |
| A06 | 完整结果分页、原类型排序、全量筛选、NULL、前导零、高精度数字、日期，旧 viewId 导出保持原快照 |
| A07 | Excel 双工作表、当前完整视图、容量/字符失败、公式样式字符串作为文本、下载取消、生成失败不留完整成功标记 |
| A08 | 会话 A/B 与同会话两个标签的查询/选项/取消/复制/导出互不覆盖；枚举/猜测任务标识拒绝 |
| A09 | 排队取消、运行取消、Oracle 取消实际效果、握手等待、断网/刷新/重复提交、资源释放；仅授权环境测试数据库 |
| A10 | SQL 主/明细编辑与条件只读、复制、静态检查失败仍可存稿、Ctrl+S、关闭确认、1 Mi 字符上限 |
| A11 | 原查询编码/BOM/CDATA/转义、其他数据源字节未改、无备份、无变化不写、哈希冲突、回读失败、保存成功但重载失败 |
| A12 | 主/明细唯一模板、SchemaOnly、新列前中后插入、表头/列宽/隐藏/样式保持，候选模板仍需确认 |
| A13 | SQL 先存后补齐模板、仅表头/列宽修改、无 SQL 变化仍可预览、查询文件字节/时间不变、重复同步无重复列 |
| A14 | 拒绝重复字段、删改重排旧字段、复杂合并/公式/交叉/冻结/打印设置/模糊模板；拒绝后原文件不变 |
| A15 | 双文件中途失败与撤回、进程终止、幂等重试、两位维护人员同时保存；运行指针不指向半成品 |
| A16 | 直连/TNS、路径失效/多个来源/别名变更/不支持 IFILE、成功保存/失败取消不覆盖、单独保存未测试、密码记忆开关/不可解密 |
| A17 | 维护站点匿名访问与非白名单身份拒绝；Q 无维护路由；CSRF、路径穿越、ZIP 越界、XML 外部实体、XSS、日志敏感内容检查 |
| A18 | 稳定 reportId 跨部署目录/版本不变；同名不同路径区分；旧位置/显示标识转换逐项核对 |
| A19 | 发布基线补查、晚提交、重复、空条目名、损坏 BLOB、缺文件、外部复制未完、错误退避、失败不丢游标 |
| A20 | 本地草稿/HIS 更新/本地已启用版本冲突；无自动覆盖，任务固定版本；启用/回退不冒充 HIS 发布 |
| A21 | IIS 旧新进程重叠单写者、启动补查、任务 Interrupted、结果丢失提示、不自动重跑、临时文件/任务清理 |
| A22 | 50 会话，短/中/大真实报表混合、字典/维护/同步竞争，测 CPU/内存/Oracle 会话/队列/导出/既有网站影响 |
| A23 | 桌面 Host/共享 Core/UI/ASAR 打包回归；旧 WinForms 入口与配置兼容；不把新 Web 更新包装作桌面更新 |
| A24 | Oracle 与 HIS 同条件核对行数/关键金额/日期、特定版本适配、打印/明细跳转单列现场验证；每项标明实际运行状态 |

现有可复用测试包括 `tests/ReportDesk.SqlEditing.Checks`、`tests/ReportDesk.Host.Checks`、`tests/desktop/sql-editor-checks.cjs`、`sql-editor-e2e-checks.cjs`、`layout-editor-e2e-checks.cjs`、`lib-query-conditions-checks.cjs`、`lib-new-reports-checks.cjs`、`report-persistence-checks.cjs`、`host-checks.cjs`、`package-checks.cjs` 等。浏览器契约新增 Web 测试，不以 Electron UI 通过代替 Web 验收。

已有文档记录综合 `tests/ReportDesk.Checks` 的 plain AddMap 断言失败，并记录在旧提交复现；这是历史已知问题，本轮未重跑。必须在 P0 建立问题与复核记录，不能宣称现有全量测试通过，也不能悄悄删除断言来获得绿色结果。

## 12. 仍需确定的最小事项

以下不阻止方案评审或独立开发，但在对应功能启用前必须定案：

1. 维护身份：是否有域/可用 Windows 账号或组、批准维护人员/终端；不重复询问普通查询是否登录。
2. 网页修改的生效规则：采用本文“存工作稿、显式启用到网页、HIS 冲突人工处理”的建议，还是仅维护草稿后回原 HIS 发布；不能默许长期本地覆盖。
3. LIB 最终来源路径、是否由现场更新程序维护；每个来源的唯一同步方式。
4. 正式查询/维护地址、端口、HTTPS 证书、允许网段，以及运行维护窗口。
5. Oracle 批准端点、只读账号权限、样本验证方式；凭据在现场安全配置，不发送到文档或仓库。
6. 会话/结果/草稿/版本有效期、持久化最小元数据范围、备份保留与清理规则；新结果落盘默认关闭。
7. 实测后的并发/队列/内存预算与目标时延；确认需要覆盖的院内浏览器版本。

## 13. 证据索引与本轮验证边界

源码/文档入口（相对仓库路径，链接可随项目迁移）：

- [桌面页面](../src/ReportDesk.Desktop/ui/index.html)、[交互及查询结果](../src/ReportDesk.Desktop/ui/renderer.js)、[条件状态](../src/ReportDesk.Desktop/ui/query-form.js)、[SQL 与列同步 UI](../src/ReportDesk.Desktop/ui/sql-editor.js)。
- [桌面 IPC/原生功能](../src/ReportDesk.Desktop/main.cjs)、[编辑原生确认](../src/ReportDesk.Desktop/sql-editor-main.cjs)、[Host 方法与参数/结果语义](../src/ReportDesk.Host/Program.cs)。
- [SQL 编辑服务](../src/ReportDesk.Host/Service.SqlEditing.cs)、[列同步服务](../src/ReportDesk.Host/Service.LayoutEditing.cs)、[SQL 文件保存](../src/ReportDesk.Core/SqlXmlEditor.cs)、[模板变换与保存](../src/ReportDesk.Core/ReportLayoutEditor.cs)。
- [导入器与路径 ID](../src/ReportDesk.Core/ReportImporter.cs)、[扫描与关联](../src/ReportDesk.Core/ReportFileDiscovery.cs)、[Oracle 执行/描述](../src/ReportDesk.Core/OracleQueryService.cs)、[Excel](../src/ReportDesk.Core/XlsxExporter.cs)、[结果规则](../src/ReportDesk.Core/HisResultRules.cs)。
- [当前列同步说明](ReportLayoutEditing.md)、[条件编辑实际边界](QueryConditionEditing.md)、[SQL 保存历史变更](Verification-SqlSave-20260917.md)、[查询条件弹窗](Verification-QueryConditionsModal-20260914.md)、[历史测试边界](LocalTesting.md)、[位置元数据](ReportLocations.md)。
- [旧交接方案](../ReportDesk-Web-IIS-Handoff-20260918/01-实施方案.md)、[HIS 发布证据](../ReportDesk-Web-IIS-Handoff-20260918/03-HIS发布机制证据.md)。

本轮已做：工作区与代码版本检查；GitHub main 引用核对；前端、IPC/Host 分派、Core 关键规则和近期提交静态核对；服务器报告 JSON 解析；官方运行时/驱动/IIS 资料核对；技术方案文档生成与覆盖检查。

本轮未做：Web 实现、构建或运行测试、全 LIB 重扫、Oracle 连接、HIS 样本解压、服务器安装/配置/重启、生产部署、容量或业务结果验收。引用旧验证记录仅表明已有历史证据，不代表本轮重跑或 Web 通过。
