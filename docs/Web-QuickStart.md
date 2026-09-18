# ReportDesk Web 本地交付与部署说明

适用：单 IIS 站点、内网少量用户。使用现有 .NET Framework 4.8/Core；桌面程序保留。本说明不代表目标服务器已经验收。

若包名包含 `net462`，请优先阅读随包 `Web-net462-Compatibility.md`：该包要求 .NET Framework 4.6.2 或更高，不要求升级到 4.8，其余权限、配置和业务验收要求不变。

## 构建

在仓库根目录运行 `powershell -NoProfile -File scripts/build-web.ps1`。输出新的 `artifacts/web/ReportDesk-Web-时间/` 完整网站包及 SHA256 清单，不覆盖旧包，不配置 IIS。

发布包默认离线，自动同步关闭，维护白名单为空。不能只替换 DLL 就视为完成发布；网站包含 bin、UI、Web.config、Global.asax 和说明。构建脚本会把生成的依赖绑定重定向合入网站 Web.config；不要用源码中的 Web.config 直接覆盖已打包配置。

## 服务器准备（现场执行，不是构建脚本自动操作）

1. 备份现有 IIS 配置及本程序旧包，确认维护窗口。保留既有 80/8080/8081 网站和绑定。
2. 核实 .NET Framework 4.8、IIS ASP.NET 4.x 功能。新增一个独立 x64 应用池，CLR v4.0、Integrated、一个工作进程；加载用户配置文件，以验证服务账号 DPAPI。此版本不是 ASP.NET Core，不需要它的 Hosting Bundle。
3. 新建网站指向完整发布目录，使用院内 HTTPS 和批准网段。配置应用池 AlwaysRunning、网站 preloadEnabled 及已安装的 Application Initialization；现场验证无人打开页面时也启动。停 IIS 后同步暂停，重启补查。
4. 在站点之外准备固定数据目录，例如 `C:\ReportDeskData`。网站目录对应用身份只读，数据目录授予该程序必要读写权限，不能映射为静态网站。将完整 LIB 内容复制到 `C:\ReportDeskData\Reports`，保持相对目录；不在原 HIS/知识库目录运行编辑和同步。
5. 编辑部署副本 Web.config 中 `ReportDesk.DataDirectory`。`MaintenanceAddresses` 为空时，院内打开网站即可使用查询和维护全部功能；若填写 IP，则仅这些电脑能做维护。
6. 入口为 `/`（查询与维护同一页）。先以 Offline=true 检查目录、条件、来源及编辑功能；SQL 保存会写服务器副本，先用隔离副本测试。
7. 获准真实数据库测试后，才把 Offline 改为 false，在维护入口配置现场最小权限 Oracle 账号。密码只在服务器以运行账号 DPAPI 保存，或取消记忆仅进程内使用；应用回收后未保存密码要重新输入。更换服务身份时需重新配置，不复制开发电脑密文。
8. TNS 文件统一放在数据目录的 config 子目录内，并在 `ReportDesk.TnsFiles` 登记完整路径（分号分隔）。仅登记文件允许读取，不接受任意客户端路径。可不使用 TNS，直接配置主机/端口/Service Name。

部署配置不提交仓库。没有密码或端点信息应在现场填写，不发送到日志或文档。

## 院内 HTTP

院内电脑直接打开网站地址即可使用全部页面和功能，不再要求 HTTPS，也不再按客户端网段拦截。HTTP 会话 Cookie 不带 Secure；CSRF 与同源检查仍保留。`MaintenanceAddresses` 保持为空即全员可用维护功能。

## 自动更新报表

需要发布表的批准只读权限。先用已有发布样本验证 owner/字段、目录标志、压缩格式和解压后的 XML 哈希；不是为了测试再发布到全院。完成后才设置 `SyncSampleValidated=true` 和 `SyncEnabled=true`，默认每 30 秒检查，可调整 `SyncSeconds`。代码不会启动 HIP，也不会写发布表。

同步只处理可识别报表及其必要模板，不能用来更新整个 HIS 客户端。发布不完整、路径不安全、本地编辑冲突均停止对应批次；不因一次失败清空目录，不执行包内程序。暂存恢复文件仅为批次故障恢复，不是长期报表版本库。维护页显示同步状态，可立即检查。

首版每轮仍读取并校验所选历史发布 BLOB，没有实现仅元数据轮询缓存。历史多时需先测读取量与耗时，再设置启用后的 `SyncSeconds`，不能直接承诺 30 秒同步完成。最新完整候选替换较旧候选，不会把全部历史解压内容堆在内存中。超出压缩包/候选集安全容量会明确失败，不静默跳过；这些限制不作用于查询结果。

网页保存只修改本程序副本并重载，不代表已发布回 HIS。不要让 HIP 或其他复制程序与本程序同时更新同一 Reports 目录。

## 结果、取消与容量

查询/选项/导出只保存在服务器内存，默认会话空闲 60 分钟释放，可改 `IdleMinutes`；正在执行的任务不会因空闲计时清除。每个页面有独立任务；关闭页面不能保证立即通知服务器，IIS 回收后结果丢失需重查，程序不会自动重跑。不要在服务器共享同一浏览器会话来模拟两个独立使用者。

当前刷新页面会创建新的页面上下文，尚未实现方案中“尽力恢复原任务”的能力，需重新选择/查询；已有任务不会因此自动重跑。每个会话最多保留 16 个页面上下文，默认最多 64 个会话，空闲释放以整个会话为单位；关闭页面不等于立即释放。达到限制会提示，不清掉正在执行的任务。若使用时经常刷新并触及限制，应先补充页面释放/恢复机制再扩展使用。

默认最多 3 个同时处理的操作，忙时提示稍后重试，可配置 `MaxConcurrentOperations`。这不是 SQL 超时或结果行数上限，不返回截断结果。大报表和导出仍需现场看内存占用；不能据此承诺任意大查询。Excel 使用原格式容量限制，下载生成完毕才提供文件。

## 验证与回退

- 本地 HTTP 安全检查：构建并运行 `tests/ReportDesk.Web.Checks`，不连接数据库。`--serve <网站包绝对路径> <隔离数据目录绝对路径>` 只开启 localhost:18765 的离线预览，不是 IIS 验收。
- 两个浏览器分别查询/取消/翻页/导出，同浏览器两个标签也不能互相覆盖；普通来源不能直接调用维护接口。
- 用真实 LIB 的隔离副本验证 SQL 回读和列同步（字段描述需获准 Oracle），包括 SQL 已保存后再补齐模板。
- 自动同步现场验证一份完整发布：无需打开服务器 HIP，更新文件并重载；再验证断网恢复、文件冲突、应用重启补查。业务结果需与 HIS 同条件核对。
- 升级时停止接新任务，告知执行中任务可能中断，备份目标应用、配置及将变动的报表，再替换完整包。出错时切回旧包；报表文件若已改变须另外恢复，程序回退不撤销 XML 修改。不修改或回滚 HIS 数据库。

官方部署依据：[IIS 应用初始化](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/applicationinitialization/)、[ASP.NET 托管后台任务](https://learn.microsoft.com/en-us/dotnet/api/system.web.hosting.hostingenvironment.queuebackgroundworkitem?view=netframework-4.8.1)。
