# ReportDesk SQL 编辑离线更新（2026-09-17）

后续源码变更：用户要求 SQL 保存不再生成备份。当前保存直接覆盖所选原 XML 中的数据源 SQL，保留写后回读校验；下述已交付离线更新包的备份说明属于原版本行为。后续构建应使用当前源码，不能凭相同版本号判断包内容。

适用于 2026-09-14 交付的 ReportDesk-results-toolbar Electron 0.2.0 Windows x64 完整包。相同版本号不保证文件相同，脚本会以 manifest.json 中的 SHA256 检查基线；不匹配时停止，不能强制混用。不是 WinForms 或 Windows x86 更新包。

## 安装

1. 将 ZIP 解压到一个单独文件夹。
2. 关闭 ReportDesk，等待后台退出。
3. 在解压后的更新文件夹打开 PowerShell，执行以下命令。将 D:\ReportDesk 改为含 ReportDesk.exe 和 resources 的程序目录，不是 HIS 配置目录。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Apply-Update.ps1 -TargetDirectory 'D:\ReportDesk'
```

成功显示 PASS update 和 Backup 备份路径。随后手动打开原来的 ReportDesk.exe。

只检查、不替换：在命令末尾加 -VerifyOnly。重复安装显示 already installed；出现 File verification failed 表示文件与此补丁不匹配，请保留错误信息，不要绕过校验直接覆盖。ExecutionPolicy Bypass 仅影响本次进程，不修改系统策略。

## 更新内容

选择报表 → SQL 编辑 → 选择主表或明细数据源 → 修改 SQL → 保存到原 XML → 确认目标文件和数据源。

- 复制 SQL：复制当前编辑区完整文本，不附报表名，不代入条件值。
- 保存：仅替换对应 Sql 内容，保留其他 XML 配置；自动生成带时间与唯一标识的 .bak 文件。保存前检测原文件是否已被外部修改。
- 保存后：重新加载当前报表，清除旧结果，重新初始化查询条件。关闭编辑窗口、核对条件后手动开始查询；保存不会自动执行 SQL。
- 文件已保存但重载失败时，界面明确提示，并禁止执行旧内存定义。
- 条件数据源和内置演示只支持查看、复制；本批不包含报表列模板编辑。
- 静态检查不连接数据库，不代表 Oracle 执行或 HIS 结果、版式已验证。

请先使用报表 XML 副本试用编辑功能。主动保存会修改所选原文件；如其他 HIS 程序使用该文件，其下次读取时可能使用新 SQL。避免其他程序同时编辑同一文件。

## 替换范围与备份

只替换下列三个文件，必须成套更新：

```text
resources\app.asar
resources\host\ReportDesk.Host.exe
resources\host\ReportDesk.Core.dll
```

安装脚本不会修改原报表 XML、连接配置、保存密码、导入来源、岗位显示配置、日志或 Electron/Oracle 依赖。无需迁移用户数据。

脚本自动将旧程序文件备份到程序目录的 update-backups\时间-随机标识\。替换或校验失败会尝试恢复旧文件；若恢复失败，请保持程序关闭并保留备份。

## 回退

关闭程序，在更新文件夹执行；将程序路径与备份路径替换为安装时的实际路径：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Apply-Update.ps1 -TargetDirectory 'D:\ReportDesk' -Rollback -BackupDirectory 'D:\ReportDesk\update-backups\实际备份目录'
```

成功显示 PASS rollback。程序回退只恢复三个程序文件，不撤销已经通过 SQL 编辑器保存的 XML；XML 原始内容可从其同目录的对应 .bak 备份找回。

## 验证边界

本补丁的具体离线验收结果见随包 VERIFY.txt。未连接内网 Oracle/HIS；Windows 10 现场兼容性、Oracle 执行与业务结果需要现场验收。

## 后续生成小包

在项目根目录执行，明确指定上一份完整客户端目录和新的输出目录：

```powershell
powershell -File scripts/build-update.ps1 -BaseDirectory '上一份完整程序目录' -OutputDirectory '新的更新输出目录'
```

运行时或 Host 依赖发生变化时，小包生成会被拒绝，需要制作完整包。应使用隔离旧包验证更新与回退，不覆盖基线包后再做升级验收。
