# 保存原 XML 失败后的事务恢复修复

日期：2026-10-08。

## 问题和证据

现场在点击“保存到原 XML”后出现异常，随后 ASP.NET 启动报 `RecoveryRequired`。现场事务记录为一张查询 XML、`Committed=false`，备份 SHA256 与记录一致，目标 XML 不带只读属性；权限截图没有显示应用池账号对目标 XML 的修改权限。最初保存的异常日志尚未取得，因此现场第一次失败的具体系统错误仍未最终确认。

代码确认 Web 的 `sqlEditorSave` 使用 `ReportFileTransaction.ExecuteProtectedEdit`，保存使用 `SqlXmlEditor.Save` 的 `File.Replace`。旧事务实现对所有原有文件无条件执行回写恢复，即使保存失败前原文件没有变化，仍可能因相同的替换限制而再次失败并遗留 `pending.xml`；启动再次恢复时阻塞整个网站。

## 改动

- 先完整校验事务路径和备份哈希，再比较目标文件与事务前哈希；相同则跳过回写，缺失或变化的文件仍正常恢复。
- 只有文件恢复和目录重载成功后才清理事务标记。损坏备份、确实改变但无法恢复的文件、重载失败仍保持阻塞。
- 兼容已有 `pending.xml` 格式，可处理原文件未变的旧遗留事务，不需要手动删除或改写记录。
- 保存和恢复异常记录为脱敏日志，仅保留异常类型、HResult、方法调用栈；不记录原始消息、SQL、参数、凭据或文件内容。
- 回滚提示说明原文件可能未变化，并提示检查目标文件修改权限、占用及日志。

没有修改权限、数据库、SQL 业务口径、现场 Web.config 或原知识库。既有未提交修改保留，本次对原已修改文件只追加上述局部改动。

## 实际验证

- 修复前：新回归用例在真实 LIB 的隔离副本上复现 `RecoveryRequired`，检查程序返回失败；原 XML 未变化而回滚仍失败。
- 修复后：net48、net462 事务检查各 **24 组通过**，构建各 0 警告 / 0 错误。
- 覆盖实际 `SqlXmlEditor.Save` 在只读原文件及 NTFS 替换拒绝下的失败：原文件字节保持不变，目录重载完成，`pending.xml` 清理，后续恢复无阻塞。
- NTFS 用例最初仅限制文件写入/删除时，没有得到预期拒绝：父目录的 DeleteChild 权限仍允许替换。补充隔离父目录的 DeleteChild 限制后获得真实访问拒绝，最终通过；原文件和父目录 DACL 均在 finally 中恢复。该中间失败属于用例权限设置不足，未隐瞒为成功。
- 覆盖未修改时进程退出、混合批次仅恢复变化文件、损坏备份继续阻塞、变化文件恢复受阻继续保留标记、恢复权限后重试、日志脱敏。原有同步、冲突、ZIP、跨进程锁回归通过。
- net462 Web 构建 0 警告 / 0 错误；HTTP/安全回归 **58 项通过**。
- net462 真实 LIB 桥接回归 **18 项通过**，读取 1,284 份真实报表定义并核对所有选择入口；没有执行 Oracle。桥接中的标记表只验证传输和会话隔离，不代表真实数据库结果。
- 从保留的旧 ZIP 解压全新隔离网站，13 个基线 DLL 和补丁完整性检查通过；只替换 Web DLL 后使用本机 ASP.NET 4.8.9221 预编译成功，逐文件哈希检查仅 Web DLL 改变。修改后的 DLL 基线被检查脚本正确拒绝；不是现场 .NET 4.6.2/IIS 验收。
- 基线检查包装命令首次误用 PowerShell 脚本没有设置的 `$LASTEXITCODE`，导致脚本已显示 PASS 而包装判失败；改为调用原生 `powershell.exe`，使用其实际退出码后通过。产品检查脚本没有因此修改。
- 上述事务检查使用真实 LIB 字节的隔离副本；每次均核对原 LIB 来源哈希未变。不伪造 Oracle 查询结果。

使用命令（项目根目录）：

```powershell
dotnet build tests/ReportDesk.Web.Sync.Checks/ReportDesk.Web.Sync.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 --force --nologo -v:minimal
& tests/ReportDesk.Web.Sync.Checks/bin/Release/net462/ReportDesk.Web.Sync.Checks.exe
dotnet build tests/ReportDesk.Web.Sync.Checks/ReportDesk.Web.Sync.Checks.csproj -c Release --force --nologo -v:minimal
& tests/ReportDesk.Web.Sync.Checks/bin/Release/net48/ReportDesk.Web.Sync.Checks.exe
dotnet build tests/ReportDesk.Web.Checks/ReportDesk.Web.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 --force --nologo -v:minimal
& tests/ReportDesk.Web.Checks/bin/Release/net462/ReportDesk.Web.Checks.exe
```

详细证据保存在 `artifacts/verification/xml-save-recovery-20261008`。

## 交付和边界

为本地保存的 `ReportDesk-Web-net462-department-options-20260920.zip` 精确基线准备单 DLL 补丁。与该包逐字节比较：Core 和所有第三方 DLL 一致，只交付 `bin/ReportDesk.Web.dll`。现场实际 DLL 是否等于该基线需要先运行补丁内只读检查，未匹配则停止替换。

正常保存仍需要目标 XML 和所在目录具有合适的修改权限；修复不会自动授予权限，也不会将保存失败报成成功。现场遗留事务只有在备份有效、目标已一致或能实际恢复、目录能重载时才可自动清理。

IIS 现场替换、现场旧事务恢复、真实 Oracle 查询和业务结果核对：**NOT RUN**。服务器操作需要备份网站 DLL、相关 XML 和完整恢复状态，只操作 ReportWeb 池；程序回退不自动撤销 XML 恢复，相关文件回退需停止该池后根据备份单独处理。
