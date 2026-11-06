# Web 选中报表时预览标题与表头（2026-10-19）

用户批准范围：选中即显示标题和表头，不执行 SQL，其他逻辑保持不变，兼容服务器当前旧 Core。不提交、推送或部署。

## 实现边界

`Service.HeaderPreview.cs` 仅在 `REPORTDESK_WEB` 编译时启用。复用导入期间已记录的主表/明细模板关联，不在每次选择时重新扫描目录。读取前复查受控路径和目录链接；XML 禁止 DTD/外部实体并限制读取尺寸。

`select` 响应新增可选 `headerPreview`：`title`、`columns`（`ordinal`、`label`、`width`）、`message`。该字段经现有元数据脱敏后返回，不发送 SQL、服务器路径或数据源标签。没有可确定的列时返回空列和说明，不增加查询阻断项，不改变原目录、参数或结果契约。

目前支持单工作表、单数据区标记、静态单行列标题；允许标题跨多行合并；按模板隐藏状态省略隐藏列。动态交叉、复杂表头、公式和不唯一关联均保守降级。同名候选文件即使数据源标记一致，仍保留候选说明。

`renderer.js` 只增加选中后重绘、预览函数和未查询分支。预览表无结果行、排序和选中控件；查询、筛选、排序、导出与参数逻辑未改。`web.css` 增加局部样式。未返回新字段的旧端仍使用原空白提示。

## 版本与交付

- 本次修改前快照及初始工作区状态：`artifacts/verification/header-preview-20261019-101843/before`、`initial-status.txt`。保护既有未提交变更。
- 原服务器组合的留存副本：`artifacts/verification/legacy-core-recovery-20261008/isolated-legacy-site`，其 Core/Web 和已知 UI 哈希与本轮服务器截图相符。
- 服务器 Core SHA256：`6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`。
- 服务器原 Web SHA256：`6AE6C9F4D6769F09F606C5CC6863C0B829F5D97E7886F859F0F3957340930BC9`。
- 新 Web SHA256：`EA702007F58D334E32FCC077427534C08992CB49CF147B520862F0BFB7154993`。
- 补丁目录：`artifacts/patches/ReportDesk-Web-net462-LegacyCore-HeaderPreview-20261019`。仅包含 Web DLL、服务器原版上叠加预览功能的 `renderer.js`、新增样式后的 `web.css`。不交付新 Core 和当前本地 `query-form.js`，不带入尚未部署的科室选择器功能。
- `assemble-package.cjs` 比较本次前后快照，断言 renderer 只有三处功能增量，然后应用到服务器脚本基线；校验 CSS 的原始基线和 Core/Web 的精确哈希。

## 实际执行

| 验证 | 结果与证据 |
| --- | --- |
| 精确旧 Core 的 net462 Web 构建 | PASS，0 警告、0 错误；`build-net462-v2`，所有基线依赖哈希不变 |
| 实际 DLL + 真实留存 XML 的表头检查 | PASS 30 项；`real-template-checks-v3`；截图八列完全一致，并验证门诊胸痛 8 列、住院胸痛 11 列 |
| 错数据源标记、合并列标题、公式、数字错误、DTD、损坏和缺失模板 | 全部安全降级，仍可选中报表；只读模板可读取；原始及测试副本 XML 哈希恢复一致，无新增 XML 事务 |
| 真实 LIB 的完整旧 Core 二进制回归 | PASS 9 项汇总；1284 张报表逐张选择和读取定义成功；只读保存失败、可写保存、重启恢复回归通过；所有改写仅发生在 `full-lib-regression` 测试副本 |
| 原有 HTTP 状态/权限与传输回归 | PASS 58 项；另执行原有 `transport-checks.cjs` 通过；仅协议或离线场景 |
| 原有浏览器回归 | PASS 普通/维护/拒绝访问/虚拟目录、搜索/输入法、同步提示等既有检查 |
| 新功能浏览器检查 | 当前源码与实际交付旧 UI 各 PASS 76 项；1920/1280 宽度，普通入口及 `/desk/admin/`；真实模板元数据，拦截全部 HTTP |
| SQL 触发边界 | 浏览器选择/切换数据源无 `query` 或 `lookup` 请求；点击开始查询才出现一次 `query`；用空结果协议响应验证原结果列渲染，不制造患者结果行 |
| 更新包检查与隔离回退 | PASS，25 个已知运行文件；拒绝错误 UI/Core 哈希及三文件混合状态；其余站点文件逐字节一致；回退恢复原基线；见 `check-package.ps1` |
| Diff 与样式 | `git diff --check` PASS；对初始快照逐文件检查；已查看普通1920及维护1280窗口截图 |

本次真实报表来源为 `artifacts/verification/legacy-core-recovery-20261008/after-fix/reports` 的留存副本；原知识库位置当前不可用。此证据只证明对这些留存模板的读取能力，不能证明现场模板仍是同一版，也不能把 1284 张选择成功等同于全部模板都能预览。

## 复现命令

从项目根目录运行，输出和证据目录须为新目录：

```powershell
powershell -NoProfile -File scripts/build-web-legacy-core.ps1 -BaselineBin artifacts/web/ReportDesk-Web-net462-search-fix-20260920/bin -OutputDirectory artifacts/verification/header-preview-recheck/build
dotnet build tests/ReportDesk.Web.HeaderPreview.Checks/ReportDesk.Web.HeaderPreview.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 -o artifacts/verification/header-preview-recheck/checks
& artifacts/verification/header-preview-recheck/checks/ReportDesk.Web.HeaderPreview.Checks.exe artifacts/verification/header-preview-recheck/build artifacts/verification/legacy-core-recovery-20261008/after-fix/reports artifacts/verification/header-preview-recheck/templates
node src/ReportDesk.Web/UI/verification/header-preview-checks.cjs artifacts/verification/header-preview-recheck/templates/details.json artifacts/verification/header-preview-recheck/browser
```

真实 Oracle、HIS 对照、现场 IIS、现场 Windows 10 x86/x64：NOT RUN。补丁替换说明列出停池、备份、三文件替换、强制刷新及三文件回退，不自动执行任何现场操作。
