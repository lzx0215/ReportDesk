# QueryFieldsPanel 统一宽度验证（2026-09-14）

本轮基于已合并“页面ui修改”和报表位置/导航功能的客户端，只调整查询字段尺寸、加载按钮呈现、完整值提示及流式布局。保留已有报表位置、检查新增报表、导航功能。没有修改 XML、ParameterDefinition、Host Details、SQL、Required、lookup、Multiple、TreeSelect、implicitValue 或参数顺序。

## 最终规则

| Token | 值 |
| --- | --- |
| `--query-field-width` | 144px |
| `--query-date-width` | 176px |
| `--query-datetime-width` | 200px |
| `--query-control-height` | 32px |
| `--query-field-gap-x` | 10px |
| `--query-field-gap-y` | 8px |
| `--query-label-gap` | 4px |
| `--query-label-font-size` | 12px |
| `--query-load-width` | 30px |
| `--query-auxiliary-gap` | 4px |

- 删除 S/M/L/XL 分级方法和样式；普通字段不再按名称、标签长度、类型或窗口宽度调整尺寸。
- 普通加载字段为 110px 输入/下拉框 + 4px 间隔 + 30px 图标按钮，总宽 144px。复用现有 SVG 图标渲染机制，无新增依赖；保留原点击处理，提供“加载选项” title 和包含字段名的无障碍标签。
- Select 闭合时省略长内容，悬停/聚焦时更新完整选中值的 title。展开仍使用原生菜单，没有给菜单设置 144px 宽度限制。已人工查看真实数据源原生菜单，完整显示 `dtMain · 查询数据源`；未声称任意极长选项在所有系统均可完整显示。
- TextBox 固定宽度，长内容使用原生内部滚动，并提供当前值 title。
- Checkbox 自然内容宽度、不拉伸，长文本允许换行，最大字段宽度 240px。
- 查询按钮 144×32px，保持最后一个 Flow Item；所有字段禁止伸缩，只允许整项换行。

## 真实报表行数

下表是窗口外尺寸；行数包含数据源与开始查询按钮。保留左侧报表列表，未调整其他区域以凑行数。

| 报表 | 1040×700 | 1280×720 | 1440×930 | 1920×1080 |
| --- | ---: | ---: | ---: | ---: |
| 按科室(出院) | 2 | 2 | 2 | 1 |
| 住院药品处方汇总表(按科室) | 3 | 2 | 2 | 1 |
| 门诊中药处方查询 | 2 | 2 | 2 | 1 |
| [中联]医保费用明细（9 参数） | 3 | 3 | 2 | 2 |
| 科室口服执行单查询（18 参数） | 6 | 5 | 4 | 3 |

医保费用明细在本次检查的 1440 和 1920 窗口下为两行；不能承诺 8～10 参数报表在所有桌面尺寸均为两行。1280 窗口实际查询栏约 910px，该报表第二行若排入 6 个普通项需 914px，因此保持固定尺寸和原顺序后为三行。

固定宽度也不保证每个窗口的总高度均减少：18 参数报表在 1920 窗口下因较长标签换行，高度由约 233px 增至 251px；其余三个测试尺寸则下降。没有为压缩高度隐藏标签或改变业务顺序。

## 构建和检查

- `scripts/build-update.ps1`：成功，0 警告、0 错误。
- `tests/desktop/lib-query-layout-checks.cjs`：PASS，10 张真实报表、11 个数据源案例 × 4 个窗口，共 44 组对照。核对实际尺寸、整项换行、无交叠、无横向溢出、长标签、title、加载图标、初始值、参数顺序和提交/lookup IPC；连续调整窗口时字段节点、状态保持不变。
- 真实 LIB 为 `D:\系统知识库\00_Inbox\yljhis\LIB\LIB`，加载 1284 张可见报表。查询和 lookup IPC 在测试中拦截取消，使用内存 UI 状态验证传参，没有调用数据库或伪造查询结果。
- 结果：`artifacts/verification/desktop/lib-query-layout-1789372386014/results.json`；相同目录含窗口截图。
- 两次先前检查因旧断言失败：要求医保在 1280 下两行，以及所有简单报表高度必须严格下降。按本次固定尺寸要求修正测试前提后完整重跑通过；没有通过改变业务或缩小字段迎合旧断言。
- `tests/desktop/report-update-checks.cjs`：PASS，精确基线、三文件更新、幂等、真实 XML 导入及 Details、回退/再应用、依赖哈希检查。证据：`artifacts/verification/desktop/report-update-1789372183939/PASS.txt`。
- 新客户端 app.asar 内五个 UI/入口文件与当前源码逐字节一致。Host Program.cs、main.cjs、index.html 与本轮修改前 SHA256 相同。
- `git diff --check` 通过；仅有既有文件 LF/CRLF 提示。
- Oracle、真实 HIS 结果对照、人工拖拽窗口、Windows 10 x86：NOT RUN。

## 交付

- 完整客户端：`artifacts/desktop/ReportDesk-query-uniform-20260914/ReportDesk.exe`。分发时保留整个所在目录。
- 增量包：`artifacts/updates/ReportDesk-0.2.0-query-uniform-20260914.zip`，仅适用于精确基线 `artifacts/desktop/ReportDesk-combined-20260914`。
- ZIP SHA256：`E466CF16665AD361C7736A82FC4000B71A293AA6F959F5BA67000ABC383A95A1`。
- 原客户端及本轮修改前文件保留；未 commit 或 push。
