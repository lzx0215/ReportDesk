# ReportSources 服务器更新包验收（2026-11-06）

## 交付物

- 发布标识：`ReportSources-20261106-v1`。
- ZIP：`artifacts/packages/ReportDesk-Web-net462-LegacyCore-ReportSources-AutoUpdate-20261106-v1.zip`。
- 大小：222,817 字节。
- ZIP SHA-256：`19043D469DBC41CDE45FFACADC582F60DC50C252B2560B77A7520EE8F1556FCB`；同目录提供 `.zip.sha256`。
- 目标：`.NETFramework,Version=v4.6.2`，x64。
- 必须匹配的服务器 Core SHA-256：`6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`。
- Web DLL SHA-256：`1EB212160F6D9DD227199F746B3D8F30FDBC8185915AF4A5B7EB7F2A3B2FF775`。
- `manifest.json` SHA-256：`CF7197D634277C6C76BDCDA63C009E0B60F0E4FEAFA1CAE3355A69DC03149720`。
- 15 个 payload 文件：Web DLL + 14 个 UI 文件；ZIP 共 27 个文件，含固定哈希更新器、检查/更新/回滚启动器、说明、4 份功能文档、manifest 和 release 元数据。

独立脚本 `scripts/build-web-report-sources-update.ps1` 从 Settings builder 沿用构建和校验逻辑，改用新发布标识、全新构建与包目录以及独立说明。包内更新器仅变更发布标识和 15 项 payload 哈希；v2 安装操作正文、IIS/维护辅助器及三个启动器保持原逻辑。旧 Settings builder 和交付物不修改。

本次在仓库根目录执行 `powershell -NoProfile -File scripts/build-web-report-sources-update.ps1`。构建产物为 `artifacts/verification/report-sources-package-20261106-v1-bin`。脚本拒绝覆盖已有构建和包，后续发布须使用新的发布标识及输出目录。此验收记录在成品生成后单独归档，不加入 ZIP，避免包哈希循环引用。

## 实际完成的验证

| 检查 | 结果与证据 |
| --- | --- |
| 全新 net462 / 精确旧 Core 构建 | 0 警告、0 错误；基线目录内 12 个非 Web 依赖 DLL 均逐字节一致。日志 `artifacts/verification/report-sources-update-20261106-review-adapters/build.log` |
| 包内 DLL 与被测 DLL | SHA-256 相同，反射读取目标框架为 `.NETFramework,Version=v4.6.2` |
| 连接管理回归 | 42/42 通过；使用旧 Core 构建目录运行专用 EXE。日志 `artifacts/verification/report-sources-package-20261106-v1-profiles.log` |
| 包内更新器：文件更新与回滚 | 29/29 通过，含此前 27 项和本次新增 2 项版本链路回归。`artifacts/verification/report-sources-package-20261106-review-files/results.json` |
| 包内更新器：维护事务 | 5/5 通过。`artifacts/verification/report-sources-package-20261106-review-maintenance/results.json` |
| 包内辅助器：维护页门禁 | 13/13 通过。`artifacts/verification/report-sources-package-20261106-review-offline/results.json` |
| 包内辅助器：loopback HTTP | 9/9 通过。`artifacts/verification/report-sources-package-20261106-review-http/results.json` |
| 独立包结构、ZIP 和旧版本冻结检查 | 46 项通过。`artifacts/verification/report-sources-update-20261106-review-adapters/package-review-results.json` |

更新器四套检查合计 **56/56**。测试适配副本仅存放于 `artifacts/verification/report-sources-update-20261106-review-adapters`，加载成品包内的 updater/helper；仓库原始测试脚本和前次验收结果不改。原测试及适配副本指纹另存 `adapter-sources.json`。

本次确认的具体边界：

1. 安装、正常回滚后，测试站点的配置、连接文件、Core/其他依赖、HIS 测试目录及其他项目哨兵字节不变；只替换固定 payload。
2. 使用真实 Settings v1 payload 构造前版本站点，并预置离线双连接配置。安装 ReportSources 后 payload 全部匹配，配置字节不变；回滚后完整站点字节与升级前一致，双连接仍保留。
3. 新包拒绝使用发布标识为 Settings v1 的回滚日志，并保持站点及配置字节不变。各发布必须使用对应的包和备份记录。
4. 升级后再保存新的双连接配置时，自动回滚拒绝覆盖并保留新配置。不能承诺修改配置后可无条件自动回滚；应保留最新配置及原备份并核对差异。
5. `manifest.json` 覆盖所有包文件（自身单独校验）；ZIP 中每个条目均与对应 SHA-256 一致。无现场 `Web.config`、连接文件、运行时数据、Core/依赖替换或 PDB。
6. `release.json` 中 56 项源码、UI、工具和文档输入指纹与冻结输入相同；构建前后及 ZIP 完成后均进行稳定性检查。
7. Settings v1 仍为 26 个包文件，逐项匹配原 manifest；ZIP SHA-256 仍为 `9B7CA2E87EF031CC1D7B1170CC7F49C96E9ABE3D97273EC05827219398AC6011`。原 builder、安装说明、LocalHis 更新源脚本及原始测试均保持原字节。

## 验证边界与使用

全部为本地离线验证，未访问医院服务器、真实 Oracle 或生产 IIS，未部署。连接专用检查以测试连接器和隔离配置验证持久化、DPAPI、密码隔离、失败/取消、显式切换及并发防护，不代表现场数据库登录或业务查询通过。本次新成品验证使用精确旧 Core/net462 构建；不将此前现代 Core 的检查当作该包证据。

安装测试中的 IIS 发现、HTTP 健康边界及管理员备份 ACL 成功初始化由替身提供；HTTP 辅助器另经真实本机 loopback 检查。真实非提权目录被备份信任校验拒绝的路径有实际覆盖；真实服务器管理员 ACL 成功路径、IIS 维护及现场更新/回滚仍待验收。

前端交互、截图和原生目录权限的验证范围见 [功能验证](Verification-Web-ReportSources-20261106.md)。安装顺序见 [本包说明](Web-ReportSources-Update-20261106.md)：解压到站点外，以管理员先运行 `Check-WebUpdate.cmd`，通过后再运行 `Update-Web.cmd`。更新会让本应用短暂重载，进程内临时密码需重新输入；现场已保存密码能否解密仍取决于保持原应用池身份和用户配置文件。
