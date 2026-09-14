# 报表位置与导入分类

ReportDesk 按 XML 内容识别独立查询定义，不按文件名或分类猜测报表类型。

- `ReportQueryInfo` 且 `QueryDataSource/QueryDataSource/Sql` 非空：独立查询报表；不支持的映射、控件和语法仍显示为待适配。
- `ReportQueryInfo` 没有报表 SQL：仅条件或定义不完整，单独列出，不作为独立查询显示。
- `Spread class="FarPoint.Win.Spread.FpSpread"`：版式，保留关联检查，不单独显示。
- 其他 XML：不作为当前支持的查询定义，不据此删除原文件。

## 外部位置配置

程序不内置具体业务系统菜单、目录或报表位置。页面可显示用户通过 `report-locations.xml` 提供的多个位置；位置必须有明确的 `evidence`，不能从文件名、分类、DDL 或同名菜单推导。位置配置只影响说明和分类展示，不改变报表查询、显示清单、SQL 或权限。

配置文件放在 `ReportDesk.exe` 同目录，重启生效。每个 `Report` 必须只使用 `id`、`hash`、`file` 中的一个标识；`file` 只作名称关联，不代表版本已核对。支持一个报表多个 `Location`，每个位置至少包含两个 `Segment`。

```xml
<?xml version="1.0" encoding="utf-8"?>
<ReportLocations version="1">
  <Report file="sample-query.xml">
    <Location evidence="用户提供的菜单记录">
      <Segment>报表中心</Segment>
      <Segment>示例入口</Segment>
    </Location>
  </Report>
</ReportLocations>
```

外部配置错误时整份配置不使用并提示位置可能不完整；报表查询及显示权限规则不受影响。缺少来源证据的报表保持“位置未确认”。
