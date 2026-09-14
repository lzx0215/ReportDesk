# LIB 路径与连接配置持久化 · 2026-09-14

当前 Electron 0.2.0 的行为取代 2026-09-11 会话模式中「每次重新导入」和「测试成功不保存」的规则。

## 使用

1. 升级后首次导入 HIS / LIB 目录或 XML，程序自动记住来源；后续打开自动读取，不必再次选择。旧版本没有保存来源，因此升级后需要导入一次。
2. 在连接设置输入数据库信息，点击「连接并保存」。成功后自动保存配置；「保存密码」默认勾选，下次启动可直接使用。取消勾选后密码仅在当前会话可用。
3. 「保存设置」保留为不连接数据库的手动保存入口。需要替换密码时，取消「使用已保存或当前会话的密码」再输入。

## 存储与失败处理

- `%LOCALAPPDATA%\ReportDesk\import-sources.json` 仅保存绝对来源路径和目录/文件类型；支持多个来源，重复导入同一路径不重复记录。导入有可独立查询的报表后才记录；启动从原 XML 读取定义、适配状态及关联信息，继续应用 `report-visibility.xml`。
- 原路径不可访问时提示并保留路径，其他可用来源仍加载。恢复路径后重启即可；配置损坏时提示，不自动覆盖。该方案要求原 LIB/XML 持续可访问，不是报表定义缓存。
- `connection.json` 只保存连接设置及 CurrentUser DPAPI 密文；新增可选 `RememberPassword` 保存用户的勾选选择，旧文件缺少此项时默认勾选。登录失败/取消不保存；写入失败不替换原配置，并明确区分「连接成功但保存失败」。
- 密码不回传渲染器、不写日志。原 XML、旧 `catalog.json`、SQL 口径、查询参数和结果均不因本改动写入或修改。无需数据库备份，没有数据库变更或不可逆操作。
- 三文件增量包沿用精确基线校验、程序文件备份及回退；旧版本忽略新的来源文件。必要时先备份本机用户配置目录，再由用户恢复之前的配置备份。

## 验证

- Host Release 构建：0 警告、0 错误。
- `tests/ReportDesk.Host.Checks`：使用注入连接函数，无 Oracle 连接。覆盖成功自动保存、DPAPI 往返与重启复用、密码不回传、失败/取消不覆盖、取消保存跨重启保留、目标文件锁定导致保存失败、日志不含测试密码、离线模式在注入函数前拒绝连接。
- `tests/desktop/lib-corpus-checks.cjs`：真实 LIB 1,284 张报表、2,347 个数据源逐项检查；进程重启后列表完全一致，1,352 份 XML 哈希未改变。无静态阻塞不代表 HIS 结果等价。
- `tests/desktop/lib-persistence-checks.cjs`：真实 XML 的隔离副本，覆盖多来源、重复导入、重启、旧结果失效、来源暂时不可访问及恢复、显示清单过滤、损坏来源配置不覆盖。
- `tests/desktop/session-checks.cjs`：保留的离线合成回归，覆盖旧 catalog 连接兼容、手动保存、取消保存密码、演示/结果不持久化；不作为真实 LIB 或 Oracle 证据。
- `tests/desktop/lib-update-checks.cjs`：在旧包的隔离副本上验证精确基线、三文件升级、重复执行、真实 LIB 导入/详情、回退/重装与依赖哈希。
- `tests/desktop/lib-ui-checks.cjs`：实际打包 Electron 窗口验证导入及重启恢复。连接界面使用明确的离线成功响应，实际写入由 Host 的保存接口完成；生产连接成功后的自动保存分支另由上述 C# 注入测试覆盖。本机 TNS 自动发现隔离，未修改 Oracle 配置。

未执行：真实 Oracle 登录、报表 SQL 执行、HIS 结果对照、Win10 现场验收。界面开发运行文件缺失后改用隔离客户端包验证；首次打开连接窗口被本机 TNS 解析提示阻挡，随后在测试中隔离自动发现，相关失败保留在验证目录。

## 复现命令

在仓库根目录运行：

```powershell
dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -o artifacts/host
dotnet build tests/ReportDesk.Host.Checks/ReportDesk.Host.Checks.csproj -c Release -o artifacts/host-checks
& artifacts/host-checks/ReportDesk.Host.Checks.exe
node tests/desktop/lib-corpus-checks.cjs
node tests/desktop/lib-persistence-checks.cjs
node tests/desktop/session-checks.cjs
```

更新包：`artifacts/updates/ReportDesk-0.2.0-persistence-20260914.zip`，只适用 manifest 中的精确基线。验证结果在 `artifacts/verification/desktop` 对应的 `PASS.txt`、JSON 和截图中；原有客户端包未覆盖。

本次通过记录：`connection-persistence-e988975e28914f718075203cb663ca78`、`lib-corpus-1789353407207`、`lib-persistence-1789353407207`、`session-1789353725665`、`lib-update-1789353477436`、`lib-ui-1789353662322`。
