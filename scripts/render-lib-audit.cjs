// Real LIB metadata only; the original session review remains an immutable baseline.
const fs=require('node:fs'),path=require('node:path');
const directory=path.resolve(process.argv[2]),baseline=path.resolve(process.argv[3]);
const read=f=>JSON.parse(fs.readFileSync(f,'utf8').replace(/^\uFEFF/,''));
const audit=read(path.join(directory,'audit.json')),before=read(baseline);
const reports=audit.rows.filter(r=>r.classification==='Report');
function action(r){
 const actions=[];
 if(r.issues.some(i=>i.includes('科室下人员'))) actions.push('需要还原 PermissionDept(EXP-REPORT-01) 的角色、操作员与授权科室来源，再加载所属人员；不能改成全院人员或移除范围。');
 if(r.issues.some(i=>i.includes('CustomControl'))) actions.push('XML 引用 FS.Expense.UI.InPatient.Base.ucInPatientNOForReport/RegisterID；现有 LIB 未找到 FS.Expense.UI.dll。需找到同版本组件或该控件的现场赋值证据，不能直接当作住院号。');
 if(r.codes.includes('table-group') || r.issues.some(i=>i.includes('分组数据源'))) actions.push('原引擎先执行 TableGroupUsing 和主表查询，再按组对结果表筛选及映射（包含患者分组，也有按 daylist 日期分组），此链尚未实现。这四份 XML 的患者树另有来源异常：两份配置为 EmployeeType=D（医生），两份引用 dtPatientInfo 但没有对应 ConditionUsing，需核对 HIS 实际赋值，不能猜成患者列表。');
 if(r.issues.some(i=>i.includes('禁用控件'))) actions.push(r.name.includes('出院')?'XML 禁用了医生条件；需核对调用窗口注入的医生值。不能擅自开启或取全部医生。':'XML 的 RegisterID/BalanceID 为禁用控件，需追踪 HIS 调用方 SetParm 的住院流水号和结算序号来源，明确后添加上下文输入。');
 if(r.issues.some(i=>i.includes('字典/自定义')||i.includes('树形条件')||i.includes('患者类型'))) actions.push('核对源 XML 的 QueryDataSource/DataSourceTypeName 及调用窗口。剩余有空字典类型、空下拉绑定、DataSource=abc、DepartmentType=N,I；不要按名称猜选项或把旧 DefaultDataSource 当作有效字典。');
 if(r.issues.some(i=>i.includes('参数名为空或重复'))) actions.push('两个不同含义的控件都叫 dtUseType。需要对照 HIS 正在使用的定义确认参数与 SQL 对应关系，不能静默覆盖其中一个。');
 return actions;
}
for(const r of reports) r.next=action(r);
const partial=reports.filter(r=>r.pending && r.sources.some(s=>!s.issues.length)).length;
const model={source:audit.source,scanned:audit.scanned,reports:audit.reports,ready:audit.ready,pending:audit.pending,partial,incomplete:audit.incomplete,layouts:audit.layouts,other:audit.otherXml,previousPending:before.pending,rows:reports,excluded:audit.excluded};
fs.writeFileSync(path.join(directory,'summary.json'),JSON.stringify({...model,rows:undefined,excluded:undefined},null,2));
const html=`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>ReportDesk · LIB 全量适配核查</title>
<style>body{font:16px/1.7 system-ui;background:#f7f6f3;color:#302e29;max-width:1150px;margin:30px auto;padding:0 22px}h1{font-size:27px}article,details{background:white;border:1px solid #d8d1c3;padding:14px 20px;border-radius:7px;margin:12px 0}summary{cursor:pointer;font-weight:600}input,select{font:inherit;padding:9px;border:1px solid #c5bba5;border-radius:5px;background:white}input{min-width:340px}small{display:block;color:#706c61;overflow-wrap:anywhere}.note{color:#74591d}p{margin:8px 0}</style>
<h1>ReportDesk · LIB 全量适配核查 · 2026-09-11</h1><p id="totals"></p>
<article><strong>当前完成范围</strong><p>原引擎证据：提供的 FS.Core.UI.dll 中 ucCommonWindow、ucMainFpReport 和 DataSetHelper。已补交叉统计、组合列/横向合计、行分组、树形编码多选、前置条件数据源及标量控件引用；Finance 的 RegisterID 改为明确填写住院流水号。</p><p>SQL 语法位置的全角括号仅在导入副本修正；原文件、字符串和注释保持原样。用户值仍逐项绑定，不复制到 SQL。列表默认显示全部，仅保留搜索栏，底部进度条保留。</p><p class="note">无静态阻塞 ≠ Oracle 执行成功 ≠ 与 HIS 结果一致。实际 SQL 执行、交叉/合计数值、选项值和多选查询尚未做内网验证。没有使用演示数据或伪造结果。</p></article>
<article><strong>剩余工作如何解决</strong><p>下面逐张列出仍需开发、需要调用上下文或源配置矛盾的情况。再次导入相同 XML 无法自动解决这些问题。68 份无查询 SQL 的定义另外保留为不完整文件，不能断言它们不是 HIS 业务报表。</p><p>本目录只扫描 XML 查询定义；如果 HIS 另有硬编码报表窗口，也需要单独追踪其入口和取数实现，不能据此承诺整个 HIS 已全覆盖。</p></article>
<input id="search" aria-label="搜索" placeholder="搜索报表名称、位置或原因"><select id="state"><option value="pending">仍有阻塞</option><option value="ready">无静态阻塞</option><option value="all">全部报表</option></select><p id="count"></p><div id="rows"></div>
<details><summary>排除的版式、其他 XML 和不完整定义</summary><p id="excluded-count"></p><div id="excluded"></div></details>
<script>const data=${JSON.stringify(model).replaceAll('<','\\u003c')};const $=s=>document.querySelector(s),el=(tag,text)=>{const n=document.createElement(tag);n.textContent=text;return n};
$('#totals').textContent='扫描 '+data.scanned+' 份 XML；识别 '+data.reports+' 张报表：'+data.ready+' 张无静态阻塞、'+data.pending+' 张仍有阻塞（基线 '+data.previousPending+' 张）。';
function render(){const term=$('#search').value.toLowerCase().trim(),state=$('#state').value;const rows=data.rows.filter(r=>(state==='all'||state==='pending'&&r.pending||state==='ready'&&!r.pending)&&JSON.stringify([r.name,r.path,r.issues,r.locations]).toLowerCase().includes(term));$('#count').textContent='匹配 '+rows.length+' 张';$('#rows').replaceChildren();for(const r of rows){const box=el('details','');box.append(el('summary',r.name+' · '+(r.pending?'仍有阻塞':'无静态阻塞，结果未核对')),el('small',r.path));for(const s of r.sources)box.append(el('p',s.name+'：'+(s.issues.join('；')||'无静态阻塞')));for(const n of r.next)box.append(el('p','处理：'+n));for(const l of r.locations)box.append(el('small',(l.Candidate?'候选位置（未确认）：':'位置：')+l.Path+'；'+l.Evidence));$('#rows').append(box)}}
$('#excluded-count').textContent=data.layouts+' 份版式、'+data.other+' 份其他 XML、'+data.incomplete+' 份不完整定义。';for(const x of data.excluded.filter(x=>x.kind==='Incomplete'))$('#excluded').append(el('small',x.path+'：'+x.reason));for(const id of ['search','state'])$('#'+id).oninput=render;render();</script></html>`;
fs.writeFileSync(path.join(directory,'review.html'),html);console.log(JSON.stringify({...model,rows:undefined,excluded:undefined}));
