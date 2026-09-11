# 报表分类、适配盘点与 HIS 位置验收

## 本轮交付

- 在 D:\系统知识库 全量只读扫描 3158 个 XML；1320 份查询定义，1256 份独立查询（357 可试查、899 待适配），64 份无报表 SQL 的定义单列；1531 份 FpSpread 版式和 307 份其他 XML 不独立显示。
- 原始提示按全文、大小写敏感去重 680 种，按根因归为 11 类，其中独立报表 10 类；按报表文件去重的分类计数可重叠。
- ImportSummary 增加临时不完整定义清单；catalog 持久化格式不变。旧库无 SQL 条目入口过滤但不删除，完整版本重导入后保留 ID、收藏、备注。
- Electron 主页面/报表说明显示所有已知 HIS 路径及来源，支持位置搜索；ReportLocations 独立外部 XML 支持 ID、哈希或明确标注未核对的文件名关联。位置配置不改变可见性规则。
- 原进度条保留 10px 圆角样式。本轮没有解除交叉映射/SQL 限制，没有修改报表 SQL、字典业务值或科室范围。

## 实际验证

|检查|结果与证据|
|---|---|
|x86 Core 检查|85 PASS，artifacts/verification/catalog-audit/checks-x86.txt|
|x64 Core 检查|85 PASS，artifacts/verification/catalog-audit/checks-x64.txt|
|WinForms 兼容烟测|x86/x64 PASS，artifacts/verification/0.1.4/{x86,x64}/ui/smoke.txt|
|后台协议回归|PASS，artifacts/verification/desktop/host-1789111212799/PASS.txt|
|Electron 全流程|PASS，artifacts/verification/desktop/ui-1789111564923/PASS.txt|
|内容分类、旧库、路径、建议、10px 进度条|PASS，artifacts/verification/desktop/catalog-1789111565005/PASS.txt|
|实际更新后 EXE 的相同功能|PASS，artifacts/verification/desktop/catalog-1789111633255/PASS.txt；测试路径均标注为合成数据|
|更新前置条件、替换、已更新识别、启动、回滚、再安装|PASS，artifacts/verification/desktop/update-1789111565036/PASS.txt|
|可搜索 HTML 清单|PASS，tests/desktop/audit-checks.cjs；1320 定义、899 待适配、556 映射、1902 未独立导入的总数及搜索验证|
|原查询文件哈希|1320 份与修改前一致，changed=0|
|diff 空白检查|git -c core.safecrlf=false diff --check PASS|

首次更新构建与 UI 验证并行时文件被测试 Host 锁定，构建失败；待测试退出后串行重建成功（0 警告、0 错误）。首次 UI 回归发现导入提示缺少“匹配文件不代表支持”的原有说明，已补回并通过回归。未隐藏首次失败。

## 更新文件

- artifacts/updates/ReportDesk-0.2.0-report-catalog-20260911.zip，85649 字节。
- ZIP SHA256：4C9CD2E0505A9E5D1102CDE9768382C134265B9F7B02352391B411EC06856251。
- payload 仅含 resources/app.asar、resources/host/ReportDesk.Host.exe、resources/host/ReportDesk.Core.dll。
- app.asar：7B5BAC2BD65960C266AF4E9CD1972C7AFC470155244B2E52FF4CFED5757860F3。
- Host：9C2B9609EE83C7892A39E4C7BF43940405BF51364160640E330E43958167BB22。
- Core：3C2706DF31CF7303CE755DABCD16632E20FD92A9C5AAE6E04E9ABC9F28B2347E。

自动 Apply-Update 校验的基线是前次 prescription-fix 包；人工三文件替换只适用于相同 Electron 44.3.0 / ReportDesk 0.2.0 x64 文件夹版，保持目录层级，先退出程序并备份对应三文件。更新不携带 HIS XML、查询结果或现场连接设置；不覆盖 catalog、report-visibility.xml、report-locations.xml。测试均针对 artifacts 下的隔离副本，没有部署内网或替换用户运行目录。

## 未完成的数据核验

- 没有实际 HIS 菜单记录及资源到 XML 的完整关联数据；只有表结构，不能恢复所有菜单位置。内置一条用户明确提供的路径，其余未知。
- 没有实际 Oracle/内网同条件结果比对；可试查不等于业务已验证。
- LISTAGG ON OVERFLOW TRUNCATE 的 1 份误拦截已分析记录，未扩展执行支持；其它适配类型提供处理路线，本轮未批量实现 HIS 交叉、分组、字典或上下文语义。
- Windows 10 实机、不同安装基线与实际业务数据：NOT RUN。
