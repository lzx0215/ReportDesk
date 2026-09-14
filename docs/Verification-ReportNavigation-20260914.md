# 报表位置与新增报表检查

## 实现

- 主界面新增“报表位置”弹窗，将原状态/来源路径和 HIS 位置信息集中展示，保留未知、候选、来源依据及停用标记。
- 主界面移除报表用途说明和离线演示入口，左侧操作集中在“全部报表”下方；实际查询阻塞提示保留。
- “检查新增报表并添加”从已记住的来源发现新报表，按 ID 去重，已有定义、条件和结果保留。目录来源递归扫描，单文件来源不扩展到同目录其他文件；新增条目仍遵守显示清单。取消不提交新增，路径失效保留来源和已有条目。

## 验证

- `dotnet build src/ReportDesk.Host/ReportDesk.Host.csproj -c Release -p:PlatformTarget=x64 -o artifacts/host --verbosity minimal`：通过，0 警告、0 错误。
- `node tests/desktop/lib-new-reports-checks.cjs`：通过。使用只读真实 LIB 和隔离副本，覆盖嵌套目录、重叠来源、重复检查、已有定义保留、单文件范围、来源失效、取消、显示清单、损坏配置，以及 Electron 位置弹窗、导航顺序、新增按钮、条件控件/值保留、待适配提示和 1040/1440 窗口宽度。
- 测试记录：`artifacts/verification/desktop/new-reports-1789369603229/PASS.txt`。截图：`output/playwright/new-reports-1789369603229/`，已人工检查主界面和位置弹窗。
- `scripts/build-update.ps1` 生成 `artifacts/updates/ReportDesk-0.2.0-report-navigation-20260914.zip`。以旧客户端的隔离副本执行 `tests/desktop/report-update-checks.cjs`：通过精确基线校验、三文件更新、重复安装、打包 Host 真实 XML 导入、回退/重装和依赖哈希检查。记录：`artifacts/verification/desktop/report-update-1789369712256/PASS.txt`。
- 保留任务开始时已有的查询条件布局、锁文件和布局检查脚本修改；未提交 Git。
- 实际打包 EXE 使用隔离数据完成真实 XML 启动、位置弹窗及新增检查（无新增时返回 0）验证：`artifacts/verification/desktop/report-update-1789369712256/PACKAGE-UI-PASS.txt`。完整新版目录为 `artifacts/desktop/ReportDesk-navigation-20260914`，复制后再次核对三个更新文件哈希通过。

真实 Oracle/HIS 查询结果、现场菜单准确性和 Windows 10 干净机验收：NOT RUN。缺少菜单对应资料的报表仍显示位置未确认。上述检查未生成或查询业务结果。
