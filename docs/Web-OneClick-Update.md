# ReportDesk Web 一键更新与回滚

适用更新：`LocalHis-20261106`，.NET Framework 4.6.2 / LegacyCore。本包把本机 HIS 目录选择整合到连接设置，保留原数据库连接功能。它是已有 ReportDesk Web 的增量更新，不能用于新建站点或任意版本升级。

**2026-11-06 更新器 v2：**修正 v1 在写入维护页后只立即检查一次的问题，改为最多等待 30 秒，首页及 `api/session` 必须连续两轮返回 HTTP 503 和本次独有标记。仍不满足就拒绝替换程序。此次只调整更新器与说明，15 个程序更新文件及 Core 基线与 v1 完全相同。

现场 v1 曾在维护页确认阶段退出，随后用户确认原网站仍能打开；此阶段尚未备份程序或替换 payload。已用隔离响应重现“第一次仍返回旧网页就失败”，但现场实际 HTTP 状态未被 v1 记录，**不能据此断定现场根因一定是延迟**。v2 会保存状态码、标记是否匹配、尝试次数等摘要，供进一步定位，不记录 HTTP 正文或凭据。

## 1. 这次会改变什么

脚本只替换 `bin/ReportDesk.Web.dll` 和更新包指定的 14 个 `UI` 文件。不会覆盖 `ReportDesk.Core.dll`、其他依赖 DLL、`Web.config`、`Global.asax` 或服务器的 `DataDirectory/config`。

备份包括完整 `bin`、`UI`、站点根目录的 `.config` 文件和 `Global.asax`、`DataDirectory/config` 的所有文件。`connection.json` 及旧版 `catalog.json` 中的连接设置都在保护范围内。脚本不输出配置内容或密码，安装和回滚均不改写数据库连接配置。

备份不包含整个 HIS 目录、查询结果或 `DataDirectory` 的全部运行数据。程序回滚不能撤销用户已经保存到自己电脑的 SQL/XML；本机报表文件应通过功能中的独立备份目录恢复。

脚本不执行 `iisreset`，不停止站点、W3SVC 或应用池，不回收应用池，不修改绑定、HTTPS 证书、应用池身份、Load User Profile 或数据库配置。**ReportDesk 本应用会短暂进入维护状态并重新加载，正在使用的会话/查询可能中断，先通知使用者保存编辑。** 它不会主动重启同机其他项目。共享进程的资源影响以及实际 IIS 行为仍须现场验收，不能把离线测试当作生产零影响保证。

## 2. 开始前准备

在实际 IIS 服务器上操作，不是在普通办公电脑上直接运行。

1. 把新 ZIP 复制到服务器，完整解压到网站目录和 HIS 目录之外，例如 `D:\ReportDeskUpdate\LocalHis-20261106`。不要直接在 ZIP 内运行，不要把整包覆盖到网站。
2. 在 IIS 管理器找到 ReportDesk 对应的应用，点“基本设置”，记录**物理路径**。输入的目录应直接包含 `Web.config`、`Global.asax`、`bin`、`UI`，不是它的上一级目录或仅 `bin`。
3. 记录原来的 HTTPS 地址。本次现场示例为 `https://172.18.20.65:8443/`；如果 ReportDesk 是子应用，还要包含其路径，例如 `/ReportDesk/`。
4. 确认原站点目前可访问。脚本会做更新前健康检查，不会以“更新”代替修复原有启动故障。
5. 提前通知使用者退出本应用的查询/编辑，保留原来的应用池、身份、证书和端口。已保存密码依赖原运行身份的 DPAPI；本脚本保留身份及配置，未曾保存的会话内密码仍需重新输入。

需要 Windows PowerShell 5.1、管理员权限和 IIS 的 `Microsoft.Web.Administration` 管理组件。脚本不会安装系统组件或自动修改系统策略；启动器的 ExecutionPolicy 参数只作用于该 PowerShell 进程，组织策略仍可能阻止运行。

## 3. 建议先做只读检查

右键解压目录中的 **`Check-WebUpdate.cmd` → 以管理员身份运行**。

看到 `ReportDesk IIS physical application path` 后输入实际物理路径，回车，例如：

```text
C:\a_userspace\ReportTool\ReportDesk-Web
```

脚本会自动核对包内 15 个文件、Core、IIS 目录映射及应用 URL。成功显示：

```text
Core SHA256 matched: 6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7
CHECK PASSED.
Status : Checked
Exit code: 0
```

`CheckOnly` 不创建维护页或备份，不替换文件、不修改配置。其 HTTP GET 检查会正常访问网页和 session 端点；不调用数据库连接测试或执行报表 SQL。

### Core 基线到底怎么核对

这里的 Core 指站点 `bin\ReportDesk.Core.dll`，不是安装 .NET 的版本，也不是文件属性中的版本号。脚本比较完整 SHA-256：

```text
6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7
```

必须完全一致。不同就停止，不替换 Core、不使用“强制跳过”，需要按服务器实际 Core 重新制作匹配包。也可手工只读确认：

```powershell
Get-FileHash -LiteralPath 'D:\Sites\ReportDesk\bin\ReportDesk.Core.dll' -Algorithm SHA256
```

### 如果无法自动识别访问地址

HTTPS 通配绑定会尝试本机活动网卡的 IPv4 地址，仍严格验证证书；不会忽略证书错误。服务器上的管理员进程也需要信任该 HTTPS 证书，办公电脑可信不代表服务器已经可信。

如果显示 `No reachable trusted site URL`，在解压目录打开**管理员 Windows PowerShell**，指定已使用的地址重新检查：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -HealthUrl 'https://172.18.20.65:8443/' -CheckOnly
```

修改两个示例值，地址必须指向本机 IIS 中这个应用。脚本拒绝跳转、其他服务器地址、错误应用路径和绕过证书校验。不要为通过更新随意更换证书、改绑定或关闭安全验证。

## 4. 一键更新

检查通过后，右键 **`Update-Web.cmd` → 以管理员身份运行**，输入同一个物理路径。

如果检查时需要显式指定 URL，则使用同一 PowerShell 窗口执行：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -HealthUrl 'https://172.18.20.65:8443/'
```

默认备份写到 `%ProgramData%\ReportDesk\WebUpdateBackups\时间-唯一编号`，常见实际位置是 `C:\ProgramData\ReportDesk\WebUpdateBackups\...`。备份子目录仅允许 Administrators 和 SYSTEM 访问。需要改备份盘时增加：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -HealthUrl 'https://172.18.20.65:8443/' -BackupRoot 'E:\ReportDeskUpdateBackups'
```

所选备份根目录必须在所有 IIS 映射目录、网站目录、程序数据目录、HIS 目录和解压包之外，不能互相包含。脚本只为新建的此次备份子目录设置权限，不改用户已有备份根目录的权限。

更新顺序：

1. 核对 Core、固定更新文件 SHA-256、目录边界、原站点健康状态；同一应用一次只允许一个脚本操作。
2. 建立受限备份目录及恢复日志，打印 `Backup/recovery directory`。**记下完整路径。**
3. 在目标应用根目录放置本次独有的 `app_offline.htm`，轮询根页面和 `api/session`，最多等待 30 秒，连续两轮均为 HTTP 503 且含本次标记才继续。探测使用独立随机参数防缓存，不把维护标记放入 URL；每轮前后核对文件归属。
4. 备份程序及配置并核对所有备份哈希，确认现场文件没有变化。
5. 逐个暂存并验证固定更新文件，再原子替换单个目标文件；整个包通过维护页隔离，不声称多文件整体原子提交。
6. 再核对 Core、其他依赖、站点配置、全部连接配置及 IIS 映射/身份；移除自己的维护页，访问网页和 session 检查启动。
7. 成功显示 `UPDATE VERIFIED`、`Status : Updated` 和 `Exit code: 0`。保留备份，先完成验收。

不要关闭脚本窗口，不要同时手工覆盖程序、编辑连接配置或调整 IIS。

## 5. 验收先用 HIS 目录副本

1. 使用原来的 HTTPS 地址，按 `Ctrl+F5` 刷新。不要改原数据库连接；先确认连接设置中原值仍在。
2. 在测试人员电脑上复制一份 HIS 目录，另建独立备份目录；首次选择这一份副本。
3. 在连接设置里选择本机 HIS 副本及独立备份目录，授权浏览器读取和写入。
4. 找一份测试报表，只做容易撤销的 SQL 注释或 XML 编辑，保存后用本机编辑器核对副本文件及备份。
5. 确认原始 HIS 目录未改变，再用另一台电脑选择它自己的副本，确认各自保存到自己的电脑。
6. 分别用现场 Chrome、Edge 和 360 极速模式验收。已提供的 360 是 15.0.2040.0（32 位）、内核 114.0.5735.289；版本信息不能替代实际目录授权与保存测试。
7. 检查服务器上其他项目的原地址仍能正常访问。

更新脚本的健康检查不代表真实 Oracle 查询、浏览器原生目录授权或 HIS 结果等价验收通过。

## 6. 失败及回滚

如果 v1 曾显示 `did not return the expected offline marker with HTTP 503`，且原网站现在可以打开，请把 **v2 完整解压到新目录**，先运行新版 `Check-WebUpdate.cmd`，再运行新版 `Update-Web.cmd`，输入同一网站物理路径。不要把新旧脚本混在一起，不要跳过维护检查，也无需创建空的 DataDirectory 或更改数据库连接。

维护检查失败时，v2 会显示首页和 session 的实际 HTTP 状态、`marker=True/False`，把安全摘要保存在此次恢复目录的 `journal.json` 的 `OfflineEvidence` 字段中。仅已创建恢复目录并不代表备份已完成；维护页未确认时，脚本还没有备份或替换程序文件。

替换前退出时，只清理内容完全属于此次操作的维护页，再复查网站健康。检查通过后日志标记为 `Cancelled`，同时说明此次未替换程序；退出码仍为 `1`。清理或健康复查未确认时保留待恢复状态和 `Cleanup` 摘要，提示人工核对。外来维护页不会删除。不要把 `Cancelled` 当作更新成功，也不需要对已取消、未替换文件的操作执行程序回滚。

常见复制失败、文件校验失败或更新后健康检查失败时，脚本会尝试恢复旧程序、移除本次新增加的 UI 文件，再检查原站点。

显示 `original application automatically restored and health checked` 表示自动恢复已验证。因为本次更新未成功，退出码仍为 `1`；它不会把回滚伪装成安装成功。

更新成功后如需主动退回：右键 **`Rollback-WebUpdate.cmd` → 以管理员身份运行**，先输入该次输出的完整备份目录，再输入原站点物理路径。也可以执行：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -Rollback -BackupDirectory 'C:\ProgramData\ReportDesk\WebUpdateBackups\实际备份子目录'
```

回滚使用备份记录的应用地址，不要求原始 payload 仍在，但需要保留本包的两个 `.ps1` 脚本。成功显示 `ROLLBACK VERIFIED` 和 `Status : RolledBack`。回滚只恢复本次更新的程序文件，数据库配置仍保持原字节。

如果脚本被强行关闭，可用同一备份目录执行回滚。尚未开始写程序的 `Preparing` 阶段会取消本次维护，显示 `Status : Cancelled`；已进入更新阶段则核对旧/新文件并恢复。精确属于本次事务的暂存文件会清理。

**若显示 `RECOVERY REQUIRED`：**记录备份目录和错误，不要盲目删除维护页或覆盖配置。出现现场文件被别人修改、备份损坏、IIS 映射变化、磁盘/权限故障等情况，脚本宁可保留维护状态，也不会覆盖无法确认来源的内容。排除原因后，用同一备份重新执行回滚。重新上电后的磁盘损坏、数据丢失不属于脚本可保证恢复的情况。

若手工回滚前发现数据库配置或更新文件已发生后续修改，会在创建新的维护页之前拒绝，不撤销这些修改。原本属于以前中断操作的维护页也不会因此被误删。

## 7. 拒绝更新时如何处理

| 提示/情况 | 处理 |
| --- | --- |
| `Core baseline mismatch` | 保留现状，提供实际 Core SHA-256，以此重新制作匹配包；不要覆盖 Core。 |
| `Update package SHA256 mismatch` | 重新复制并解压完整更新包；不要手改 manifest 或脚本跳过。 |
| `app_offline.htm already exists` | 查明前次维护来源；如果是本脚本中断，使用对应备份回滚。 |
| `Offline verification failed` | 本次尚未替换程序时会尝试清理自己的维护页。记录两路 HTTP 状态和 `marker` 结果、恢复目录，并确认原网站是否能打开。持续 200、503 无标记或其他状态须查明原因，不能放宽检查或强行覆盖。 |
| `overlap`、`UNC`、`junction` 或多处映射 | 目录隔离无法证明，脚本拒绝；需要现场核对实际 IIS 应用/虚拟目录。 |
| `No reachable trusted site URL` / `certificate` | 显式指定原 HTTPS URL，核对服务器信任、端口、证书和应用路径；不跳过证书。 |
| 缺少 `Microsoft.Web.Administration` | 由管理员按现有服务器管理规范准备 IIS 管理脚本组件，本包不会安装它。 |
| 外置、继承、`location`、`clear/remove` 配置 | 当前自动包要求本站明确声明 DataDirectory 和 ReportsDirectory（后者可明确为空）；复杂有效配置需单独核对，脚本不改配置来凑条件。 |
| 备份 ACL/owner 拒绝 | 核实以管理员运行及备份盘支持 Windows ACL；脚本在替换程序前检查，不降低备份权限。 |
| 回滚时配置或程序已被改动 | 保留后续修改及备份，由管理员比较差异；脚本不强制覆盖。 |

## 8. 验证边界

开发验证使用独立目录、真实旧 Core/Web DLL 和旧 UI 补丁，配置为无凭据的测试标记。文件事务、错误路径及恢复已在 Windows PowerShell 5.1 检查；IIS 映射由测试替身提供，HTTP helper 另用本机 HTTP fixture 验证。

v2 本地 Windows PowerShell 5.1.26100.9711 验证共 **54/54 PASS**：文件更新与回滚 27 项、维护页门禁 13 项、失败清理主流程 5 项、真实 loopback HTTP 9 项。对应结果位于项目 `artifacts/verification/` 下：

- `web-update-20261106-145054-faae9e74/results.json`
- `web-update-offline-20261106-145120-ce07387d/results.json`
- `web-update-maintenance-20261106-145148-f1cf7449/results.json`
- `web-update-http-20261106-145328-b29f38d4/results.json`

本地未安装 IIS，IIS 映射与备份 ACL 创建边界由测试替身提供。医院服务器已由用户验证 v1 的只读检查通过，并报告 v1 维护页确认失败后原网站仍可访问。**v2 在医院真实 IIS 上的安装/回滚、真实 Oracle、360 原生目录选择和服务器多项目验收仍为 NOT RUN。** 请先运行新版 `CheckOnly`，再安排本应用的短维护窗口安装验收；开发侧没有连接医院服务器或执行生产部署。

维护页机制参考 Microsoft 的 [.NET Framework ASP.NET 应用离线说明](https://learn.microsoft.com/en-us/aspnet/web-forms/overview/deployment/advanced-enterprise-web-deployment/taking-web-applications-offline-with-web-deploy)。

Framework 源码的 [离线响应实现](https://github.com/microsoft/referencesource/blob/main/System.Web/HttpRuntime.cs#L1569-L1574) 明确返回 503；[文件变更通知](https://github.com/microsoft/referencesource/blob/main/System.Web/FileChangesMonitor.cs#L1154-L1183) 排队后在另一线程触发，因此脚本允许有界等待，但不降低离线确认标准。
