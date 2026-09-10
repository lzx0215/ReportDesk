# 0.1.4 手工报表显示配置验证

范围：仅通过 EXE 同目录的 `report-visibility.xml` 手工维护报表 ID 清单，启动时读取；界面不提供岗位配置或切换入口。未设置保持旧行为；selected 模式默认隐藏未列出的报表，空清单不回退；错误配置停止启动。未修改 catalog 持久化格式、原 XML、SQL、科室限制、数据库权限或凭据逻辑。新增配置说明和 example，未代填实际医生/护士报表。

实际执行：项目根目录 `powershell -File scripts/build.ps1`。Windows 11 家庭版 x64 上完成 x86/x64 编译及进程检查，各 PASS 60 checks（原 52 项回归＋8 项显示配置检查）；4 次构建均 0 警告、0 错误。两种进程的 WinForms 离线冒烟均 PASS。

配置检查覆盖：缺文件、ID 匹配而非名称/分类、共享报表、空清单、未匹配 ID、all 模式、重读、格式和未知字段错误、外部实体拒绝、文件上限和独占锁导致的读取失败，以及原文件不被改写。UI 检查覆盖：同名不同 ID、全部/分类/收藏/最近/待适配/搜索、导入合并后过滤、演示入口、获显示报表的模拟查询/筛选/导出、空列表清除结果及禁用相关操作、修改配置后重开生效、错误配置拒绝启动。已目视核对 x64 visibility.png，无岗位选择或编辑控件，隐藏分类和演示入口不出现。

首轮发现 `InvalidDataException` 没有进入预期的统一中文诊断分支（不能仅按 `IOException` 捕获）。已显式纳入异常分支，并完整重跑两种架构通过。首轮失败日志保留为 `artifacts/verification/0.1.4/build-visibility.txt`；修复后日志为 `build-visibility-fixed.txt`。

打包核对通过：两包 EXE 产品版本均为 0.1.4；ZIP 内 EXE、Core DLL、配置说明和 example 与 release 中对应文件 SHA256 一致；无正式 `report-visibility.xml`、catalog.json 或运行日志。包不会自动启用岗位清单，更新时需复制人工配置到新 EXE 目录。核对记录为 `artifacts/verification/0.1.4/package-proof.txt`。

- x86 ZIP SHA256：`1AB1CE926C72EA6C96A61F48F06AD70773E21E7DC2C9141D4A0E2E5CC8E93B1E`
- x64 ZIP SHA256：`FBEA18914F48D95EC408CF9841C8CD519B89DBD372DB11D73A321E076C44D1CA`

当前目录不是 Git 仓库；保留修改前文件副本 `artifacts/verification/0.1.4/baseline-20260910-173353`，通过 `git diff --no-index` 核对原有文件改动并保存为 `source-diff.txt`，另检查新增文件。未执行 commit、push、部署或真实数据库连接。

未执行：真实岗位清单核对、Oracle 查询、Windows 10 32/64 位现场验收，均 NOT RUN。本功能仅为显示偏好；同电脑同程序目录共用清单，能修改/删除配置者能改变显示范围，SQL 数据行范围仍按原查询和数据库端规则处理。
