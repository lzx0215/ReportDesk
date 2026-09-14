# ReportDesk

- 本地 HIS 资料现从 `D:\系统知识库\00_Inbox\yljhis\LIB` 读取（内部还有一层 `LIB`）；保持只读。2026-09-11 用户复制后指定此新路径，旧 E 盘路径仅保留在历史验证记录中。

- 导入以内容为准：ReportQueryInfo 且含报表 SQL 才作为独立查询显示；无 SQL 的定义单列为不完整，保留原文件和旧库条目，不能断言不是 HIS 业务报表。所有目录入口及按 ID 获取遵守同一过滤规则。
- HIS 菜单位置须有明确来源，允许多路径；不得从文件名、分类或 DDL 猜菜单。report-locations.xml 是独立位置元数据，不改变 catalog 或岗位显示权限；文件名关联必须注明版本未核对。菜单记录未提供时保持未知。

- 用户范围：Windows 10 x86/x64 个人桌面程序，Oracle 19c，导入现有 HIS 报表 XML，统一表格查询与 Excel 导出，不复刻原报表版式。
- WinForms / .NET Framework 4.8，Oracle.ManagedDataAccess 19 系列；升级驱动必须重新核对 x86 支持。
- 原知识库只读；不连接真实数据库，不自动修改 SQL 业务口径或删除科室限制。
- 所有 SQL 使用绑定参数，拒绝不支持的模板语法，不以字符串替换拼接用户值。只读事务是额外防护，不能替代现场最小权限账号（尤其函数执行权限）。
- 不保存查询结果和参数值；密码仅可由用户勾选后以 CurrentUser DPAPI 保存。
- Electron 首次成功导入后在 import-sources.json 仅保存来源路径/类型，启动自动读取原 XML，路径失效须提示并保留。连接测试成功后自动保存 connection.json；保存密码默认勾选并使用 CurrentUser DPAPI，取消选择须持久保留。测试失败/取消或保存失败不能覆盖原连接配置，不回传密码到渲染器。
- 用户要求 0.1.3 起不设置连接/查询计时器、结果行数或内存截断；不改 TNS/数据库的外部限制。错误日志仅本地按日追加，过滤凭据、SQL、参数与结果；无上下文的异常不写原始消息。
- 不支持的跨表、交叉、映射、控件语义明确标记待适配，不能静默简化后称为 HIS 等价结果。
- HIS 根目录导入按 XML 根节点识别文件；显式明细引用与主表命名候选分别呈现，不因配套文件存在解除待适配。关联信息不改变 catalog 格式，不改原 XML，不跟随扫描中的链接或外部引用。
- 表格适配须有原引擎/查询证据和回归检查。纯 AddMap 仅输出结果映射；交叉、合计、行分组按 HisResultRules 的支持范围执行，不能把任意数据源属性引用当普通参数。最新规则和真实 LIB 验证见 docs/Verification-LibCompatibility-20260911.md；无静态阻塞不代表结果等同 HIS。中文和多选参数使用生成的 ASCII 绑定名；更新后重新导入。
- 自动分类与位置区分已确认绑定和同名菜单候选；候选不得伪称真实完整入口，停用菜单须注明。手工分类优先。静态选项扩展为向后兼容的 catalog 可选元数据，不保存用户输入与查询结果。
- Electron 同运行时/依赖的小更新使用 scripts/build-update.ps1；只交付 app.asar、Host EXE、Core DLL，按精确基线哈希检查、备份及回退。用隔离旧包验收更新，不覆盖基线包后再声称验证升级。
- 构建和离线检查：`powershell -File scripts/build.ps1`。真实 Oracle、Win10 32/64 位环境验收单独记录，未执行保持 NOT RUN。
- 用户要求仅用真实 LIB 时，使用 tests/desktop/lib-*-checks.cjs 和直接 dotnet build；旧 build.ps1 含生成测试数据，不用其结果替代真实 LIB 验证，不伪造 Oracle 结果行。
- 不自动 commit、push、部署或创建 PR。
- 0.1.4 岗位报表显示仅由 EXE 同目录的 report-visibility.xml 手工配置，不增加前端岗位选择/编辑入口。按报表 ID 过滤所有目录入口；缺文件兼容旧版显示全部，selected 空清单显示零张，错误配置停止启动，修改需重启。该配置是显示偏好，不是身份/数据库授权；不改变 catalog 格式、原 XML、SQL 或科室范围。
