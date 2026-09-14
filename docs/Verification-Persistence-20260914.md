# 报表来源路径与连接配置持久化 · 2026-09-14

当前 Electron 0.2.0 首次导入外部报表目录或 XML 后记住来源路径，后续启动自动读取；连接成功后自动保存配置。

## 使用

1. 首次导入报表目录或 XML，程序自动记住来源；后续启动从原路径读取。
2. 在连接设置中点击「连接并保存」。成功后自动保存配置；「保存密码」默认勾选并使用当前 Windows 用户 DPAPI 加密。取消勾选后密码仅在当前会话可用。
3. 「保存设置」仍可在不连接数据库时保存连接信息。

## 存储与失败处理

- `import-sources.json` 仅保存绝对来源路径及目录/文件类型，不保存报表定义、查询参数或结果。来源不可访问时提示并保留路径；配置损坏时提示且不覆盖原文件。
- `connection.json` 只保存连接设置及可选 DPAPI 密文。登录失败、取消或保存失败不会覆盖原配置；密码不回传渲染器、不写日志。
- 原 XML、旧 `catalog.json`、SQL 口径和数据库结构不因本功能修改。更新包仍按精确基线校验并支持备份/回退。

## 验证范围

- Host Release 构建：0 警告、0 错误。
- Host 持久化检查：覆盖自动保存、DPAPI 往返、重启复用、密码不回传、失败/取消不覆盖、取消保存跨重启保留、写入失败和离线保护。
- 来源目录检查：覆盖多来源、重复导入、重启、旧结果失效、来源暂时不可访问及恢复、显示清单过滤、损坏来源配置保留。
- Electron UI 检查：覆盖导入及重启恢复；连接界面使用隔离的成功响应，实际数据库登录不在离线检查范围内。

未执行：真实数据库登录、报表 SQL 执行、业务结果对照、Windows 10 现场验收。

## 复现命令

在仓库根目录运行：

```powershell
dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -o artifacts/host
dotnet build tests/ReportDesk.Host.Checks/ReportDesk.Host.Checks.csproj -c Release -o artifacts/host-checks
& artifacts/host-checks/ReportDesk.Host.Checks.exe
node tests/desktop/report-persistence-checks.cjs <查询 XML>
node tests/desktop/session-checks.cjs
```
