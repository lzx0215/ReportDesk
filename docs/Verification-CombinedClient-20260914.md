# 两个任务的客户端整合验证

整合“页面ui修改”（任务 `01a09db8-eaa9-7933-a3ab-02b044e2d22a`）最终紧凑布局与“更新报表位置与导航功能”。两个任务共享工作目录，当前源码已包含双方实现，无需覆盖文件或执行 Git 分支合并。本次从完整当前源码重新构建，避免仅复制查询布局文件而遗漏导航及 Host 功能。

## 完整客户端

`artifacts/desktop/ReportDesk-combined-20260914/ReportDesk.exe`

同目录其他文件为运行必需。旧客户端目录保留。

合并版包含：120/160/200/240px 固定查询控件、64px 加载按钮、120px 查询按钮、自动换行、自然宽度 Checkbox，以及报表位置弹窗、移除用途及离线演示入口、顶部集中侧栏按钮和新增报表检查。

## 验证结果

- Host Release x64 构建：0 警告、0 错误。
- 更新、回退、重装、依赖及载荷哈希：通过。记录 `artifacts/verification/desktop/report-update-1789370462576/PASS.txt`。
- 打包 ASAR 的 main.cjs、index.html、renderer.js、query-form.js、styles.css 与当前源码逐字节一致；query-form.js 与另一个任务最终客户端一致。三个更新文件及全部声明依赖哈希通过。
- 新增扫描与导航真实 LIB 检查：通过。记录 `artifacts/verification/desktop/new-reports-1789370475088/PASS.txt`。
- 合并版实际 EXE 的 8 张真实报表、9 组数据源、27 组窗口布局检查：通过。结果 `artifacts/verification/desktop/lib-query-layout-1789370509618/results.json`。同时验证默认值、参数名/值、数据源、lookup 调用、Resize 控件身份及状态保持。
- 1280×720 的住院药品处方汇总表条件控件为两行；1920×1080 的科室口服执行单 18 参数为三行。已检查对应截图；无横向溢出或控件重叠。

真实 Oracle/HIS 执行、现场菜单准确性和 Windows 10 干净机验收：NOT RUN。未提交、推送或替换旧客户端。业务 XML 保持只读。
