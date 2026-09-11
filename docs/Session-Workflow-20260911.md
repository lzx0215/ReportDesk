# Electron 会话报表及适配核查 · 2026-09-11

本轮已移除当前 Electron 界面的收藏、最近、分类及可编辑说明。选取 HIS / LIB 目录或 XML 后，查询定义只进入 Host 内存；关闭后清单清空，下次重新选来源。搜索、HIS 位置、适配提示、关联 XML 检查、查询、筛选、排序和导出保留，未改进度条样式。

不再因导入、演示、查询或重新检查写 catalog。连接设置仅在点击保存时写 `connection.json`，密码继续遵循用户勾选与 CurrentUser DPAPI。无新设置文件时仅兼容读取旧 catalog 的 Connection 成员；不恢复旧报表、不改写或删除旧文件。旧 WinForms 的 CatalogStore API 留作兼容回归，不是本轮交付界面。

## 原引擎证据与本轮修复

`ucCommonWindow.queryDataByControlType` 的 Sql、DepartmentType、Dictionary、EmployeeType 分支返回动态数据，只有默认分支使用传入的 DefaultDataSource；`ucTopQueryCondition.AddControls` 使用已加载的 ComboBoxType.DataSource，不拿残留的自定义列表作已选条件。本轮因此只对受支持 SQL/字典下拉框忽略其不生效的自定义列表。未知选项来源、文本上下文和禁用控件仍保持检查，SQL 与科室范围均未修改。

共 12 张去掉此项误判，其中 8 张还有其他原因，4 张新增为无静态阻塞：

- [中联]医保费用明细
- 住院患者特殊使用级抗菌药物使用量占比
- 外购药品来源
- 挂号情况统计表

原先笼统的“启用了 AddMap…映射待适配”已按实际规则拆分。AddMap 本身不再被误列为阻塞原因；每条提示明确源名称及 IsCross、CrossCombinColumns、SumRows、RowGroup 或树形选择规则。分类去掉不等于取消这些执行检查。

本机静态反编译证据位于 `artifacts/verification/his-materials/Implement.ucCommonWindow.cs`、`Implement.ucTopQueryCondition.cs`、`Implement.ucLeftQueryCondition.cs` 和 `Implement.ucMainFpReport.cs`。只分析文件，未执行 HIS 程序或连接生产数据库。

## 全量扫描

来源：`E:\sql\yljhis\LIB\LIB`。扫描 3687 份 XML；1352 份查询定义中，1284 张含查询 SQL 可独立显示。另 68 份无 SQL 定义单列；1570 份版式和 765 份其他配置排除出独立查询列表。原文件未修改。

| 检查结果 | 报表数 |
| --- | ---: |
| 无静态阻塞 | 949 |
| 部分数据源可试查 | 17 |
| 全部数据源仍阻塞 | 318 |

共有 335 张仍至少有一项待适配。以下数量允许重叠，不能相加：

| 原因 | 涉及报表 | 后续处理 |
| --- | ---: | --- |
| 行分组小计 | 128 | 实现分组键、小计位置、标签和数值类型；核对重复组及空值 |
| 树形多选 | 89 | 复现勾选/全部节点展开；每个编码单独绑定并适配 IN |
| SQL 全角括号 | 63 | 逐条核对行列与修正副本，不能替换字符串和标识符中的符号 |
| 交叉统计 | 39 | 复现行列键、指标、空值和汇总 |
| 指定行汇总 | 35 | 解析原行选择与汇总公式 |
| 数据源顺序/用途 | 33 | 实现 ConditionUsing 等前置查询及依赖传值 |
| 字典/选项 | 30 | 核对编码、范围与实际加载实现 |
| 自定义控件 | 19 | 按控件类核对取值和联动 |
| 组合列 | 19 | 解析计算公式、顺序及缺列行为 |
| 树形节点/全部 | 12 | “全部”可能展开多个编码，不能直接传 ALL |
| 数据源/控件属性依赖 | 11 | 区分值、名称及前置结果行列 |
| 禁用控件上下文 | 7 | 保留原固定或上下文来源，不能改成任意输入 |
| 默认值/上下文 | 5 | 确认初始化来源 |
| 分组查询 | 4 | 实现组查询与主查询传参 |
| 参数冲突 | 2 | 核对重复参数及引用关系 |

开发工作的优先级是行分组和树形多选；它们共享规则，适配按规则推进，无需用户逐张整理 XML。剩余工作尚未实现，不能声称全库已适配。需要内网验证的是运行依赖和结果口径，当前没有要求用户再补整套 XML。

逐张清单、源级提示、位置依据及建议见 `artifacts/audit/session-20260911/review.html`；可按名称、提示、规则和状态搜索。机器可读结果为同目录 `audit.json`、`summary.json`，不含查询结果或患者数据。

## 已执行验证

- `powershell -NoProfile -File scripts/build.ps1 -ScanDirectory 'E:\sql\yljhis\LIB\LIB\Config\Xml'`：x86/x64 各 98 项通过，WinForms 冒烟通过；含原门诊处方 XML 回归。该子目录 1276 张，全 LIB 额外 8 张由全库审计覆盖。
- `node tests/desktop/host-checks.cjs`：绑定、分页、完整导出、取消重试、DPAPI、只读离线边界、导入、显示清单及日志检查通过。
- `node tests/desktop/session-checks.cjs`：内存清单、重新读取 XML、退出重启清空、不写报表库、旧 catalog 不变、连接设置单独持久化及取消保存密码通过。
- `node tests/desktop/ui-checks.cjs`：真实 Electron 查询、筛选、排序、复制、导出、说明弹窗、连接设置、移除入口及原 10px 进度条通过，无 renderer 异常。
- `node tests/desktop/session-audit-checks.cjs`：审计页面搜索、规则过滤、949/17/335 数量及展开明细通过。
- 更新测试覆盖精确旧基线、损坏文件/正在运行拒绝、三文件替换、两次启动重新选择来源、演示查询、回退、重装和原文件保留。最终更新包的验收路径在本文件末尾记录。

首次 Core 回归失败是旧测试仍断言 AddMapColumn 为阻塞原因；已将其改为实际 IsCross，并增加 AddMap 不误报的断言后通过。检查中未隐藏该失败。

真实 Oracle 查询、HIS 同条件数据核对和内网 Win10 实机验收：**NOT RUN**。可试查不等于业务结果已验证。未部署、提交或推送。

## 交付

`artifacts/updates/ReportDesk-0.2.0-session-final-20260911.zip`，仅适用精确匹配上一轮 adaptation-recheck 更新后文件的 Electron 0.2.0 x64。按包内 README-Update.md 执行 Apply-Update.ps1，脚本核对实际文件哈希并自动备份，不能混用其他基线的文件。

ZIP SHA256：`356BFCF6E953A6E6A686B8580C97E1A5B66033A4A7573CCD70C6C8A86D4167BD`。

基线：`artifacts/verification/desktop/update-1789120819988/installation with spaces`，未覆盖该基线。更新仅含 app.asar、Host EXE、Core DLL；不替换运行时/驱动/用户 XML/显示配置。

最终包更新/回退验收通过：`artifacts/verification/desktop/update-1789123266609/PASS.txt`。审计页面验收：`artifacts/verification/desktop/audit-session-1789123181428/PASS.txt`。Host 会话验收：`artifacts/verification/desktop/session-1789122804281/PASS.txt`。实际业务界面验收：`artifacts/verification/desktop/ui-1789122806526/PASS.txt`。最终 `git diff --check` 无空白错误。
