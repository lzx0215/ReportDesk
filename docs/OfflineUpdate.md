# ReportDesk 内网离线更新

适用于与此更新包基线一致的 Electron 0.2.0 x64 文件夹版。不是 WinForms 0.1.4 更新包，也不能用于其他 Electron 运行时。相同版本号不保证文件相同，更新脚本会检查实际文件 SHA256。

本次 LIB 适配更新默认显示全部报表，列表仅保留搜索栏。保留 HIS 位置、关联文件检查、Excel 导出和原进度条。按提供的 HIS 引擎补充交叉统计、组合列/横向合计、行分组、多选编码绑定及前置条件数据源。1,284 张报表中 1,262 张无静态阻塞，22 张仍有配置或执行依赖问题；真实 Oracle 执行、数值结果及选项交互尚未在本机验证，不能据此声称所有 HIS 报表已可正确查询。

`ReportDesk-0.2.0-lib-compatibility-20260911` 小包基于上一份 `ReportDesk-0.2.0-session-final-20260911` 已更新程序制作。脚本校验精确文件哈希；其他基线不能直接强制替换。完整原因见项目 `artifacts/audit/lib-compatibility-20260911/review.html`，验证范围见 `docs/Verification-LibCompatibility-20260911.md`。

每次打开后选择 HIS / LIB 目录或 XML，程序只把报表定义读入本次会话。左侧 ↻ 从原 XML 刷新当前清单，关闭后不保留清单。“普通门诊处方记录”和“门诊处方患者明细”保留已有表格适配；后者 dtALL 明细需要处方号和唯一号。数据不复刻打印版式公式。

HIS 位置继续区分已确认入口、同名候选和停用菜单。连接设置单独保存到 `%LOCALAPPDATA%\ReportDesk\connection.json`；密码仍只在勾选保存后用 CurrentUser DPAPI 加密。升级兼容读取旧 catalog 的连接设置，不恢复其中报表，也不改写或删除旧 catalog。

## 内网安装

1. 将更新 ZIP 复制到内网，解压到一个单独文件夹。
2. 关闭 ReportDesk，等后台退出。不要在正在查询或导出时替换文件。
   更新脚本备份程序三个文件。旧 catalog 保留原样；如需整体保留个人连接设置，可另备份 `%LOCALAPPDATA%\ReportDesk\connection.json`（如果存在）。
3. 在解压后的更新文件夹打开 PowerShell，执行下面命令。把 `D:\ReportDesk` 替换为**含 ReportDesk.exe 和 resources 的程序目录**，不是 HIS 目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Apply-Update.ps1 -TargetDirectory 'D:\ReportDesk'
```

`ExecutionPolicy Bypass` 仅作用于本次 PowerShell 进程，不修改系统策略。成功会输出 `PASS update` 和备份路径，然后启动原来的 `ReportDesk.exe`。

只检查、不写入：在同一命令末尾加 `-VerifyOnly`。已安装同一更新时输出 `already installed`，不重复覆盖。校验失败时不要强制替换；提供错误中的文件名，以制作对应基线的更新包。

## 替换范围与回退

仅替换以下三个文件，必须成套更新：

```text
resources\app.asar
resources\host\ReportDesk.Host.exe
resources\host\ReportDesk.Core.dll
```

不会替换 Electron EXE/运行时、数据库驱动、连接配置文件、`report-visibility.xml`、原 HIS XML、用户 catalog、保存的密码或日志。无需迁移用户数据。

更新前自动把原三个文件备份到程序目录的 `update-backups\时间-随机标识\`，并验证备份。复制/校验失败会尝试恢复；如果文件被占用导致恢复失败，保持程序关闭，保留备份并按下面方法回退。

关闭 ReportDesk，在更新文件夹执行（备份路径使用安装时输出的实际路径）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Apply-Update.ps1 -TargetDirectory 'D:\ReportDesk' -Rollback -BackupDirectory 'D:\ReportDesk\update-backups\实际备份目录'
```

成功输出 `PASS rollback`。需要手工复制时，先备份上述三个原文件，再把 `payload` **内部的 resources 文件夹**复制到程序目录，合并并替换三个文件；不能把整个程序目录删掉重建，也不能只换其中一个文件。推荐脚本方式，以免混用版本或缺少备份。

本次会话版不写旧 catalog，回退旧程序会恢复其原有报表库流程。旧程序不会读取新的 connection.json；如在会话版中改过连接设置，回退后须核对旧程序中的连接设置。

## 更新后使用

左侧文件夹图标 → 选择 HIS 根目录或报表目录。程序递归识别查询 XML 和 FarPoint 版式 XML，显示导入/匹配摘要；其他 XML 不作为报表导入。扫描警告不会静默丢弃，取消不保存本次半成品。

选择报表 → 报表说明 → 检查关联 XML：

- “已按显式路径匹配”：原 XML 的明细引用在扫描范围内对应一个版式文件。
- “名称候选”：来自主表命名约定，或原目录变动后按同名查找，尚未确认关联。
- “多个候选”：同名文件有冲突，不自动选取。
- “未找到/引用拒绝”：配套文件缺失或路径超出扫描范围等。

匹配信息按需读取原文件，不复制或执行版式文件。应保持源目录可访问；当前会话记住所选扫描根目录，重启后清单及扫描根目录均清空，重新选择 HIS / LIB 目录或 XML 即可。

扫描不跟随目录链接，不读取外部实体，也不自动访问 XML 指向的外部共享目录。主表当前按“查询设置 → 报表设置”命名约定查找；明细读取 `ReportInfo/DetailDirectory`。未知引用方式、FarPoint 内部映射执行和 HIS 业务结果仍需另行适配验证。

## 开发侧后续生成小包

在项目根目录执行：

```powershell
powershell -File scripts/build-update.ps1 -BaseDirectory '上一份已交付的完整程序目录'
```

只构建 Host 并重新生成 app.asar，不下载/重新打包 Electron。依赖或运行时变化会拒绝生成此类小包，届时使用完整构建。生成后仍须完成离线检查与在隔离旧包上的更新/回退验收，再交付内网。
