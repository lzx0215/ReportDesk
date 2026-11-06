# Web 报表来源入口合并验证（2026-11-06）

## 本次范围

将左侧文件夹入口统一为“报表来源”，用“打开本机目录 / 导入到服务器”两个页签说明保存位置。设置改为“连接与同步设置”，只保留数据库连接和 HIS 发布同步。本机目录区域、状态和恢复控件迁入来源窗口，三个编辑器的“本机保存状态”直接打开来源窗口。

切换页签只改变显示内容，不切换实际报表来源。本机 active/staging 状态下来源入口保持可用，服务器上传入口隐藏并显示切回说明；明确点击“切回服务器目录”后沿用原有草稿、待恢复保存及确认保护。本次不改变上传、目录保存/备份/恢复算法、连接档案持久化、数据库查询或权限规则。服务端只更新两条入口指引文字。

## 已执行验证

| 检查 | 结果与范围 |
| --- | --- |
| `node --check` | `web.js`、`local-directory.js` 及三份浏览器验证脚本语法通过 |
| `settings-checks.cjs` | PASS：设置两页签、来源两页签、键盘操作、连接草稿/多连接新增保存测试激活、来源操作不读写连接、文件/文件夹上传、普通查询入口边界 |
| 来源模式回归 | PASS：真实浏览器 OPFS 目录句柄；操作系统选择器和服务端协议替代；local active/staging 中入口可用、导入不可用、切页不切来源、取消/接受切回服务器 |
| 恢复入口回归 | PASS：SQL/XML/layout 三个编辑器的本机保存状态链接进入来源本机页；核对保存状态可调用 |
| `browser-checks.cjs` | PASS：既有 Web 页面、维护权限、虚拟目录、同步/冲突、TNS、连接密码清理及日志交互回归 |
| `local-directory-checks.cjs` | 39 PASS：备份、回读、部分写入失败、响应丢失、恢复、第三方冲突、跨页锁、权限丢失等；浏览器文件接口模拟，保留的真实 XML 样本不变 |
| `transport-checks.cjs` / `xml-transport-checks.cjs` | PASS：原协议、定义指纹、取消、维护边界、XML 保存目标确认和草稿保护 |
| Impeccable 静态检查 | 对 `web.js` / `web.css` 执行 detect，输出 `[]` |
| 页面截图 | 已查看桌面 1440px 和窄屏 390px 的本机/服务器页，来源窗口无控件重叠或水平溢出；保留现有配色。未扩展既有主页面手机适配 |

浏览器证据：`output/playwright/report-sources-20261106/summary.json` 与同目录八张截图。运行 Chromium 147.0.7727.15，无页面错误；不生成或查询真实业务结果行。

`local-directory-e2e.cjs` 已更新来源导航且语法通过，本次未重新运行完整生产 WebRuntime + OPFS 保存流程。上述模式和恢复入口检查不能代替真实 Windows 文件选择器、现场目录写回、360 浏览器或 HIS 引擎验收。

## 交付边界

新版包使用独立 `build-web-report-sources-update.ps1`、新的 release 和输出路径；上一版 Settings 更新包及其 builder 保留。沿用精确旧 Core 基线 `6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`，由 builder 重新编译 net462 Web DLL，仅交付 Web DLL 和现有 UI 白名单。

包构建、哈希、配置保留及更新/回滚检查结果另记在 `Verification-Web-ReportSources-Package-20261106.md`。本记录不代表已部署；未连接真实 Oracle、未操作服务器 IIS、未修改现场配置或数据库连接。
