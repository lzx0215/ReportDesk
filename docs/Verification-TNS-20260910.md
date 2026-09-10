# TNS 连接新增验证 · 2026-09-10

范围：ReportDesk 0.1.1，新增直接/TNS 模式切换、只读读取配置、别名选择、旧目录兼容。未连接数据库，未修改 HIS、TNS 源文件或系统环境变量。执行环境是 Windows 11 x64；真实 Windows 10 与 Oracle 19c 验收保持 NOT RUN。

首轮结果：x86 构建及 43 项检查、UI 冒烟通过。x64 构建出现 CS8012 架构不匹配警告，检查进程失败，未交付该轮产物。定位到 x86/x64 共用 obj 中间文件，增量构建切换 PlatformTarget 未完整重编译引用程序集。脚本两处构建增加 --no-incremental，使每种架构完整重建。

最终重跑通过：powershell -File scripts/build.ps1 返回 0。x86/x64 应用和检查程序均构建成功，4 次构建均 0 警告、0 错误；两种进程各 PASS 43 checks，UI 均 PASS。已查看两种架构的 TNS 对话框截图，字段、状态与按钮完整。

额外核验：读取两套 ReportDesk.exe、ReportDesk.Core.dll 的 PE Machine，分别符合 x86 (0x14c) 与 x64 (0x8664)。读取两包 ZIP 内 exe、Core DLL、README.md、Acceptance.md 并计算 SHA256，与最终 release 目录对应文件全部一致。最终构建日志：artifacts/verification/build-tns-final.txt；包校验值：artifacts/packages/SHA256.csv。

新增离线验证覆盖：注释、多行、多别名、大小写、引号内括号和 #、SID/多地址/DESCRIPTION_LIST 保留、重复/缺失别名、IFILE 和错误语法拒绝、文件删除及内容更新、文件只读、大小限制、密码含特殊字符、路径与别名持久化、旧版无 TNS 字段目录兼容、未知模式拒绝。UI 冒烟操作真实连接对话框，验证模式启禁用、直接/TNS 切换、保存与重开恢复，并截图。

文件证据：artifacts/verification/{x86,x64}/checks.txt、ui/smoke.txt、ui/connection-tns.png。程序包：artifacts/packages/ReportDesk-0.1.1-win-{x86,x64}.zip。原 0.1.0 压缩包保留。
