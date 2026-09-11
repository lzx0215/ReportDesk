> 后续实现说明：本次 HIS 程序集与 Excel 核对已扩展实际适配和菜单候选分类，见 [HIS-Adaptation-20260911.md](HIS-Adaptation-20260911.md)。以下原验证证据保留；精确三文件组合不再是普通 SQL 表格查询的唯一准入条件。

# HIS 位置与导入分类

ReportDesk 按 XML 内容识别独立查询定义，不按文件名中的“报表”二字判断。

- `ReportQueryInfo` 且 `QueryDataSource/QueryDataSource/Sql` 非空：独立查询报表；不支持的映射、控件和语法仍显示为待适配。
- `ReportQueryInfo` 没有报表 SQL：仅条件或定义不完整，单独列出，不作为独立报表显示。这不证明它不是 HIS 业务报表，可能需要窗口代码提供数据。
- `Spread class="FarPoint.Win.Spread.FpSpread"`：版式，保留关联检查，不单独显示。
- 其他 XML：不是当前支持的查询定义，不独立显示。不能据此删除原文件。
- 无法读取/不合法 XML：显示扫描警告，不宣称已完整分类。

旧 catalog.json 格式、原始条目、收藏和备注保持兼容；旧库中的无 SQL 条目在入口过滤，未删除。重导入后若补齐 SQL，会按原 ID 恢复展示并保留备注。

## 多个 HIS 位置

页面及“报表说明”显示所有已知位置；搜索框可搜索位置。路径是 HIS 菜单层级，与本地 XML 文件路径分开展示。没有来源的标为“位置未确认”；已知路径不代表当前已收集完整菜单。

内置用户于 2026-09-11 提供的“普通门诊处方记录查询设置.xml”位置：报表中心 → 各职能科室用表 → 药剂科 → 抗菌药物查询。按文件名关联并明确标注版本未核对，不把它误套到“门诊处方患者明细”。

后续取得实际对应关系后，可在 **ReportDesk.exe 同目录** 放置 `report-locations.xml`，重启生效。每个 Report 必须使用 id、hash、file 中的一个标识；优先用查看 catalog/audit 中的 id 或 SourceHash。file 只作名称关联，不代表同名文件的版本已核对。每个 Location 必须填写真实来源 evidence，路径层级使用 Segment。支持一个报表多个 Location、重复 Report 合并，不以最后一条覆盖前面的路径。

```xml
<?xml version="1.0" encoding="utf-8"?>
<ReportLocations version="1">
  <Report file="普通门诊处方记录查询设置.xml">
    <Location evidence="用户提供，2026-09-11">
      <Segment>报表中心</Segment>
      <Segment>各职能科室用表</Segment>
      <Segment>药剂科</Segment>
      <Segment>抗菌药物查询</Segment>
    </Location>
  </Report>
</ReportLocations>
```

有第二个已确认入口时，在同一 Report 下添加第二个 Location，写全从顶层开始的所有 Segment。不要根据报表分类、文件夹或文件名猜路径。外部配置错误时整份外部位置配置不使用，界面提示位置可能不完整；报表查询及显示权限规则不受影响。小更新不覆盖此文件。

## 本地菜单资料边界

本次在知识库核对到 `00_Inbox/yljhis/schema/HIS.sql` 的 SYS_BD_MENU 表结构（71412 行），包括 MENU_ID、MENU_NAME、PARENT_MENU_ID、GROUP_ID、RESOURCE_ID、PARAMETER，以及 SYS_BD_RESOURCE 窗口资源结构（71702 行）。表结构不能恢复实际菜单路径。

已检索知识库索引与文本；检查报表 XML、字典工作簿 29 个工作表及字段工作簿 6 个工作表；未找到完整 HIS 菜单记录和报表 XML 对应数据。需要现场导出菜单记录及关联的资源/窗口配置，其中保留父子 ID、菜单名称、功能/资源 ID 和参数，再核对参数如何指向 XML。不能把 PARAMETER 未核对的内容直接当作 XML 文件名。当前不自动连接数据库或生成未经确认的业务 SQL。
