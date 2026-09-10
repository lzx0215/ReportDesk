# 0.1.3 取消限额与错误日志验证

范围：取消命令超时、应用连接超时设置、自动计时取消、查询行数及 32 MiB 截断；手动取消保留。TNS 文件保持原样，不修改生产网络/数据库配置。高级页去掉限额输入，旧配置字段保留兼容但不再使用。

新增 ErrorLog：按日期 UTF-8 追加，记录时间/版本/架构/操作/异常链/错误码/方法调用栈；过滤凭据、SQL、参数和引用内容。不输出 Exception.Data 或局部变量。无上下文的未捕获异常仅记录类型/码/调用栈。日志写入失败明确返回失败提示，不自动清理历史日志。

首轮完整构建通过：x86/x64 各 PASS 52 checks，UI 冒烟均 PASS，4 次构建 0 警告、0 错误。新增检查用 DataTableReader 读取 60001 行、按原字节预算超过 32 MiB 的合成结果，最后一行完整保留；取消读结果、旧限额兼容、基本连接描述无应用 timeout/retry 参数、演示超过原 1000 行均通过。日志追加/去重/异常链/调用栈/敏感过滤/不写原始未知消息/并发写入/目录不可写提示均通过。

UI 模拟连接失败生成了隔离日志并显示日志路径；重开连接页以及原报表查询/筛选/导出回归通过。运行环境 Windows 11 x64 的 x86/x64 进程；无真实数据库连接。真实 Win10、长 SQL、长连接等待、实际 Oracle 错误、服务器取消行为、生产大结果内存情况均 NOT RUN。

构建证据：artifacts/verification/build-unlimited-log-first.txt；最终产物和 UI 证据位于 artifacts/verification/0.1.3。程序包 ReportDesk-0.1.3-win-x86.zip / win-x64.zip；旧包保留。

最终重跑同样各 52 项检查及 UI PASS、0 警告/错误，日志见 artifacts/verification/build-unlimited-log-final.txt。已目视核对高级页“不限制”显示，验证两包 EXE/Core 的 PE 架构及 ZIP 内程序/README/Acceptance 与 release 文件 SHA256 一致；压缩包未包含运行日志或 catalog.json。证据：artifacts/verification/0.1.3/package-proof.txt。

实际边界：取消应用限制不能取消 TNS/驱动/OS/数据库端超时或硬件内存上限；Excel 单工作表容量限制保留，超容量明确报错。未执行任何发布、生产连接、SQL/Schema 修改。
