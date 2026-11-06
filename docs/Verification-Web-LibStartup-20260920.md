# Web 完整 LIB 启动修复（net462）

## 证据与修复范围

现场截图显示 `ReportsDirectory=C:\LIB`，`Offline=true`、`SyncEnabled=false`、`SyncSampleValidated=false`，并显示被 `customErrors=On` 隐藏的 ASP.NET 运行时错误。截图没有异常类型和堆栈，不能直接确认现场根因。

本地使用真实 LIB 的 3,687 个 XML 副本重现旧版完整启动失败：`BackendAdapter` → `SyncCoordinator.InitializeBaseline` → `ReportFilePolicy.ValidateFile` → `SyncSafetyException: ReportXmlInvalid`。部分旧 Spread 模板包含 XML 禁止的空字符。报表查询定义可以导入，但启动基线对配套模板执行下载校验，导致整个站点失败；离线和禁用同步时也会触发。

此次仅调整基线登记：记录已发现文件的原始 SHA256，用于本地修改冲突检测，不把基线登记当作批准下载。路径、重解析点、配套文件发现、下载 XML 校验、批次完整性、事务回滚及已有哈希不重置规则保持不变。原 XML 不清洗、不修改。缺失的基线文件仍报错。

此前 2026-09-18 的全库检查只覆盖业务桥接，完整 HTTP 启动预览只使用少量 XML，遗漏了“完整 LIB + 同步基线初始化”的组合。本次新增完整生产 BackendAdapter 的启动、重启、目录接口及 XML 哈希检查入口，不再以单独业务桥接通过代替启动验证。

## 复验入口

本轮 net462 Web、HTTP 检查项目、同步检查项目构建均为 0 警告、0 错误。HTTP/目录检查 58 项、同步检查 18 组通过；新增同步回归使用真实旧模板字节，验证基线保持原样、非法下载待处理、有效完整替换成功、重建基线不抹掉本地冲突。原始样本哈希不变。

实际 HttpListener 预览使用整份 LIB 副本，主页 HTTP 200，目录接口成功返回 1,284 张报表。预览与交付包 Web/Core DLL 的 SHA256 一致。包内 Framework 目标、依赖和 36 个文件哈希检查通过。HttpListener 不是 IIS。

中途失败记录：首轮完整启动测试已打开主页并返回 1,284 张报表，但同步状态任务超出检查工具原有约 3 秒等待窗口；当时还有另一预览进程在扫描同一副本。停止该预览，并仅将完整 LIB 检查的任务等待窗口改为约 60 秒，普通 HTTP 检查保持原窗口，生产程序未新增或变更超时。

最终完整启动检查通过：新隔离数据目录首次启动及再次重启均返回主页 HTTP 200、1,284 张报表，同步状态任务成功且保持离线；3,687 个 XML 的启动前后 SHA256 全部一致。新检查通过生产 `WebRuntime` 和 `BackendAdapter`，未注入替代后端。

```powershell
dotnet build tests/ReportDesk.Web.Checks/ReportDesk.Web.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462
dotnet build tests/ReportDesk.Web.Sync.Checks/ReportDesk.Web.Sync.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462
& tests/ReportDesk.Web.Checks/bin/Release/net462/ReportDesk.Web.Checks.exe
& tests/ReportDesk.Web.Sync.Checks/bin/Release/net462/ReportDesk.Web.Sync.Checks.exe
# 以下三个路径替换为新包目录、隔离数据目录、完整 LIB 副本目录；1284 为本地资料的预期报表数。
& tests/ReportDesk.Web.Checks/bin/Release/net462/ReportDesk.Web.Checks.exe --check-startup '<package>' '<isolated-data>' '<copied-LIB>' 1284
```

必须传入 LIB 副本：启动会在报表目录创建锁文件，在隔离数据目录写目录缓存和同步基线。检查不连接 Oracle，不伪造查询结果。

## 服务器替换

1. 备份目前网站目录和 Web.config；保留 `C:\LIB`、`C:\ReportDeskData` 备份。只停止 `ReportWeb` 应用池，执行中的本站任务会中断，不执行全局 iisreset。
2. 解压完整 net462 新包到新目录。使用新包的 DLL、UI 和依赖绑定配置，将旧 `ReportDesk.*` 现场参数逐项迁入；保留 `ReportsDirectory=C:\LIB`、`DataDirectory=C:\ReportDeskData` 和应用池身份。
3. 将本站物理路径切到新目录并启动 `ReportWeb`。先验收页面能打开、来源为 `C:\LIB`、报表列表可见。此次启动修复不要求开启 Oracle 或自动同步。
4. 若仍报错，备份 Web.config，将已有 `<customErrors mode="On" />` 临时改为 `<customErrors mode="RemoteOnly" />`，在服务器本机访问 `http://localhost:8083/`，记录异常类型、说明、堆栈前几行，然后恢复 `On`。配置变更会重启应用；不要把完整配置或密码发送出来。
5. 回退时只停止本站应用池，切回原网站目录和原配置，再启动。启动修复不改 LIB XML；后续编辑或同步的文件改动需单独使用备份恢复。基线只保存文件路径/哈希，不修改数据库。

## 自动同步边界

当前截图中的三个开关使自动下载保持关闭。新包修复启动，不代表已完成其他主机发布到本服务器的联调。现场验证发布表权限、实际发布压缩格式、配套完整性及连接可用后，才能设置 `Offline=false`、`SyncEnabled=true`、`SyncSampleValidated=true`；不能仅改验证标志来跳过实际核对。

已启用且站点运行时，服务器每轮完成后等待 30 秒再检查发布；下载成功重载后，空闲且可见的网页每 10 秒检查版本并刷新列表，查询中或弹窗中推迟。新文件仍严格校验，含非法 XML 字符的发布会待处理，不自动清洗或写入。IIS 无人访问持续同步还需 Application Initialization、AlwaysRunning、preloadEnabled 和空闲超时配置，并独立验收。

现场 IIS、仅安装 .NET Framework 4.6.2 的环境、Oracle、其他主机真实发布下载及 HIS 结果对照：NOT RUN。未部署、未 commit/push。
