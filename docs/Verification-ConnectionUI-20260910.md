# 0.1.2 连接窗口验证

用户目标：按提供的 Navicat 截图提供连接名、基本/TNS 类型、网络服务名、用户名、密码、保存密码、测试连接，以及常规/高级分组。

实现范围：当前单组连接设置增加名称；新增常见路径自动发现；多来源不默认选择；沿用只读解析 TNS 描述、直接连接及 DPAPI 保存密码。测试连接只 Open/读取 ServerVersion/Dispose，不执行报表 SQL。未修改数据库 schema、HIS、原资料或系统配置。

本地环境：Windows 11 x64，运行 x86 和 x64 进程；不是 Windows 10 验收。自动发现检查使用合成目录；UI 使用注入的发现函数和连接测试函数，不读取真实本机 Oracle 配置，也不联网。

离线检查包括原报表/SQL/导出回归、TNS 文件解析/更新、旧目录兼容、指定目录发现与去重、连接名持久化。UI 冒烟包括常规/高级截图、模式切换、保存重开、唯一文件加载、多个来源不自动选取、失效保存路径不回退、无服务阻止测试、测试期间阻止重复点击、成功/失败反馈、修改输入清除旧成功、未点击确定不持久化、关闭窗口后异步完成。

首轮 x86 核心 45 项检查通过，UI 检查失败：测试在 Show 返回后直接读取默认已完成的 discoveryTask，此时 WinForms 的 Shown 事件尚未执行。诊断证据显示文件未选择、别名数为 0、状态为空。修复测试辅助逻辑，等待真实 Shown 后再等待发现任务；定向 x86 构建与 UI 重跑通过。原失败保留在 artifacts/verification/build-navicat-style.txt 和 0.1.2/diagnostic/smoke.txt。

最终双架构构建通过：x86/x64 各 45 项核心离线检查与完整 UI 冒烟 PASS，四次构建均 0 警告、0 错误。已查看常规 TNS 与高级页截图，字段/按钮完整；程序包版本 0.1.2，保留 0.1.0/0.1.1 旧包。最终构建日志 artifacts/verification/build-navicat-style-final.txt；截图及检查证据 artifacts/verification/0.1.2/{x86,x64}。

复跑还发现新增发现测试复用了固定临时目录，上一轮创建的第二个配置影响“仅一个文件”的断言。已改为每次唯一的合成目录，避免依赖旧测试产物；失败日志保留为 artifacts/verification/build-navicat-style-fixture-failure.txt。测试约定：等待真实 UI 初始化完成；文件发现用例使用每次独立目录，不依赖历史运行状态。

限制：现场 Win10/DPI、Oracle 19c 真实登录、会话释放、网络超时及环境变量/注册表路径发现仍为 NOT RUN，见 Acceptance.md。无报表业务规则变更，不声称与 Navicat 所有驱动/钱包/SSH 功能等价。
