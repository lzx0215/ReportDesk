# 门诊处方患者明细修复验收 · 2026-09-11

## 已确认问题与改动

1. 旧导入器对所有 AddMap 一概拦截。现有知识库这组查询/主模板/明细模板，经结构核对可按 ReportDesk 既有独立数据源表格模式展示原 SQL 结果。新增精确三文件哈希限定的适配，保留其他未审查的映射、SQL 与控件保护；不是通用 HIS 引擎实现。
2. 真实文件回归首次失败揭示明细中文参数未被识别：原 ASCII 正则漏掉 `&唯一号`、`&处方号`。修复后生成这两项必填条件，并转为 ASCII `:pN` 参数绑定；中文动态标识符仍拦截，缺值拒绝执行。
3. 旧 catalog 通过重新导入更新适配状态，保留原 ID、备注、收藏；无原 XML/SQL 修改，无新持久化字段。实际绑定参数与原日期格式保留。

原资料的具体证据、三个 SHA256 和适配边界见 [OutpatientPrescriptionAdapter.md](OutpatientPrescriptionAdapter.md)。

## 最终更新包

- 路径：`artifacts/updates/ReportDesk-0.2.0-prescription-fix-20260911.zip`
- 大小：79,004 字节。
- ZIP SHA256：`9C5D3315D52F5B08348C3DF95EDA068F106615DA1026DC6C220A4E2DD1AB4242`
- 基线：上次交付的 XML 导入更新，即 `ReportDesk-0.2.0-xml-import-20260911-113233`。制作时使用其已验证的完整隔离安装副本 `artifacts/verification/desktop/update-1789097568011/installation with spaces`，不覆盖上一版。
- 新 app.asar：`FBCC994546796080D0C1A22C221D5F0381E38994B181FE0078B7E3192674FB0C`
- 新 Host EXE：`E6EFE12C95533C5701954733A78B0B908C378A358AF9BAB6C22F5302D8EBB789`
- 新 Core DLL：`9278F72F735E6AD78EB7E914E6FB571368A3246700A06BAEBFB66D93F1955809`

仍只交付三个程序文件；逐个读取最终 ZIP 内有效载荷并验证哈希通过。没有打入 HIS XML、数据库结果或用户配置。

## 实际检查

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| x86 / x64 Core 及原 WinForms 回归 | 每架构 78 项 PASS，两个烟测 PASS | artifacts/verification/desktop/prescription-fix-20260911/ |
| 原始报表集重新导入 | 1320 查询定义、1834 非查询 XML 跳过、0 读取错误；可试查 357、待适配 963，仅新增本报表可试查 | 上述 core-x86.txt / core-x64.txt |
| Host Release 构建 | 0 警告、0 错误 | scripts/build-update.ps1 输出 |
| 既有 Host 接口回归 | PASS，含 SQL/参数隐私、取消、导出及岗位清单限制 | artifacts/verification/desktop/host-1789110089595/PASS.txt |
| 旧版复现与新后台、实际 Electron 开发界面 | PASS | artifacts/verification/desktop/prescription-1789110090562/PASS.txt |
| 更新后真实 EXE 的本报表操作 | PASS，主表三个条件、明细两个条件、按钮可用，均通过适配/参数检查后在缺连接密码处停止 | artifacts/verification/desktop/prescription-1789110195090/PASS.txt、main-ready.png、detail-ready.png |
| 小更新替换、启动、重启、回退、重复安装、错误文件/运行中拒绝 | PASS | artifacts/verification/desktop/update-1789110195083/PASS.txt |
| 既有完整 Electron UI 回归 | PASS，演示、筛选、排序、复制、导出、说明、配置、原不支持报表、无 renderer error | artifacts/verification/desktop/ui-1789110225179/PASS.txt |
| Diff 空白检查与最终 ZIP 三文件哈希 | PASS | git diff --check、ZIP 逐项检查输出 |

数据源接口测试逐项确认原 SQL 与旧版本返回的定义完全一致，主表参数为 dtBeginTime/dtEndTime/dtYYPE，明细参数为唯一号/处方号；缺值拒绝，备注和报表 ID 保留。默认演示用于 UI 回归，不替代真实处方结果。

## 测试中修正

- 中文参数问题在真实文件正向测试中发现并修复，未跳过失败断言。
- x86 构建后的增量 Host 构建曾产生程序集架构警告，小更新构建改为显式 x64、no-incremental，最终构建无警告。
- 增量升级测试复制了旧目录已有备份，最初误选最早备份，更新脚本正确拒绝了目标不符的回退。测试改为使用本次安装输出的备份路径后完整回退通过；未放宽脚本校验。

## 内网操作与未验证边界

关闭 ReportDesk，备份旧三个程序文件，把新包 payload 下 resources 合并到程序目录，成套替换。重新启动后**重新导入该报表所在目录**，且查询/主模板/明细模板保持同目录和已核对内容。出现“已适配此版本的表格查询”及主表三个条件，表示适配生效。

Oracle 实际执行、实际返回数据、HIS 业务口径对照、Win10 干净机及内网文件是否与已核对版本相同：NOT RUN。开发验证没有连接数据库。原截图“普通门诊处方记录”不是本次核对到的同一份定义，不承诺被同时修复。

未 commit、push、部署或修改知识库原资料。
