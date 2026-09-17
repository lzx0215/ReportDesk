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

## 已知回归边界

`tests/ReportDesk.Checks` 的旧 HIS 适配断言已在合并前 `67ac5cf` 上复现失败（`HisAdapterChecks.cs` 中 plain AddMap 检查）。SQL 编辑专项通过不能替代这一综合套件的全量通过。该旧问题不在本轮修复范围。
