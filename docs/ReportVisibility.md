# 手工配置岗位报表显示（0.1.4）

适用需求：由配置人员决定每套 ReportDesk 打开后显示哪些报表。无登录，无前端岗位选择、切换或授权管理。它是本地显示配置，不是身份认证或数据库权限；修改或移除文件即可改变显示范围，不限制 SQL 数据行范围。

## 配置位置与生效方式

在 **ReportDesk.exe 同一目录**放置 `report-visibility.xml`，UTF-8 编码，关闭程序后修改，重新启动生效。路径与当前工作目录无关。不同岗位使用各自程序目录中的配置文件；同一程序目录下的所有 Windows 用户共用此显示清单，但各自的本地报表目录仍独立。

- 没有此文件：兼容旧版，显示本地目录中的全部报表。
- `mode="selected"`：只显示列出的报表 ID，空清单或全部 ID 未匹配时不显示任何报表。
- `mode="all"`：显示全部，不允许同时写入 Report 清单，避免配置误解。
- 文件损坏、字段拼错或读取失败：提示配置错误并停止启动，不回退到全部，也不覆盖配置或报表库。
- 只在启动时读取；运行中修改或删除文件不会刷新当前窗口，必须重启。

## 操作步骤

1. 使用原有导入功能建立目标 Windows 用户的报表库，关闭程序。
2. 在该 Windows 用户的 PowerShell 中执行下列只读命令，查看每张报表的 ID、名称和来源。只输出目录元数据，不输出连接设置、SQL、参数或结果。

```powershell
$reportCatalogPath = Join-Path $env:LOCALAPPDATA 'ReportDesk\catalog.json'
$reportCatalog = Get-Content -LiteralPath $reportCatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$reportCatalog.Reports | Select-Object Id, Name, SourcePath | Format-List
```

若提示路径不存在，请先用该 Windows 用户启动程序并导入报表。成功时会逐张显示 `Id`、`Name`、`SourcePath`；同名报表要根据来源区分。请复制实际 Id，不要把姓名、报表名称或 XML 文件名当作 Id。

3. 将随包的 `report-visibility.example.xml` 复制为 `report-visibility.xml`，编辑清单。下面的 ID 是占位文字，必须换成上一步复制的实际值；`name` 只是方便人工阅读的备注，不参与匹配。

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- 护士电脑的报表清单 -->
<ReportVisibility mode="selected">
  <Report id="替换为护理报表实际Id" name="护理工作量" />
  <Report id="替换为公共报表实际Id" name="科室业务统计" />
</ReportVisibility>
```

医生电脑采用相同格式，手工列出需要显示的医生报表及公共报表 ID。同一报表可以列在多份配置中，不必在程序内新增岗位或修改报表分类。

4. 重启 ReportDesk，检查全部报表、分类、搜索、收藏、最近使用、待适配是否只出现清单内报表。清单只决定显示；原来待适配的报表仍不能查询。

## 配置细节

- ID 精确匹配，忽略大小写；不支持名称匹配、通配符或分类自动分配。重复 ID 无额外作用，未匹配 ID 不会扩大范围。
- 当前导入器的 ID 来自 XML 完整来源路径：在相同路径更新 XML，ID 保持；换路径重新导入，ID 改变。跨电脑的导入路径不同，需要重新核对目标目录中的 ID。
- 新导入但未列出的报表不会自动显示；导入、保存收藏和修改分类不会写入此配置文件，也不会删除隐藏报表。
- 自带演示报表 ID 为 `built-in-demo`。selected 清单未列出它时，隐藏“体验演示”按钮且不会加入演示报表。
- 原有导入、连接设置等功能保留；本功能不增加操作权限控制。想展示某张隐藏报表，需配置人员修改文件并重启。
- XML 属性中的 `&` 和 `"` 需分别写成 `&amp;` 和 `&quot;`。元素及属性名区分大小写；不支持 DTD、外部实体和未知字段。

## 更新与恢复

更新程序前保留旧程序目录及其 `report-visibility.xml`，并按 README 备份本地报表库。把配置复制到新程序目录，核对文件名后再启动。程序包只附带 example 文件，不自动启用或覆盖人工配置；新目录没有正式文件时将显示全部。

恢复全部显示可以将配置改成以下内容，然后重启：

```xml
<ReportVisibility mode="all" />
```

本次仅添加本地显示配置，不修改数据库、凭据存储、SQL 或科室条件，不需要数据库备份或回滚。回退旧版程序会失去该显示过滤功能。真实岗位清单由配置人员填写；Oracle 与 Win10 x86/x64 现场验收未执行时保持 NOT RUN。
