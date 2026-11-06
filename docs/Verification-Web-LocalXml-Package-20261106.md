# 默认本机 XML 更新包独立验收记录

日期：2026-11-06。发布：`LocalXml-20261106-v1`。本记录只对应下列冻结包，在本地隔离环境验证；未部署服务器、未连接 Oracle，不能代替现场验收。为避免循环哈希，本记录保留在仓库，不加入成品 ZIP。

## 交付物与指纹

- ZIP：`artifacts/packages/ReportDesk-Web-net462-LegacyCore-LocalXml-AutoUpdate-20261106-v1.zip`，233,829 字节。
- 解压目录：`artifacts/packages/ReportDesk-Web-net462-LegacyCore-LocalXml-AutoUpdate-20261106-v1`。
- 构建脚本：`scripts/build-web-local-xml-update.ps1`；安装说明：`docs/Web-LocalXml-Update-20261106.md`（包内 `README.md`）。
- 新构建：`artifacts/verification/local-xml-package-20261106-v1-bin`。
- 目标：`.NETFramework,Version=v4.6.2` / x64；构建 0 警告、0 错误。

| 对象 | SHA-256 |
| --- | --- |
| ZIP | `5A3B324AA3723B3B9907434A5B2B9B73468ED8DB6B79767A5380DA15B8A01BFB` |
| `payload/bin/ReportDesk.Web.dll` | `E3D550D50E054D3D01FD980B7C607BF2FEBC1171372C16E2F18DAF00AF2F6642` |
| `manifest.json` | `1083E030AF961E4DF9CFA1DD90A521051B4BE44CAFC7D83A991ABC1040890586` |
| `release.json` | `262090CFAAD13B6BC95FEF467895C903AE941BCEECD3B286A3FDEFE595D83D8B` |
| 必需旧 `ReportDesk.Core.dll` | `6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7` |

固定更新清单为 15 文件：Web DLL 与 14 个 UI 文件；ZIP 共 28 文件。包内没有 `Web.config`、数据库连接文件、运行数据、业务 XML、Core 替换文件或 PDB。构建前后与 ZIP 生成后核对全部编译及交付输入指纹；独立审查再次核对其与当前冻结源码相同。旧 Core 基线目录中的 12 个非 Web DLL 与新构建保持相同 SHA-256。

## 安装器与包结构检查

四套更新器检查全部通过，失败数均为 0：

| 检查 | 通过数 | 证据 |
| --- | ---: | --- |
| 文件更新、回滚、配置保护 | 31 | `artifacts/verification/local-xml-package-20261106-review-files/results.json` |
| 维护失败与恢复 | 5 | `artifacts/verification/local-xml-package-20261106-review-maintenance/results.json` |
| 连续 503 与操作标记门禁 | 13 | `artifacts/verification/local-xml-package-20261106-review-offline/results.json` |
| 本机真实 HTTP 读取与超时 | 9 | `artifacts/verification/local-xml-package-20261106-review-http/results.json` |
| 合计 | **58** | |
| 独立清单、ZIP 与冻结基线审查 | **51** | `artifacts/verification/local-xml-update-20261106-review-adapters/package-review-results.json` |

采用证据目录内测试副本，固定仓库路径并加载本包内安装器/helper；原有 `tests/web-update-*.ps1` 未改动。副本来源及哈希在同目录 `adapter-sources.json` 中。新 builder 只替换原 v2 安装器的发布标识和固定文件哈希表，操作主体、IIS helper 与三个启动器保持不变。

覆盖：精确清单更新与回退、包损坏及错 Core 拒绝、部分写入/健康失败恢复、连接或程序文件并发变化拒绝覆盖、断电日志恢复、未知维护页保护、路径隔离、相邻站点保持、禁止站点/服务/应用池操作。测试配置快照包括 `Web.config`、根配置、`Global.asax`、数据目录全部文件及 `SyncState` 哨兵；旧 Core、Oracle 等依赖及现有多连接字节均保持不变。实际 Settings v1 文件升级到本包后可按字节回退；错误发布的 journal 被拒绝。

新增缺失 LIB 情形：在健康响应替身成立时，旧 `ReportsDirectory` 指向不存在目录也可 Check、更新和文件回滚，且不创建该目录；旧应用健康失败时仍在预检阶段拒绝，未生成维护页、备份或修改配置。这只验证安装器门禁和文件行为，**不表示依赖 LIB 的旧版运行时能在缺少目录时健康启动**。应先保留旧 LIB 完成升级，验证新版默认模式后再自行安排移动；回退旧版前先恢复原 LIB 与权限。安装器不备份或恢复 LIB 内容。

IIS 发现、健康响应与管理员身份边界使用替身。当前非提权环境未实际验收新建备份目录的管理员 ACL；初始化与回滚 ACL 信任成功路径仅作替身验证，普通可写目录被真实信任校验拒绝。HTTP 套件仅访问本机 loopback 测试监听，不访问医院 IIS。

## 精确旧 Core 的运行验证

以下均实际载入最终交付 Web DLL 和上述精确旧 Core；不是以现代 Core 成功替代。正式构建目录在运行前后逐文件核对不变。

| 检查 | 通过数 | 证据 |
| --- | ---: | --- |
| 默认无 LIB、显式服务器兼容、连接与配置保护 | 43 | `artifacts/verification/local-xml-package-20261106-v1-no-server-reports.log` |
| 多连接、DPAPI、过期写拒绝及旧契约 | 42 | `artifacts/verification/local-xml-package-20261106-v1-profiles.log` |
| 本机 XML 保存协议、隔离、生命周期与清理重试 | 87 | `artifacts/verification/local-xml-package-20261106-legacy-runtime/LocalDirectory/checks.log` |
| HTTP、安全与活动任务保护 | 72 | `artifacts/verification/local-xml-package-20261106-legacy-runtime/Http/checks.log` |
| 合计 | **244** | |

43/42 套件为反射测试，接收最终 bin 路径。87/72 套件从当前测试源码重新编译，直接引用最终 legacy Web/Core；在独立 testbin 中装配完整同哈希依赖，两次构建均 0 警告、0 错误。通用 HTTP 测试使用已有 `REPORTDESK_LEGACY_CORE` 编译条件排除仅现代 Core 支持的独立 DepartmentContext 入口，实际默认测试仍为 72 项；不声称该可选入口通过旧 Core 验证。编译与运行脚本为证据目录下 `run-legacy-runtime-checks.ps1`，每套 `provenance.json` 记录测试源码和实际 DLL 哈希。

87 项包括：XML/SQL/配套版式两阶段准备与确认、原字节哈希和 UTF-16 保持、不同页面隔离、配置不变、按 tab 过期、`localExpected` 410 防止回落到其他来源、未确认保存保护、活动任务/关闭延迟释放、后台清理、锁文件释放后的重试、旧 worker 归属标记保存、活动 worker 与未知目录保护、连续上传完成状态的竞争回归。使用保留的真实 HIS XML 副本，源文件只读且结束哈希不变；不执行真实 Oracle SQL。

## 最终交付二进制的浏览器复验

在新的隔离站点与数据目录中，使用上表同哈希 Web/旧 Core、独立 HTTP harness 和成品包的完整 UI，Chrome 157.0.8103.38 与 Edge 156.0.4314.58 双向 OPFS 浏览器检查均通过。实际核对 SQL/XML 保存与备份、外部修改冲突不覆盖、浏览器隔离、服务端释放后 410 保留两类编辑草稿、失效编辑器禁用保存并保留复制/关闭，以及关闭后临时 XML 为 0。两浏览器无页面错误，原始样本字节未变。

证据：`output/playwright/local-default-legacy-20261106/browser-results.json` 与 `final-verification.json`。结束后预览进程已停止、18765 端口无监听；正式 bin、源码及成品 ZIP 未改。这里使用浏览器真实 OPFS 目录句柄及离线 WebRuntime，未测试原生 Windows 目录选择器、授权提示或 360 浏览器，未进行 IIS/Oracle 验收。

## 旧发布保持与未验收项

独立 51 项检查确认 Settings v1、ReportSources v1 的 ZIP、manifest、完整包目录、builder 和专属 README 均保持原字节；原 LocalHis updater/helper/builder/README 和原测试源也未改动。

- Settings v1 ZIP：`9B7CA2E87EF031CC1D7B1170CC7F49C96E9ABE3D97273EC05827219398AC6011`。
- ReportSources v1 ZIP：`19043D469DBC41CDE45FFACADC582F60DC50C252B2560B77A7520EE8F1556FCB`。

实际 IIS 安装、应用身份下 DPAPI 解密、Oracle 权限与结果、现场原生 Windows 目录选择和写回权限均 **NOT RUN**。功能和浏览器证据见 `Verification-Web-LocalXml-20261106.md`；本记录不将 OPFS 浏览器副本检查描述为现场原生磁盘/IIS 验收。
