# ReportDesk Web 服务器更新包：报表来源统一入口

发布：`ReportSources-20261106-v1`。目标：**.NET Framework 4.6.2 / x64 / 精确旧 Core**。适用于已有 ReportDesk Web 站点的增量更新，不能用来新建站点。本包包含此前 Settings 版本的设置分区及多连接功能；已安装该版本或仍使用相同旧 Core 的站点均按本文先检查再更新。

## 本次更新

- 左侧统一为“报表来源”入口，分为“打开本机目录”和“导入到服务器”两个页签。
- “打开本机目录”选择当前电脑的 HIS 报表目录和独立备份目录，点击“使用此目录”后，SQL/XML 修改写回当前电脑。
- “导入到服务器”保留 XML 文件和文件夹两种导入方式，将副本上传到服务器，后续修改保存在服务器。本机模式下先明确切回服务器目录，再导入；切回会清除本页查询结果。
- 设置入口保留“数据库连接”和“HIS 发布同步”，继续支持新增、编辑、保存多个 Oracle 连接及明确切换当前连接。报表来源操作不保存、测试或切换数据库连接。

只替换 **`bin/ReportDesk.Web.dll` 和 14 个 `UI` 文件**。不覆盖 Core、其他依赖 DLL、`Web.config`、`Global.asax`、数据目录、TNS 或数据库连接配置，不改数据库。本包不含本机或现场连接配置、密码、业务 XML。

要求服务器现有 `bin\ReportDesk.Core.dll` 的 SHA-256 精确等于：

```text
6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7
```

基线不同会拒绝更新。不要替换 Core 或跳过校验，应按实际 Core 另制匹配包。此前 Settings v1 交付包保留不变；本包使用独立发布标识和备份记录。

## 服务器操作

在实际 IIS 服务器上操作，使用 **管理员 Windows PowerShell 5.1**。需要现有 IIS 管理组件。本包不安装组件，不修改系统策略，不调整应用池身份、端口或证书。

1. 完整解压到网站、数据目录和 HIS 目录之外，例如 `D:\ReportDeskUpdate\ReportSources-20261106-v1`。不要把整包直接覆盖到网站。
2. 右键 **`Check-WebUpdate.cmd` → 以管理员身份运行**，输入 ReportDesk 的实际 IIS 物理路径。该目录应直接包含 `Web.config`、`Global.asax`、`bin` 和 `UI`。成功显示 `CHECK PASSED`、`Status : Checked`、`Exit code: 0`。
3. 通知本应用使用者保存草稿、结束查询后，右键 **`Update-Web.cmd` → 以管理员身份运行**，输入同一路径。记下输出的 **`Backup/recovery directory`**。
4. 成功显示 `UPDATE VERIFIED`、`Status : Updated`、`Exit code: 0`。使用原网址按 **Ctrl+F5** 刷新，确认“报表来源”两种方式、设置中的原有连接，再按现场授权做报表查询验收。

如果不能自动识别原访问地址，在解压目录的管理员 PowerShell 中运行（替换两处示例值）：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -HealthUrl 'https://reportdesk.example.invalid/' -CheckOnly
# 只读检查通过并安排好维护窗口后：
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -HealthUrl 'https://reportdesk.example.invalid/'
```

URL 必须是本服务器该 IIS 应用的真实地址，子应用要包含子路径；严格校验证书，不绕过证书错误。检查失败时保留原站点，不手动跳过门禁。包内文件哈希不符时重新复制完整 ZIP；Core 不符时停止。

## 配置保护与影响

更新器沿用已验证的 v2 维护门禁：本应用首页及 `api/session` 最多等待 30 秒，必须连续两轮为 HTTP 503 且带本次维护标记，才能开始替换程序。只读检查不会创建维护页或修改配置；HTTP 检查不调用数据库测试或报表 SQL。

安装前备份完整 `bin`、`UI`、站点根 `.config`/`Global.asax` 以及 `DataDirectory/config` 的所有文件，包含现有 `connection.json` 和旧 `catalog.json`。安装后按 SHA-256 验证受保护配置及依赖逐字节不变。默认备份目录为 `%ProgramData%\ReportDesk\WebUpdateBackups\时间-唯一编号`，本次新建子目录限制为 Administrators/SYSTEM。

更新只让本应用短暂进入维护状态和重新加载，现有查询/会话可能中断；不执行 `iisreset`、不停止同机其他项目、不修改或回收应用池。原应用池身份及用户配置文件应保持，已保存密码仍由原身份 DPAPI 解密。**此前未勾选保存、仅留在进程内的临时密码，应用重载后需重新输入。**

更新不会主动改写已有连接文件。网页中主动保存连接，才会记录连接配置；兼容旧单连接根字段并支持多连接列表。当前连接由同一应用进程内的查询和 HIS 同步共用；切换前核对目标库。保存非当前连接不切换。多个 IIS worker/实例没有实时切换广播。

本机目录模式需要可信 HTTPS 和支持目录读写的浏览器，并由使用者选择及授权目录。所选目录下全部 XML 会发送到服务器的独立工作区，建议选择报表子目录并先用副本验证。HIS 发布同步作用于服务器报表目录，不作用于本机目录。SQL/XML 保存不等于发布回 HIS。

本机 SQL/XML 保存写入的文件不包含在程序回滚范围内，仍应使用独立备份目录。数据库数据、查询结果和原始 HIS 目录不在本次更新操作范围内。

## 回滚与失败处理

更新失败时脚本会尽可能恢复旧程序并核对原站点。`original application automatically restored and health checked` 表示旧程序恢复已验证，更新本身仍为失败，退出码为 1。

主动回滚：使用**本包**的 **`Rollback-WebUpdate.cmd`**，输入本次 ReportSources 更新的备份目录及原站点路径；或以管理员运行：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -Rollback -BackupDirectory 'C:\ProgramData\ReportDesk\WebUpdateBackups\本次完整备份目录'
```

成功显示 `ROLLBACK VERIFIED`、`Status : RolledBack`。回滚恢复本次安装前的程序文件，不重写数据库配置；不能交叉使用 Settings 包与 ReportSources 包的备份记录。如果升级后又在网页新增或修改了连接，自动回滚检测到配置变化会拒绝覆盖；保留当前配置与备份，先核对差异，再安排程序回退。更早、不支持多连接的版本若保存配置，会丢失新版附加的多连接列表，因此回退前需另备份最新 `connection.json`，不能盲目恢复旧连接覆盖后续修改。

`RECOVERY REQUIRED`、维护门禁失败或中途断电时，保留完整备份/恢复目录和 `journal.json`，不要盲删维护页、强制覆盖程序或配置。核对路径、权限和备份后，用同一包及备份继续恢复。脚本不会删除归属不明的维护文件。

## 验证边界

本包的目标框架、旧 Core 基线、依赖哈希、连接管理离线回归、更新/回滚配置保留、包内文件及 ZIP 哈希在本地检查。IIS 管理及相关维护事务使用隔离替身；真实服务器安装、现场 Oracle 登录/报表结果、浏览器原生目录权限仍需现场验收。离线通过不代表已部署。

报表来源功能的验证记录见 `docs/Verification-Web-ReportSources-20261106.md`；此前设置及多连接功能见 `docs/Verification-Web-Settings-20261106.md`。包内容和版本信息见 `manifest.json`、`release.json`。
