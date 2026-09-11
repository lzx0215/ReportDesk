# HIS 资料适配验证（2026-09-11）

## 交付物及完整性

- 更新包：`artifacts/updates/ReportDesk-0.2.0-his-adaptation-20260911.zip`，141353 字节。
- SHA256：`9FF4AC26EDB82BD6E23EF249B7E5EF2E5C65BA3406AB3C8D5E7F9BBAF64EBEA4`。
- 运行文件仅三个：`resources/app.asar`、`resources/host/ReportDesk.Host.exe`、`resources/host/ReportDesk.Core.dll`。ZIP 内每个文件的 SHA256 均与 manifest 和最终隔离安装目录一致。
- 包内附替换说明、更新脚本、适配说明及四份菜单配置的只读导出 SQL。未部署到内网，未复制或运行 HIS DLL。

## 已执行验证

| 验证 | 结果和证据 |
|---|---|
| `powershell -NoProfile -File scripts/build.ps1 -ScanDirectory 'E:\sql\yljhis\LIB\LIB\Config\Xml'` | x86、x64 各 93 项检查通过；两种构建的 WinForms 启动检查通过。日志：`artifacts/verification/his-materials/build-all.txt`、`artifacts/verification/0.1.4/{x86,x64}/checks.txt` |
| 原引擎对应的合计行为 | decimal 精度、整数/日期/文本不合计、空值、空结果、关闭合计、标签列缺失、取消及类型异常检查通过；见 `HisAdapterChecks.cs` 和上述构建日志 |
| Host 回归 | 全量导出、分页、类型、取消重试、DPAPI、离线边界、导入、显示配置和日志隐私通过；`artifacts/verification/desktop/host-1789117670517/PASS.txt` |
| 隔离目录实际更新 | 基线错误、损坏文件、运行中拒绝，三文件安装、重复安装、回退、重装和用户文件保留通过；`artifacts/verification/desktop/update-1789117670762/PASS.txt` |
| 最终更新后的 Electron 程序 | 旧 catalog 读取与重新导入、收藏备注保留、真实 LIB 导入、两张处方报表条件界面、静态选项、布尔值、停用菜单候选、参数拒绝及原进度条检查通过；`artifacts/verification/desktop/his-1789117765669/PASS.txt` |
| 既有 Electron 和目录回归 | `artifacts/verification/desktop/ui-1789117259096/PASS.txt`、`artifacts/verification/desktop/catalog-1789117251469/PASS.txt`；后续 Core 合计修改另经最终构建与打包程序检查 |
| 可交互审计清单 | 摘要、更新前后状态、搜索、待适配和排除过滤检查通过，无 JavaScript 错误；`artifacts/audit/his-adaptation-20260911/PASS.txt` |
| 差异检查 | `git diff --check` 通过。保留已有未提交工作，未 commit、push 或部署 |

## 同一批输入的结果

全 LIB 有 3687 个 XML；1284 张独立报表由 363 张可试查、921 张待适配，变为 944 张可试查、340 张待适配。602 张解除拦截，21 张新识别为旧版漏拦截的问题，净增加 581 张可试查。1352 份查询定义的源文件哈希逐一比较未变。上述构建命令仅扫描 Config/Xml 子树，计数少于全 LIB，不能混用。

排除 1570 个版式、765 个其他 XML 和 68 个没有查询 SQL 的定义，共 2403 个文件。666 张报表有同名通用菜单候选，共 845 条候选路径；519 张有分类显示（包含候选分类）。候选并非已核实的 XML 绑定，剩余未分类未宣称解决。

## 未执行及限制

- 真实 Oracle 查询与 HIS 结果对账：**NOT RUN**。本次未连接真实数据库；可试查不等于现场查询或业务结果已经通过。
- Windows 10 x86/x64 现场安装验收：**NOT RUN**。本机 x86/x64 构建和启动检查不替代现场验收。本次小更新包针对现有 Electron 0.2.0 x64 目录。
- 340 张报表仍有真实转换、复杂控件或上下文等缺口，详见可搜索审计清单；导入菜单配置不会自动解决这些引擎功能。
- 两份 Excel 缺少明确菜单到 XML 绑定、窗口内部子菜单和组名称，完整位置仍待四份配置表导出；见 `Export-HIS-ReportLocations.sql`。
- 更新前关闭程序，备份三个运行文件及 `%LOCALAPPDATA%\ReportDesk\catalog.json`。更新后重新导入 HIS 根目录刷新适配判断。更新脚本只备份运行文件；回退旧程序后需恢复旧 catalog，或使用旧程序重新导入后再查询，避免沿用新版本的适配状态。
