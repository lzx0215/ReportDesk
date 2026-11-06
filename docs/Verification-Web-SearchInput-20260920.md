# 报表搜索输入中断修复

## 原因与行为

旧搜索事件在停顿 250 毫秒后调用 `task`，全局禁用输入控件，并通过 `filterReports` 自动切换报表或清空当前报表。浏览器禁用获得焦点的搜索框后，后续键入无法继续进入该输入框。Chromium 回归在旧代码上重现：以 400 毫秒间隔键入 `slow-search`，最终只得到 `s`。

搜索改为保留 250 毫秒防抖的本地列表筛选，不进入后台任务、不自动选择报表、不清空条件或结果。筛选后点击列表项目才切换报表。中文输入法组词期间不刷新列表，提交后再筛选。Web 目录版本轮询在搜索框获得焦点时推迟，离开搜索框后下次轮询恢复；版本请求返回后再次检查焦点，避免异步响应打断输入。

生产改动位于共用的 `src/ReportDesk.Desktop/ui/renderer.js` 和 Web 的 `src/ReportDesk.Web/UI/web.js`；Web 构建会将共用 renderer 复制为包内 `UI/renderer.js`。桌面目录检查同步改为显式点击搜索结果。

## 验证

- 在原代码上先复现慢速输入失败，再执行修复。
- Chromium 普通页、可维护普通页、维护页、无权限维护页、虚拟子目录检查；新增慢速输入、停顿超过一秒后在中间继续编辑及光标位置、清空重输、中文 composition 事件、搜索不发后台调用、不切换选择、搜索焦点期间不刷新目录等检查。
- 前端传输协议检查、JavaScript 语法检查。
- 自动化 composition 事件验证不等于逐一验收现场搜狗/微软输入法；现场 IIS、真实 Oracle、原生输入法和桌面端完整业务回归未执行。

复验命令（项目根目录）：

```powershell
node src/ReportDesk.Web/UI/verification/browser-checks.cjs
node src/ReportDesk.Web/UI/verification/transport-checks.cjs
node --check src/ReportDesk.Desktop/ui/renderer.js
node --check src/ReportDesk.Web/UI/web.js
```

## 已安装上一版时的前端替换

适用基线：`ReportDesk-Web-net462-lib-startup-fix-20260920`。备份网站的 `UI/renderer.js`、`UI/web.js`，用补丁包内同名文件替换，然后浏览器按 Ctrl+F5。让使用者先完成当前查询：刷新页面会丢失该页当前条件和结果。无需修改 Web.config、DLL、C:\LIB 或 C:\ReportDeskData，也无需停止其他 IIS 网站。替换前端两文件不自动更改配置或数据库。

回退：恢复备份的两个 JS 文件，再强制刷新。本次未自动部署；完整包同时包含此前 LIB 路径和启动修复。不同版本或基线不明时使用完整包，按 Web-QuickStart.md 的备份、配置迁入和本站应用池切换步骤更新。
