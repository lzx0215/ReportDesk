# Web .NET Framework 4.6.2 兼容包

本包仅用于 Web；桌面版仍以 net48 构建。页面、接口、业务规则、维护 IP 校验、CSRF、会话隔离及同步保护不变。不是只将 Web.config 的版本号改低：Web/Core DLL 与依赖均需按 net462 重新构建。

构建：`powershell -NoProfile -File scripts/build-web.ps1 -TargetFramework net462`。默认不传参数仍构建 net48。兼容构建使用独立 packages.net462.lock.json，不覆盖桌面依赖锁；输出使用全新目录避免混入旧 DLL。

## 服务器替换与回退

1. 在维护窗口备份当前网站完整目录、Web.config，以及站点外的数据目录。只停止 ReportDesk 自己的应用池，勿执行全局 iisreset 或停止 HIS；执行中的查询会中断、内存结果会丢失。
2. 解压完整兼容包到新目录，不向旧 bin 叠加文件。保留新包的 bin、Global.asax、UI、依赖绑定重定向，以及两个 targetFramework="4.6.2"。
3. 将旧配置的 ReportDesk.* 现场设置逐项迁入新 Web.config，勿直接用旧 Web.config 覆盖。数据目录、应用池身份及权限保持不变；不复制开发机密码密文。本包允许院内 HTTP，`MaintenanceAddresses` 为空时全部功能可用。不要把旧包里的 `AllowIntranetHttp=false` 再拷回去，否则仍会被拒绝。
4. IIS 应用池仍为 x64、CLR v4.0、Integrated，启用 ASP.NET 4.x。当前现场池名为 `ReportWeb`：只停止该池，将本站物理路径切到新目录并启动该池，不升级系统 Framework，不执行全局 iisreset。
5. 用 `http://172.18.20.65:8083/` 打开后应能使用查询和维护。在批准后验证 Oracle 查询/取消/导出。框架兼容不代表真实业务验收。
6. 出错时只停止 `ReportWeb` 池，切回备份的 **net462** 网站目录与 Web.config 并启动该池。不要用旧 net48 包回退。程序回退不撤销 XML 编辑或同步，必要时单独恢复数据备份；不回滚 HIS 数据库。

HTTPS、本机 HTTP（`AllowLoopbackHttp`）及院内 HTTP 白名单见 [Web-QuickStart.md](Web-QuickStart.md)。Web.config 的修改会触发应用重启；诊断结束后恢复原错误隐藏设置。不要把旧版 Framework 的系统补丁维护当作已完成。

## 验证边界

2026-09-18 院内 HTTP 白名单增量：本地 net462 `tests/ReportDesk.Web.Checks` 63 项通过（含原 HTTP/安全回归）。业务桥接与同步检查本轮未重跑，不得冒称已复验。此前记录：本地 net462 编译零警告零错误；HTTP/安全 30 项、业务桥接 17 项（1,284 份真实 LIB 元数据）、同步 17 组通过。同步检查新增空名称/有名称 ZIP 的 Unix 符号链接与 Windows 重解析属性拒绝用例；原 LIB 哈希不变。只将不兼容的 ZipArchiveEntry.ExternalAttributes 改为读取已校验中央目录的同一字段，不取消保护，不引入生产依赖。

本机运行时 Release=533509，高于 4.6.2。只有在真实仅安装 4.6.2 的目标服务器上运行，才能确认该环境的兼容性；新运行时上的 net462 编译与测试不能代替此验收。IIS、真实 Oracle、实际发布 BLOB 和服务身份 DPAPI：NOT RUN。随包 Verification-Web-20260918.md 是首版 4.8 的历史记录，其浏览器检查不可冒称本轮已重跑。

本轮中途失败：首次缺编译参考程序集，强制还原后补齐；直接全局 TargetFramework 参数未使引用项目正确选择目标，已改用专用 ReportDeskWebTargetFramework 属性贯穿项目；ZIP 新接口编译失败，已用等价文件头读取和拒绝用例修复；桥接测试第一次传入了重复 LIB 层级，纠正调用路径后通过。

复验：对三个检查项目分别运行 `dotnet build <项目路径> -c Release -p:ReportDeskWebTargetFramework=net462 --force`，再运行各自 bin/Release/net462 下 EXE。BridgeChecks 的参数是 `D:\系统知识库\00_Inbox\yljhis\LIB`，同步检查默认内部 LIB 路径。包检查使用 `powershell -NoProfile -File tests/Test-WebPackage.ps1 -PackageDirectory <包目录>`。

补充验证：桌面 Host 默认 net48 构建零警告零错误，net48 同步 17 组回归通过，SQL 编辑前端 29 项通过。本机 ASP.NET 4.8.9221 预编译兼容网站通过（不是 4.6.2 IIS 验收）。包内配置、Web/Core 的 4.6.2 目标元数据及 33 个文件哈希通过检查；缺少目标属性的三个第三方 DLL 使用 NuGet 实际选中的 net461/net462 运行库路径与逐字节 SHA256 比对确认来源。

包检查脚本调试中遇到第三方 DLL 没有 TargetFrameworkAttribute、Windows PowerShell 参数默认值的路径求值，以及新版 assets.json 使用 net462 键而非长框架名称；均已修正后重跑通过，未将这些工具失败计为成功。
