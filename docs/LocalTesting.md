# 本机测试

2026-09-17 用户指定 `E:\his\LIB` 为本机 HIS 报表测试资料，数据库使用本机 Oracle。旧 D 盘知识库路径不再作为本机默认测试位置。`REPORTDESK_LIB` 可覆盖桌面 LIB 测试默认路径。

## SQL 编辑回写验证

在仓库根目录执行。每个命令退出码必须为 0；PowerShell 中应逐条运行并核对退出码。

```powershell
dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -o artifacts/host
dotnet run --project tests/ReportDesk.SqlEditing.Checks/ReportDesk.SqlEditing.Checks.csproj -c Release
node --test tests/desktop/sql-editor-checks.cjs
node tests/desktop/sql-editor-e2e-checks.cjs 'E:/his/LIB/Config/Xml/普通门诊处方记录查询设置.xml' E:/his/local_sandbox/reportdesk-sql-editing
```

端到端测试启动真实 Electron 界面，经 IPC 和 Host 保存至 E 盘副本，连续覆盖两次，核对磁盘 SQL、其他数据源未变、无新备份或残留临时文件、重新打开后的 SQL 和原资料保持不变。包含 `ORDER BY SORT_ID` 的报表会验证注释和取消注释两个方向。测试自动批准的是隔离测试窗口内的原生对话框，没有替换保存服务或伪造文件写入结果。可以追加第四个参数（EXE 路径）测试实际打包程序。

## 本机数据库验证（明确连接本机）

```powershell
dotnet run --project tests/ReportDesk.LocalOracle.Checks/ReportDesk.LocalOracle.Checks.csproj -c Release -- 'E:/his/LIB/Config/Xml/普通门诊处方记录查询设置.xml' E:/his/LIB/Conf/ObjectConfig.xml E:/his/local_sandbox/reportdesk-sql-editing
```

测试仅从指定配置读取已有用户名和密码到内存，强制连接 `127.0.0.1:1521/ORCL`，不使用配置内的远程地址，不输出或保存密码，不修改数据库账号、权限或表数据。

使用真实查询 XML 的副本，将原 SQL 包装为零行查询后通过 SQL 编辑服务保存，再通过 Host 查询服务使用绑定日期在 Oracle 执行。验证连接、真实 SQL 解析、对象访问、参数绑定、磁盘回写及重新加载；不证明真实患者数据、金额汇总、HIS 版式或全部报表业务口径一致。

测试证据位于 `artifacts/verification/desktop/`。E 盘隔离副本保留在 `E:/his/local_sandbox/reportdesk-sql-editing/` 供核对；不修改 `E:/his/LIB` 原资料。当前保存直接覆盖原文件，不生成备份，已有历史备份不会自动删除。

本轮针对用户反馈的报表，可将以上命令中的 XML 改为 `E:/his/LIB/Config/Xml/普通会诊及时完成率查询设置.xml`。Oracle 检查只为当前主表需要的日期条件赋值，明细的其他参数不参与主表测试。

## 报表列同步验证

```powershell
dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -o artifacts/host
dotnet run --project tests/ReportDesk.SqlEditing.Checks/ReportDesk.SqlEditing.Checks.csproj -c Release
node --test tests/desktop/sql-editor-checks.cjs
dotnet run --project tests/ReportDesk.LocalOracle.Checks/ReportDesk.LocalOracle.Checks.csproj -c Release -- --layout 'E:/his/LIB/Config/Xml/(病案)观察室工作日志查询设置.xml' E:/his/LIB/Conf/ObjectConfig.xml E:/his/local_sandbox/reportdesk-layout-editing
node tests/desktop/layout-editor-e2e-checks.cjs 'D:/aiproject/ReportDesk/artifacts/layout-reconcile/ReportDesk-win32-x64/ReportDesk.exe'
```

端到端测试需要已经打包的新 EXE；可依次追加真实查询 XML、已有本机 ObjectConfig.xml 路径。它启动独立数据目录的测试窗口，不使用/关闭用户当前窗口。测试凭据仅留在内存，强制 loopback，`remember=false`。模板列预览只读数据库结构；测试保存只覆盖副本，原库两个 XML 字节保持不变。完整支持范围及不支持案例见 [报表列同步](ReportLayoutEditing.md)。

### SQL 已保存、模板待补齐

下列命令针对当前「(病案)观察室工作日志」的 6 个 SQL 字段、5 列模板测试副本。保留现有 SQL 和统计逻辑，仅补齐模板；核对查询 XML 字节和修改时间均不变，再次同步不会重复加列。

```powershell
dotnet run --project tests/ReportDesk.LocalOracle.Checks/ReportDesk.LocalOracle.Checks.csproj -c Release -- --layout-reconcile 'E:/his/LIB/Config/Xml/(病案)观察室工作日志查询设置.xml' E:/his/LIB/Conf/ObjectConfig.xml E:/his/local_sandbox/reportdesk-layout-editing
node tests/desktop/layout-editor-e2e-checks.cjs 'D:/aiproject/ReportDesk/artifacts/layout-reconcile/ReportDesk-win32-x64/ReportDesk.exe' 'E:/his/LIB/Config/Xml/(病案)观察室工作日志查询设置.xml' E:/his/LIB/Conf/ObjectConfig.xml --reconcile
```

此模式只识别数据库字段，不执行患者明细查询。仍只写隔离副本；真实 HIS 显示、明细跳转、打印和业务口径须另外验收。

## 综合回归边界

`tests/ReportDesk.Checks` 的旧 HIS 适配断言已在合并前 `67ac5cf` 上复现失败（`HisAdapterChecks.cs` 中 plain AddMap 检查），本次也从 `166c6a9` 导出隔离源码复现。SQL/模板编辑专项通过不能替代这一综合套件的全量通过。该旧问题不在本轮修复范围。
