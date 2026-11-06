# Settings 服务器更新包验收（2026-11-06）

## 交付物

- ZIP：`artifacts/packages/ReportDesk-Web-net462-LegacyCore-Settings-AutoUpdate-20261106-v1.zip`
- 大小：219,565 字节。
- ZIP SHA-256：`9B7CA2E87EF031CC1D7B1170CC7F49C96E9ABE3D97273EC05827219398AC6011`；同目录提供 `.zip.sha256`。
- 目标：`.NETFramework,Version=v4.6.2`，x64。
- 必须匹配的服务器 Core SHA-256：`6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`。
- Web DLL SHA-256：`095E56A20767A121551DAC6DA3F2607BB077E6C8EB3565F216C126F75BEB8C55`。
- 15 个 payload 文件：Web DLL + 14 个 UI 文件；ZIP 共 26 个文件，含固定哈希更新器、检查/更新/回滚启动器、说明、manifest 和 release 元数据。

新脚本 `scripts/build-web-settings-update.ps1` 对源码、UI、更新器与说明输入取指纹并在构建和打包结束核对；全新目录中针对精确旧 Core 编译。旧 LocalHis 源脚本和已有交付包未改。本包只在复制后的更新器中调整发布标识及 payload 哈希，保留 v2 维护确认、备份、替换和回滚实现。

## 本次实际验证

1. 全新旧 Core/net462 构建 0 警告、0 错误，原依赖 DLL 全部 SHA-256 不变；反射读取包内 Web DLL 目标框架通过。
2. 以与包内 DLL 完全同哈希的实际构建运行多连接专用检查：42/42 通过。日志 `artifacts/verification/settings-package-20261106-v1-profiles.log`。
3. 以包内 updater/helper 运行四套隔离检查：54/54 通过。分别为文件更新与回滚 27 项、维护流程 5 项、维护页门禁 13 项、loopback HTTP 9 项；结果目录为 `artifacts/verification/settings-package-20261106-review-{files,maintenance,offline,http}/results.json`。
4. 独立包结构、ZIP、manifest、逐项固定指纹及脚本正文检查：39 项通过。结果为 `artifacts/verification/settings-update-20261106-review-adapters/package-review-results.json`。
5. 确认只替换指定程序文件，更新/正常回滚后测试站点配置、连接配置、Core/其他依赖、HIS 测试目录及其他项目哨兵字节不变；ZIP 无真实 `Web.config`、连接配置、Core 或其他依赖替换。
6. 升级后已经保存新的双连接配置时，自动回滚拒绝覆盖并保留新配置。不能承诺修改配置后可无条件自动回滚；应保留最新配置与原备份并核对差异。

生成器语法预检曾发现清单表达式少一个闭合括号，修正后才开始构建；首次独立验收脚本有 PowerShell 5.1 数组封装问题，仅修正隔离验收脚本，最终通过。均未导致现场或历史包变更。

## 边界与使用

全部为本地离线验证；IIS 映射、管理员备份 ACL 成功初始化边界由替身提供，HTTP 另由真实本机 loopback 验证。未访问医院服务器、未调用真实 Oracle、未部署。真实 IIS 安装/回滚、原身份 DPAPI 解密及业务查询仍需现场验收。

操作顺序见 [安装说明](Web-Settings-Update-20261106.md)：解压到站点之外，以管理员先运行 `Check-WebUpdate.cmd`，通过后再运行 `Update-Web.cmd`。更新使本应用短暂重载，未保存到配置的进程内临时密码需重新输入。包内不覆盖现场配置，也不改变应用池身份。
