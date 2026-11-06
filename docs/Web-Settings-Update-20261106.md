# ReportDesk Web 服务器更新包：设置分区与多连接

发布：`Settings-20261106-v1`。目标：**.NET Framework 4.6.2 / x64 / 精确旧 Core**。适用于已有 ReportDesk Web 站点的增量更新，不能用来新建站点。

## 本次更新

- 设置页分为数据库连接、本机报表目录、HIS 发布同步三个页签。
- 支持新增、编辑、保存多个 Oracle 连接及明确切换当前连接；新增连接不自动启用，也不继承其他连接的密码。
- 原单 XML 和目录导入按钮合为“导入报表”入口，保留两种方式。
- 保留已有本机目录、SQL/XML 编辑和同步功能。

只替换 **`bin/ReportDesk.Web.dll` 和 14 个 `UI` 文件**。不覆盖 Core、其他依赖 DLL、`Web.config`、`Global.asax`、数据目录、TNS 或数据库连接配置，不改数据库。本包不含本机或现场连接配置、密码、业务 XML。

要求服务器现有 `bin\ReportDesk.Core.dll` 的 SHA-256 精确等于：

```text
6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7
```

基线不同会拒绝更新。不要替换 Core 或跳过校验，应按实际 Core 另制匹配包。

## 服务器操作

在实际 IIS 服务器上操作，使用 **管理员 Windows PowerShell 5.1**。需要现有 IIS 管理组件。本包不安装组件，不修改系统策略，不调整应用池身份、端口或证书。

1. 完整解压到网站、数据目录和 HIS 目录之外，例如 `D:\ReportDeskUpdate\Settings-20261106-v1`。不要把整包直接覆盖到网站。
2. 右键 **`Check-WebUpdate.cmd` → 以管理员身份运行**，输入 ReportDesk 的实际 IIS 物理路径。该目录应直接包含 `Web.config`、`Global.asax`、`bin` 和 `UI`。成功显示 `CHECK PASSED`、`Status : Checked`、`Exit code: 0`。
3. 通知本应用使用者保存草稿、结束查询后，右键 **`Update-Web.cmd` → 以管理员身份运行**，输入同一路径。记下输出的 **`Backup/recovery directory`**。
4. 成功显示 `UPDATE VERIFIED`、`Status : Updated`、`Exit code: 0`。使用原网址按 **Ctrl+F5** 刷新，打开设置确认旧连接及新页签，再按现场授权做报表查询验收。

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

更新不会主动改写旧单连接文件。之后在网页主动保存连接时，才以兼容根字段的格式记录多连接列表。当前连接由同一应用进程内的查询和 HIS 同步共用；切换前核对目标库。保存非当前连接不切换。多个 IIS worker/实例没有实时切换广播。

本机 SQL/XML 保存写入的文件不包含在程序回滚范围内，仍应使用独立备份目录。数据库数据、查询结果和原始 HIS 目录不在本次更新操作范围内。

## 回滚与失败处理

更新失败时脚本会尽可能恢复旧程序并核对原站点。`original application automatically restored and health checked` 表示旧程序恢复已验证，更新本身仍为失败，退出码为 1。

主动回滚：以管理员运行 **`Rollback-WebUpdate.cmd`**，输入本次备份目录及原站点路径；或：

```powershell
.\Apply-WebUpdate.ps1 -SitePath 'D:\Sites\ReportDesk' -Rollback -BackupDirectory 'C:\ProgramData\ReportDesk\WebUpdateBackups\本次完整备份目录'
```

成功显示 `ROLLBACK VERIFIED`、`Status : RolledBack`。回滚只恢复此次程序文件，不重写数据库配置。如果升级后又在网页新增或修改了连接，自动回滚检测到配置变化会拒绝覆盖；保留当前配置与备份，先核对差异，再安排程序回退。旧版本若保存配置，会丢失新版附加的多连接列表，因此回退前需另备份最新 `connection.json`，不能盲目恢复旧连接覆盖后续修改。

`RECOVERY REQUIRED`、维护门禁失败或中途断电时，保留完整备份/恢复目录和 `journal.json`，不要盲删维护页、强制覆盖程序或配置。核对路径、权限和备份后，用同一包及备份继续恢复。脚本不会删除归属不明的维护文件。

## 验证边界

本包的目标框架、旧 Core 基线、依赖哈希、连接管理离线回归、更新/回滚配置保留、包内文件及 ZIP 哈希在本地检查。IIS 管理及相关维护事务使用隔离替身；真实服务器安装、现场 Oracle 登录/报表结果、浏览器原生目录权限仍需现场验收。离线通过不代表已部署。

程序功能的验证记录见 `docs/Verification-Web-Settings-20261106.md`；包内容和版本信息见 `manifest.json`、`release.json`。
