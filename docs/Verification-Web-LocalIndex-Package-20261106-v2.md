# 本机索引 v2 更新包验收记录

日期：2026-11-06。以下为本地隔离验证，不代表现场 IIS、Oracle 或 HIS 验收。记录不打入 ZIP，避免循环哈希。

## 交付物与范围

- 包：`artifacts/packages/ReportDesk-Web-net462-LegacyCore-LocalIndex-AutoUpdate-20261106-v2.zip`
- ZIP SHA-256：`6CF0C0FE39AC062AC6FD7DAEE1FCC77F372DF53EBD40DE83A567731F7F6ABD55`
- 交付 Web DLL SHA-256：`482C768FB4811AAB7830434DDF2D7D28A0BFF64EB5788D1839448099ECCCB409`
- 必须保留的旧 Core SHA-256：`6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`
- 目标 `.NET Framework 4.6.2` / x64；增量部署 15 个文件，Web DLL 和 14 个 UI 文件。旧 Core、Oracle 依赖、配置、连接与业务 XML 不在部署载荷中。
- 构建脚本：`scripts/build-web-local-index-update-v2.ps1`；包内 `README.md` 来源：`docs/Web-LocalIndex-Update-20261106-v2.md`。
- 独立复核 `artifacts/verification/local-index-update-20261106-v2-adapters/package-review-results.json`：51 项通过，包含 ZIP 和载荷哈希、源输入和旧发布保护；结束后 54 个输入指纹与构建记录一致，见 `artifacts/verification/local-index-v2-final-preview/frozen-inputs.json`。

## 安装、回滚与精确旧 Core

| 内容 | 通过 | 本地证据 |
| --- | ---: | --- |
| 文件预检、更新、回滚、连接和配置保护 | 31 | `artifacts/verification/local-index-package-20261106-v2-review-files/results.json` |
| 维护失败与恢复 | 5 | `artifacts/verification/local-index-package-20261106-v2-review-maintenance/results.json` |
| 维护 503 和操作标记门禁 | 13 | `artifacts/verification/local-index-package-20261106-v2-review-offline/results.json` |
| 本机真实 HTTP 和超时 | 9 | `artifacts/verification/local-index-package-20261106-v2-review-http/results.json` |
| 空服务器 LIB、连接与配置保护 | 43 | `artifacts/verification/local-index-v2-suite-0.log` |
| 多连接与配置兼容 | 42 | `artifacts/verification/local-index-v2-suite-1.log` |
| 按需工作集和菜单位置 | 18 | `artifacts/verification/local-index-v2-suite-2.log` |
| 本机 XML 保存协议与生命周期 | 92 | `artifacts/verification/local-index-package-20261106-v2-legacy-runtime/LocalDirectory/checks.log` |
| HTTP、安全与任务保护 | 72 | `artifacts/verification/local-index-package-20261106-v2-legacy-runtime/Http/checks.log` |

安装器的 IIS 发现、维护响应和部分管理员权限边界使用替身。不是实际服务器安装或管理员 ACL 验收。回滚不撤销已写回用户电脑的 XML，旧版本依赖 LIB 时需先恢复原目录和权限。

## 规模与数据传输

实际样本为既有隔离副本 `artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml`，并非对当前服务器 LIB 的实测。只读读取 3210 个 XML、80,027,249 字节，将独立副本写入 Chrome OPFS，再运行成品 v2 Web 与精确旧 Core。没有连接 Oracle。计时不含测试准备，数值是当前机器一次测试，并非现场性能保证：

| 操作 | 实测 |
| --- | ---: |
| 首次本机索引 | 8.837 秒 |
| 刷新后缓存列表显示 / 后台核对完成 | 0.126 / 1.952 秒 |
| 初次扫描和刷新各自位置元数据请求 | 13 次 / 168,572 字节 HTTP 请求体 |
| 扫描与核对上传 XML | 0 份 |
| 打开一张报表 / 本地候选校验 | 3.349 / 0.174 秒 |
| 离线查询含本地校验 | 0.607 秒 |
| 打开一张上传 XML | 2 份，原始内容 33,560 字节，HTTP 请求体 45,076 字节 |
| 关闭后的服务器临时 XML | 0 份 |

位置请求仅含相对路径与哈希；一张报表的 XML 内容只在按需打开时上传。现有样本的 1276 个前端报表相对路径与精确旧 Core 的 1276 个可显示报表逐项相同；旧 Core 还标记 68 个非独立不完整报表，导入错误 0。源副本逐文件哈希前后不变。结果：`output/playwright/local-index-v2-final-scale-20261106/scale-results.json`、`catalog-difference.json`；测试 `artifacts/verification/local-index-update-20261106-v2-adapters/scale-e2e.cjs`。

## 浏览器保存与导出

最终包 UI 和 Web DLL 与旧 Core 在隔离 WebRuntime、Chrome 157 和 Edge 156 上运行。分别以两种浏览器作为主页面，检查本机 SQL/XML 连续保存和原文件备份、回读、外部修改阻止覆盖、浏览器之间隔离、会话失效草稿保留、关闭释放临时文件。合成 HIS 菜单位置可搜索，位置配置文件测试前后哈希相同。见 `output/playwright/local-index-final-v2-20261106/browser/browser-results.json`、`artifacts/verification/local-index-v2-final-preview/final-checks.json`；预览进程已停止。

Chrome 实际下载检查使用 12 个查询 XML 副本，识别 11 张报表；选择 2 张下载包含且仅包含这 2 张标题及 SQL，全部下载包含 11 张标题及 SQL。见 `output/playwright/local-index-v2-final-export-20261106/export-results.json`。

首次最终规模脚本曾因未等页面条件渲染就调用查询，导致测试代码空引用；等待条件修正后对 v2 重跑通过，不将该失败当作已通过的产品验证。

## 现场边界

尚未测试真实 IIS 安装、现场原生 Windows 目录选择器和权限提示、360 浏览器、真实 Oracle 查询结果、真实 HIS 配置及 DPAPI 应用池身份。每台客户端需可信 HTTPS、支持 File System Access 的浏览器和现有维护入口权限；授权可能在浏览器重启、站点数据清除或策略变更后需要重新授予。缓存显示后须等待后台核对完成才可打开。仅按旧报表 ID 配置的菜单位置要打开报表后才能显示；批量/全部导出受 256 MiB 工作集上限限制，超限需分批。先备份并保留旧 LIB，完成现场副本验收并确认其他应用不依赖后，再自行安排迁移或删除；本包不执行该操作。
