# 本机 HIS 目录验证记录 — 2026-11-06

## 结论与证据范围

已实现连接设置中的本机目录选择、原数据库连接保留、SQL/XML 备份写回及异常恢复。没有连接真实 Oracle、没有修改原 HIS 目录、没有部署 IIS 或修改 HTTPS 配置。

原知识库路径当前不可访问。本次使用项目已保留的真实 XML 副本作为只读输入：

`artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml/`

- `抗菌药物消耗情况查询设置.xml`
- `抗菌药物消耗情况报表设置.xml`

测试修改只发生在新建的测试目录及浏览器 OPFS 副本中；保留输入文件的原始 SHA-256 不变。

## 已执行

| 检查 | 结果 |
| --- | --- |
| net462 当前 Core 构建 | 0 警告、0 错误 |
| net462 旧 Core 精确基线构建 | 0 警告、0 错误；Core 与原依赖哈希不变 |
| WebRuntime / 实际后端集成 | 64 项通过 |
| 浏览器目录保存逻辑与故障注入 | 39 项通过；该套使用浏览器 API doubles |
| Chrome 157.0.8103.38 | 真实浏览器 + 生产 WebRuntime + 原生 OPFS 目录句柄流程通过 |
| Edge 156.0.4314.58 | 同上，通过 |
| 原前端回归 | transport、XML transport/editor、public/admin browser-checks、真实模板 header-preview 检查通过 |
| UI 静态检查 | impeccable detect 返回空问题列表 |
| diff 空白检查 | 通过；存在原工作区的 CRLF 转换提示 |

Chrome/Edge 流程覆盖：连接表单未保存输入保持不变、选目录并加载、SQL 注释写回、XML 注释写回、备份原字节校验、未改模板保持原样，以及外部文件变更后停止覆盖并保留草稿。完整流程分别在当前 Core 与旧 Core 兼容构建上运行。

**证据边界：**浏览器测试把目录选择器替换为返回浏览器 OPFS 中的副本目录；目录句柄、IndexedDB、Web Locks、哈希和写回为真实浏览器实现。没有自动操作 Windows 原生选目录与授权提示，因此不能据此声称现场目录授权或 360 已验收。

后端覆盖 HTTPS/真实 loopback、伪造 Host 拒绝、维护权限、CSRF、同源、跨会话和跨标签隔离、原始 UTF-16/BOM、两阶段保存、错误哈希拒绝、幂等确认、连续编辑 token、关联路径相对化、上传中断、worker lease 与临时目录保护。原服务器 XML 和数据库设置保持不变。

故障注入覆盖备份失败、IndexedDB 失败、部分多文件写入、全量恢复预检、损坏备份、第三方改动、确认响应丢失、刷新后恢复、跨标签锁和恢复记录冲突。没有用模拟行代替 Oracle 查询验证。

## 证据路径

- 后端最终结果：`artifacts/verification/local-directory-20261106-124337-61b8bf/PASS.txt`
- 旧 Core 构建：`artifacts/verification/local-directory-20261106-legacy-final/`
- 浏览器结果：`artifacts/verification/local-directory-20261106-browser/browser-results.json`
- 当前 Core 浏览器结果：同目录 `modern-browser-results.json`
- 连接设置截图：同目录 `connection-chrome.png`、`connection-msedge.png`
- 旧 Core 基线 SHA-256：`6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`

## 复跑命令

在仓库根目录 PowerShell 中运行；所有输出应放在新的 `artifacts` 目录，不指向原 HIS 或生产目录。

```powershell
dotnet build tests/ReportDesk.Web.LocalDirectory.Checks/ReportDesk.Web.LocalDirectory.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 -o artifacts/local-directory-checks
& artifacts/local-directory-checks/ReportDesk.Web.LocalDirectory.Checks.exe
node src/ReportDesk.Web/UI/verification/local-directory-checks.cjs
node src/ReportDesk.Web/UI/verification/transport-checks.cjs
node src/ReportDesk.Web/UI/verification/xml-transport-checks.cjs
```

浏览器完整流程需先通过 `tests/ReportDesk.Web.Checks` 的 `--serve` 模式启动离线站点，监听 `http://localhost:18765/`，站点 `UI` 来自 Web UI 加现有 Desktop 共享资源，报表路径必须为副本的绝对路径。然后运行：

```powershell
node src/ReportDesk.Web/UI/verification/local-directory-e2e.cjs
```

成功时输出 Chrome 和 Edge 的 `PASS`，并生成 `browser-results.json`；该脚本需要本机安装 Chrome、Edge 和仓库现有 Playwright 依赖。模板保存和列同步的真实 Oracle 字段识别仍需现场数据库验收。

## 尚未执行

- Windows 原生目录选择、拒绝授权、重新授权及系统 ACL/杀毒软件干预。
- 360 安全浏览器 15.0.2040.0（32 位、内核 114.0.5735.289）极速模式。
- 医院 HTTPS、IIS 多 worker/实际回收、多台物理电脑。
- 真实 Oracle 查询、列识别、HIS 显示与打印一致性。

正式使用前按 `Web-Local-His-Directory.md` 在每种实际浏览器中用目录副本验收。已有目录文件由外部程序并发写入时，浏览器检查不能提供跨应用事务锁；维护期间应避免同时编辑同一组文件。
