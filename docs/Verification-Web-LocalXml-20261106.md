# 默认本机 XML 与临时工作区验证（2026-11-06）

本次范围：默认由使用者选择自己电脑上的 XML 目录，查询仍由服务器使用原连接执行，SQL/XML 保存回本机并先备份；解除对服务器 LIB 的默认依赖，完善临时工作区释放。仅本地代码、隔离副本和更新包验证，未部署服务器、未连接真实 Oracle、未删除现场 LIB。

## 实现与兼容边界

- `ReportDesk.ServerReportsEnabled` 未配置时为 `false`。此时不规范化、扫描或创建旧 `ReportsDirectory`，不初始化服务器同步，也不创建空的替代报表目录。旧配置文件原样保留；连接配置照常加载。
- 显式 `true` 保留旧服务器报表、导入、同步及路径检查分支；默认界面只显示本机报表来源与数据库连接设置。
- 继续使用现有维护 IP 白名单、CSRF、同源检查和 HTTPS 要求。本机目录句柄需浏览器授权；不会自动读取其他电脑或越权扫描本机磁盘。
- 每个页面独立临时目录和报表上下文。`localExpected` 防止已过期的本机操作落回服务器目录。只在明确重新加载目录时取得新会话，不自动重试 SQL、上传或保存。
- 每页按既有 `IdleMinutes` 独立空闲回收；运行中的任务不回收。关闭页面尽力释放；本机写回过程续期，未决保存拒绝立即释放。工作区失效保留浏览器草稿和恢复记录，先核对再重新加载。
- 清理只删除有归属标记的已退出 worker 及本应用已释放的临时工作区。锁定文件失败留待重试；标记在内容清理成功后才删除，末步失败保留重试依据。未知目录及活跃 worker 保留。
- 不更改查询 SQL 语义、查询时间或行数限制，不改变密码 DPAPI 保存方式和共享当前连接的行为。

## 后端验证

当前源码以 .NET Framework 4.6.2 编译，0 警告、0 错误。以下检查均为隔离环境；真实业务查询结果不在此证据范围。

| 检查 | 结果 | 主要覆盖 |
| --- | --- | --- |
| `ReportDesk.Web.NoServerReports.Checks` | 43 项通过 | 缺失/无效旧 LIB 路径、配置字节保持、无 Reports/SyncState 创建、多个本机上下文及显式服务器兼容模式 |
| `ReportDesk.Web.ConnectionProfiles.Checks` | 42 项通过 | 旧单连接读取、多连接保存和切换、密码不返回、配置并发保护 |
| `ReportDesk.Web.LocalDirectory.Checks` | 87 项通过 | 实际 XML 隔离副本、两阶段保存、SQL/XML 编码和关联模板、按页回收、任务/未决保存保护、占用重试和 worker 释放 |
| `ReportDesk.Web.Checks` | 72 项通过 | HTTP、安全边界、会话和任务隔离、CSRF、活动任务保护及生命周期接口 |

生命周期新增检查通过反射调整空闲时间，实际执行 `WebRuntime` 和文件删除，不等待 60 分钟。单执行槽下连续 1 KiB 分块上传不吞掉或重试 409/429；完成状态只在页面和执行槽释放之后可见。排队活动任务在关闭 runtime 时保留 owner/lease，任务观察取消并结束后再释放。

本机目录检查使用保留的真实 XML 副本，仅增加注释等测试改动；源文件哈希未变化。服务端确认测试模拟浏览器回执，不把它表述为 Windows 原生文件写回验证。

证据：

- `artifacts/verification/local-directory-20261106-161758-239679/PASS.txt`
- `artifacts/verification/no-server-reports-20261106-161825-99ea16/PASS.txt`
- 编译输出：`artifacts/verification/local-only-20261106-lifecycle-final2`、`artifacts/verification/local-only-20261106-http-final`

## 浏览器及协议验证

- `lifecycle-transport-checks.cjs`：22 项通过；覆盖节流活动、无空闲心跳、写回续期、关闭尽力释放、410/401 保留草稿及恢复记录、仅显式加载刷新会话、403 仍禁用越权操作。
- 既有 `transport-checks.cjs`、`xml-transport-checks.cjs` 通过；请求不自动重放，定义版本、取消、权限及编辑会话行为保留。
- `local-directory-checks.cjs`：56 项通过；含失效后禁止旧查询/保存、保留句柄和 pending、仅本机核对恢复、显式重新扫描和关闭目录。
- 设置与本机默认模式浏览器检查通过：默认隐藏服务器导入/同步/发布，空态及来源/连接弹窗在桌面和窄屏可读；显式服务器兼容模式仍可用。
- 真实离线 `WebRuntime` 上以 Chrome、Edge 两个隔离浏览器使用各自 OPFS 目录：加载相同文件名的不同副本、查询按离线配置拒绝、SQL/XML 写回并生成两批备份、外部变更阻止覆盖、关闭一个页面来源只删除其临时 XML，关闭两个来源后临时 XML 数量为 0。本机源文件、备份及已选句柄保留。
- 在实际服务器释放工作区后收到 HTTP 410，SQL/XML 草稿仍保留，旧保存按钮禁用，复制、关闭和恢复入口可用。未修改共享 Desktop SQL 编辑器。
- `header-preview-checks.cjs` 76 项、`browser-checks.cjs`、`xml-editor-checks.cjs`、JS 语法检查及 `git diff --check` 均通过；界面检测无命中。

浏览器证据：`output/playwright/local-default-20261106/local-default-summary.json` 和 `runtime/browser-results.json`。本地 Chrome 157.0.8103.38 / Edge 156.0.4314.58 双向执行；临时预览进程已停止，18765 端口无监听，遗留临时 XML 为 0。未验证 360 浏览器。

OPFS 是真实浏览器文件系统接口测试，替代了原生目录选择器；它不证明现场 Windows 目录选择、权限提示、受管浏览器策略或 HIS 排版已经验收。没有伪造 Oracle 查询结果作为业务正确性证据。

## 更新包与现场验收

使用新构建的 net462 Web DLL，配合固定旧 Core 基线；不把当前开发 Core 放入更新负载。安装器仅替换 Web DLL 和 14 个 UI 文件，保留原 Core、其他依赖、`Web.config`、`Global.asax`、DataDirectory/config 及已保存连接。最终旧 Core 运行、包哈希和安装/回滚检查记录于同次交付的 `Verification-Web-LocalXml-Package-20261106.md`，以实际产物为准。

现场仍需确认：可信 HTTPS 和浏览器目录权限、原应用池账号 DPAPI 解密、真实 Oracle 查询结果、实际 IIS 安装与回滚、HIS 使用修改后 XML 的效果。先保留原 LIB 完成更新和副本验收，再决定移走；回退依赖 LIB 的旧版本前恢复原目录。应用回滚不会撤销已保存到各电脑的 XML。
