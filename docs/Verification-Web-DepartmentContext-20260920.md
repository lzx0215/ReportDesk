# Web 当前科室选择更新（2026-09-20）

适用于已安装 net462 的 lib-startup-fix 或 search-fix 版本。此更新同时修改后端和前端，不能只替换两个 JS 文件。

## 使用

打开报表条件，先在“当前科室（CurrentDeptID）”点击加载选项，选择科室，再加载其他科室条件。独立科室来源为现有字典 COM_BD_DEPARTMENT 的 DEPARTMENT_ID、DEPARTMENT_NAME，过滤 VALID_FLAG = '1'，按名称、编码排序；不限制科室类型，不添加代表全部科室的虚构编码。

“各科室门诊人次统计”的“科室”和“核算科室”选项 SQL 都依赖 CurrentDeptID。原 TARGET_DEPT、部门类型和特殊科室规则保持不变。缺少上下文时提示先选择哪个条件；切换当前科室后清空依赖它的旧选项和选择，需重新加载。

选择的是本次查询上下文，不是自动获取 HIS 登录身份，也不构成身份授权。其他操作员、角色、PermissionDept 等上下文不能推测。用户输入不持久化，源 XML 和数据库结构不修改。

## 替换与回退

1. 在 IIS 确认 ReportWeb 的当前物理目录。此前为 C:\a_userspace\ReportTool\ReportDesk-Web，以现场配置为准。
2. 等待当前查询结束，备份网站的 bin、UI、Web.config；暂停 ReportWeb 对应应用程序池。暂停会中断未完成任务。
3. 将新包的 bin 和 UI 文件夹内容复制到网站的对应文件夹，替换同名文件。保留服务器现有 Web.config，尤其 Offline=false、ReportsDirectory=C:\LIB、连接及同步配置。新包的默认配置为离线模式，不要覆盖现场配置。文件不放进 C:\LIB，也不通过网页导入。
4. 启动应用程序池，浏览器 Ctrl+F5。先加载当前科室，再加载两个依赖科室条件，验证切换当前科室后旧选择被清空。页面刷新会清空当前条件和结果。
5. 若异常，停止同一应用程序池，恢复备份的 bin 和 UI，启动并 Ctrl+F5。本次升级不迁移数据、不修改报表 XML。

完整压缩包内 manifest.json 是文件及 SHA256 清单，供校验使用，不是运行配置；不要用它代替 Web.config。

## 已执行验证

- net462 Web.Checks 构建：零警告、零错误。
- 使用真实 LIB 中“各科室门诊人次统计”和“住院各科有效收入统计”的 XML 副本做离线检查：当前科室独立加载、依赖识别、缺失参数提示、绑定参数和原科室限制均通过；原 XML 字节未改变。
- HTTP 检查 58 项及 transport-checks.cjs 通过。
- Chromium：公共页面、管理页面、子路径及已有搜索/输入法回归通过；用真实报表元数据验证缺失依赖阻止请求、当前科室独立请求、选中编码传递、切换后清空两个依赖条件。不执行报表查询。浏览器选择值仅为界面测试标记，不是模拟的 Oracle 返回数据。
- query-form.js、renderer.js JavaScript 语法检查通过。

真实 Oracle 字典权限、实际科室列表和 HIS 同条件结果、服务器 IIS 和 Windows 10 现场验收未执行。需要服务器保持 Offline=false 并已配置可用连接后由现场验证。
