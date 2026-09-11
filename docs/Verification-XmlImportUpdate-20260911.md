# HIS 目录导入与离线小更新验收 · 2026-09-11

## 交付内容

- 选择 HIS 根目录或报表目录，递归按 XML 根节点识别 ReportQueryInfo 与 FarPoint Spread，导入查询定义并输出关联摘要。
- 明细使用 ReportInfo/DetailDirectory 的显式引用；主表“查询设置 → 报表设置”仅作为命名候选。缺失、同名冲突和超范围引用明确显示，不自动认定 HIS 等价结果。
- 报表说明新增“检查关联 XML”。文件仍从原路径读取，不复制原 HIS 文件、不改变 catalog 格式；重启后按精确 QueryFilePath 后缀推导扫描根，否则回到查询文件所在目录。
- 原 AddMap/IsCross 等适配保护保留，并显示具体启用项。原进度条尺寸、白色圆角样式及动画保留。
- 小更新只替换 resources/app.asar、resources/host/ReportDesk.Host.exe、resources/host/ReportDesk.Core.dll，核对精确基线及依赖哈希，自动备份、可回退。

## 最终产物

`artifacts/updates/ReportDesk-0.2.0-xml-import-20260911-113233.zip`

- 大小：77,580 字节。
- ZIP SHA256：`8899A7610ED907FB70BF3C03E023C856755E6291A92B291BB5B162469BE5CAD3`。
- 基线：`artifacts/desktop/ReportDesk-win32-x64`，Electron 44.3.0、ReportDesk 0.2.0 x64，旧完整文件夹未被覆盖。
- 新 app.asar SHA256：`77D48B016CD226215E2FD77B44C9210646B76715A0FFC0FDC3C50B4523307B52`。
- 所有前置文件及新旧三个文件哈希见随包 manifest.json。相同版本号不能替代哈希匹配。
- ZIP 包含三个有效载荷、manifest、Apply-Update.ps1、README-Update.md；已逐个读取 ZIP 内有效载荷并确认 SHA256 一致，不包含构建源码、HIS 文件、用户配置或真实数据。

## 已执行验证

1. `powershell -File scripts/build.ps1`：x86 / x64 各 69 项检查 PASS，两个 WinForms 启动烟测 PASS。证据 `artifacts/verification/0.1.4/{x86,x64}/checks.txt` 和 `ui/smoke.txt`。
2. Host Release 构建：0 警告、0 错误。`node tests/desktop/host-checks.cjs` PASS，含新增关联入口的岗位显示清单限制，证据 `artifacts/verification/desktop/host-1789097551924/PASS.txt`。
3. 真实 Electron UI 回归 PASS：模拟查询、筛选、排序、复制、导出、参数、折叠、说明、连接保存、待适配拦截及无 renderer error；证据 `artifacts/verification/desktop/ui-1789097488894/PASS.txt`。只使用离线模式和合成数据。
4. 最终更新包验证：`node tests/desktop/update-checks.cjs artifacts/updates/ReportDesk-0.2.0-xml-import-20260911-113233` PASS。证据 `artifacts/verification/desktop/update-1789097568011/PASS.txt` 及同目录截图。覆盖只检查、基线不符拒绝、有效载荷损坏拒绝、进程运行时拒绝、实际三文件替换、重复更新无变化、真实已更新 EXE 的 HIS 根目录导入/关联、关闭重启再次匹配、待适配查询禁用、演示查询、回退、再次安装；验证 catalog、岗位配置、用户自建文件、原 XML、运行时和驱动依赖保持不变。
5. 当前本地资料只读扫描：`artifacts/check-bin/x64/ReportDesk.Checks.exe artifacts/verification/desktop/xml-corpus-20260911 D:/系统知识库/00_Inbox/yljhis/reports`，1320 份查询定义、1834 份非查询 XML 跳过、0 读取错误；356 份静态可试查、964 份待适配。证据 `artifacts/verification/desktop/xml-corpus-20260911/import-scan.txt`。该统计不是 Oracle 执行成功率。
6. `git diff --check` PASS（仅 Git 的 LF/CRLF 提示，无空白错误）。原有浅色界面修改保留，无 commit/push/部署。

## 修正与未验证项

生成更新包时发现打包后的 package.json 不含 devDependencies，改为读取基线 version 文件核对运行时；Windows PowerShell 对无 BOM 脚本中的中文文件名存在解码问题，交付说明改用 ASCII 文件名；子进程环境未能加载 Get-FileHash，安装脚本改用 .NET SHA256 流式校验。修正后完整更新/回退测试通过。

真实内网电脑上实际旧文件是否匹配：NOT RUN，由安装脚本现场预检查。真实 Oracle、HIS 映射/交叉业务口径、Win10 x64 干净机、原截图报表的完整关联文件：NOT RUN。本次没有实现 FarPoint 映射执行引擎，不能把配套 XML 匹配当作解除待适配的依据。

使用与回退操作见 [OfflineUpdate.md](OfflineUpdate.md)。本次只构建并验证可交付文件，未替换内网程序。
