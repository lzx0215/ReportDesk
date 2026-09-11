# 本轮交付验证

更新包：`artifacts/updates/ReportDesk-0.2.0-adaptation-recheck-20260911.zip`。

升级基线是上轮已交付 HIS 适配版本，隔离副本位于 `artifacts/verification/desktop/update-1789117670762/installation with spaces`。不修改基线目录。最终更新安装到 `artifacts/verification/desktop/update-1789120819988/installation with spaces`，未部署内网。

| 实际执行 | 结果 | 证据 |
|---|---|---|
| x86/x64 构建及基本检查 | 各 90 项通过，WinForms 启动通过 | `artifacts/verification/recheck-build.txt` |
| x86/x64 真实 Config/Xml 样本追加检查 | 各 96 项通过 | `artifacts/verification/recheck-x86.txt`、`artifacts/verification/recheck-x64.txt` |
| Host 回归 | 通过 | `artifacts/verification/desktop/host-1789120654204/PASS.txt` |
| 新功能实际 Electron UI | 通过 | `artifacts/verification/desktop/recheck-1789120644429/PASS.txt` |
| 既有 Electron 全流程回归 | 通过 | `artifacts/verification/desktop/ui-1789120819992/PASS.txt` |
| 新包校验、安装、重复安装、回退、重装 | 通过 | `artifacts/verification/desktop/update-1789120819988/PASS.txt` |
| 更新后的实际 EXE 再验证新功能 | 通过 | `artifacts/verification/desktop/recheck-1789120857116/PASS.txt` |
| 全 LIB 离线审计 | 1284 张：945 全部通过、17 部分通过、322 全部阻塞 | `artifacts/audit/his-recheck-20260911/audit.json` |
| JavaScript 语法和 git diff 检查 | 通过 | `node --check`、`git diff --check` |

新功能检查覆盖：旧 catalog 保守拦截后通过按钮刷新、精确版本标点修正、原 XML 哈希不变、不同版本不自动放行、主/明细独立状态、共享交叉依赖整体拦截、缺失原文件提示且保留条目、收藏备注分类保留、原 10px 进度条。

审计脚本首次运行因 Windows PowerShell 对无 BOM 中文脚本的编码识别失败；补回 UTF-8 BOM 后重跑通过。原始 SQL 提示过于笼统的问题已由四类原因与行列测试覆盖。

现场 Oracle 查询与 HIS 结果对账、Windows 10 干净环境验收均为 **NOT RUN**。本机测试只证明导入、检查、界面和更新行为，不证明院内函数、数据或账号权限。
