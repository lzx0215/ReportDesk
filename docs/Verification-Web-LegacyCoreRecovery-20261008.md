# 现场旧 Core 的 XML 恢复兼容补丁

日期：2026-10-08。

现场替换 Web DLL 后，刷新页面能加载报表列表，但自动选择第一张报表失败。现场日志记录 `WebQuery`、`System.MissingMethodException`，调用栈位于 `Service.Details` 的 LINQ 排序键函数。现场 Core SHA256 为 `6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7`，Web 为 `522BCF1E6C333CE27EF5611984DCFA7C6CF4A1A2FFAAD9112C44A25D62F7ACF3`。

前一次补丁针对较新的 department-options 基线，其 Core 为 `EB9BA5ED04F62EA599441FC6E182B13510F880DA5E525A520F137B2CDF17B8A0`。交付前未核对现场实际 Core，是补丁适用范围判断不足；单 DLL 补丁不能默认跨 Core 基线使用。

在保留的旧包中找到与现场逐字节一致的 Core；反射确认该版本没有 `ParameterOptions.IsCurrentDepartment`。最新 Web 的详情参数排序调用该方法。用现场同哈希 Core 和上次 Web 补丁加载真实 LIB 副本，复现完全相同的 MissingMethodException，排除了把本次刷新报错直接归因于 XML 修改权限的判断。

## 修正范围

- 新增显式旧 Core 构建配置 `ReportDeskLegacyCoreDirectory`。仅该配置使用旧的参数元数据投影，保留旧 Core/UI 条件行为，不调用不存在的方法。
- 编译直接引用已核对哈希的旧 Core 及其原依赖，不重新编译或交付 Core；错误 Core 在编译前拒绝。
- 默认构建继续使用现有项目引用及当前科室选择功能，用户原有未提交改动保留。
- 新 Web DLL 保留本次 XML 事务修复：验证全部备份后，原文件哈希已一致则跳过回写；变化文件仍实际恢复，失败仍保留标记。恢复异常脱敏日志保留。
- 补丁只替换 `bin/ReportDesk.Web.dll`。不修改现场 Web.config、UI、Core、第三方依赖、权限、SQL 或数据库。

## 验证结果

- 精确版本混用复现：5 项二进制检查通过，首张报表选择产生 MissingMethodException；日志与现场一致。
- 修正 DLL：9 项二进制检查通过，启动自动恢复原文件未变且只读的旧事务，打开全部 1,284 张本地真实报表的详情和定义。
- 直接调用实际交付 DLL 的 SQL 编辑保存：只读原文件拒绝保存，原字节保留，pending.xml 清理，后续选报表可用；可写副本保存成功、重新加载、清理事务；失败和成功保存后的重启可用。
- 上述二进制检查无项目引用，运行时加载指定 bin 中的 Web/Core，确认 Core 为现场精确哈希。全部原知识库 XML 来源哈希保持不变；不执行 SQL、不连接 Oracle。
- 同一旧 Core 下事务/同步检查 24 组通过，包括真实 NTFS 替换拒绝、进程退出恢复、变更文件恢复失败仍阻塞、损坏备份拒绝、冲突和同步回归。
- 同一交付 Web/Core 二进制组合下 HTTP/安全状态检查 58 项通过。
- 兼容 Web、二进制测试、事务检查、HTTP 检查构建均 0 警告/0 错误；默认 net462 构建也通过。默认当前科室检查 27 项通过，来源字节未变，未执行 Oracle。
- 兼容构建逐 DLL 哈希检查确认 Core 和全部 11 个第三方依赖均未变化。错误 Core 构建被正确拒绝。
- 完整旧网站的全新隔离副本先模拟现场错误组合，再只替换兼容 Web DLL；13 个基线 DLL 检查分别接受错误组合的已知输入和修正组合，错误 Core、缺失 DLL 均被拒绝。逐文件比较只有 Web DLL 变化，UI、配置、Core 和其他文件不变。
- 隔离网站使用本机 ASP.NET 4.8.9221 预编译通过；这不是现场 Windows Server/.NET 4.6.2 的执行验收。
- 中间验证：HTTP 检查最初包含依赖新 Core 的当前科室专用检查，编译产生 4 个缺少方法错误；为显式旧 Core 配置隔离该专用入口后，通用 58 项检查通过。该检查不适用于旧 Core，不能声称在旧 Core 上通过当前科室升级验收。
- 默认当前科室检查初次传入外层 LIB，返回 DirectoryNotFoundException；修正为真实内层 LIB 路径后通过。未修改来源文件。

实际交付 Web SHA256：`6AE6C9F4D6769F09F606C5CC6863C0B829F5D97E7886F859F0F3957340930BC9`。

构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-web-legacy-core.ps1 -BaselineBin 'D:\aiproject\ReportDesk\artifacts\web\ReportDesk-Web-net462-search-fix-20260920\bin' -OutputDirectory 'D:\aiproject\ReportDesk\artifacts\verification\legacy-core-recovery-new-build'
```

二进制回归需提供全新证据目录：

```powershell
dotnet build tests/ReportDesk.Web.Binary.Checks/ReportDesk.Web.Binary.Checks.csproj -c Release -p:ReportDeskWebTargetFramework=net462 --force
& tests/ReportDesk.Web.Binary.Checks/bin/Release/net462/ReportDesk.Web.Binary.Checks.exe 'D:\aiproject\ReportDesk\artifacts\verification\legacy-core-recovery-20261008\build-net462' 'D:\系统知识库\00_Inbox\yljhis\LIB\LIB' 'D:\aiproject\ReportDesk\artifacts\verification\legacy-core-recovery-rerun'
```

证据目录：`artifacts/verification/legacy-core-recovery-20261008`。现场列表为 1,292 张，本地 LIB 为 1,284 张；本地覆盖不能替代现场定义差异和业务验收。

## 现场验收与回退边界

安装前运行新包内只读基线检查，必须 13 个 DLL 匹配；已知旧版本、上次错误组合或本次新 Web 哈希才接受，不接受任意 Web DLL。安装按 README 停止 ReportWeb 池、备份、只换 Web DLL、启动并 Ctrl+F5。启动可能恢复真实已变化文件，因此相关 XML 和完整 SyncState 仍需备份。无需手动删除 pending.xml。

回退使用最初替换前与旧 Core 匹配的 Web DLL；不能将 `522BCF...` 当作可用回退，因为它正是本次不兼容组合。恢复 XML/事务状态是单独的数据恢复操作，DLL 回退不能自动撤销它。

本次没有访问服务器或执行部署；现场 IIS、现场 1,292 张定义、真实 Oracle 查询、原 XML 写入权限和业务结果验收 **NOT RUN**。保存仍需要适当修改权限；本补丁不授予权限，也不将失败保存显示为成功。
