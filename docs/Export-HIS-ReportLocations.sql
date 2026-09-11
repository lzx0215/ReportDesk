-- 在内网 HIS 查询工具中分别执行并导出四个结果为 Excel/CSV。
-- 仅查询菜单/报表配置，不含业务结果、不修改数据。此处未连接内网执行。
-- 字段名来自本次提供的 FS.Manager.DMO / DAL 程序集；MEMU_ID 为原库拼写。

-- 1. 菜单到查询 XML、版式 XML 的明确绑定（最关键）
SELECT MEMU_ID, QUERYSETTINGDIRECTORY, QUERYSETTINGFILE,
       FPSETTINGDIRECTORY, FPSETTINGFILE
FROM SYS_BD_REPORTSETTING;

-- 2. “抗菌药物查询”等复合窗口内部的子报表菜单
SELECT MENU_CONTROL_ID, MENU_CONTROL_NAME, MENU_ID,
       GROUP_ID, RESOURCE_ID, SORT_ID, VALID_FLAG
FROM SYS_BD_MENU_CONTROLS;

-- 3. 菜单组名称及上级，例如“报表中心”；现有菜单表只有 GROUP_ID
SELECT GROUP_ID, GROUP_NAME, PARENT_GROUP_ID
FROM SYS_BD_GROUP;

-- 4. 通用报表窗口的 Report / QueryFilePath 等属性配置
-- 只导出报表窗口对应资源。SETTING 如果是 CLOB，导出时须保存完整内容，不能截断。
SELECT S.RESOURCE_ID, S.SETTING
FROM SYS_BD_RESOURCE_SETTING S
JOIN SYS_BD_RESOURCE R ON R.RESOURCE_ID = S.RESOURCE_ID
WHERE R.WIN_NAME = 'FS.Core.UI.Report.Common.Implement.ucCommonWindow,FS.Core.UI'
   OR R.WIN_NAME = 'FS.Manager.UI.SystemManager.ucPrivReport,FS.Manager.UI';
