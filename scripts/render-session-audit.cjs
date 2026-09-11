// Read-only audit presentation: names, paths, blocker diagnostics and menu evidence;
// no report SQL, query parameter values or patient/result data.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const directory = path.resolve(process.argv[2]);
const audit = JSON.parse(fs.readFileSync(path.join(directory, 'audit.json'), 'utf8').replace(/^\uFEFF/, ''));
const before = JSON.parse(fs.readFileSync(path.resolve(process.argv[3]), 'utf8').replace(/^\uFEFF/, ''));
const reports = audit.rows.filter(r => r.classification === 'Report');
const partial = reports.filter(r => r.pending && r.sources.some(s => !s.issues.length));
const blocked = reports.filter(r => r.pending && r.sources.every(s => s.issues.length));
const newlyReady = reports.filter(r => !r.pending && before.rows.find(b => b.id === r.id)?.pending);
const improved = reports.filter(r => before.rows.find(b => b.id === r.id)?.codes.includes('default') && !r.codes.includes('default'));
assert.equal(reports.length, audit.reports);
assert.equal(audit.ready + partial.length + blocked.length, audit.reports);
const model = { totals: { reports: audit.reports, ready: audit.ready, partial: partial.length, blocked: blocked.length }, newlyReady: newlyReady.map(r => r.name), families: audit.families.filter(f => f.visibleReports), rows: reports };
fs.writeFileSync(path.join(directory, 'summary.json'), JSON.stringify({ ...model.totals, scanned: audit.scanned, incomplete: audit.incomplete, layouts: audit.layouts, other: audit.otherXml, newlyReady: model.newlyReady, defaultRuleImproved: improved.map(r => r.name) }, null, 2));
const html = `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>ReportDesk 待适配核查</title>
<style>body{font:16px/1.6 system-ui;margin:32px auto;max-width:1100px;padding:0 20px;background:#f8f7f4;color:#272923}h1{font-size:27px}input,select{font:inherit;padding:8px;margin:4px;border:1px solid #bcb9b0;background:white;border-radius:5px}article,details{background:white;padding:14px 20px;margin:12px 0;border:1px solid #ddd9cf;border-radius:6px}summary{cursor:pointer;font-weight:600}small{display:block;color:#666;overflow-wrap:anywhere}pre{white-space:pre-wrap;font:inherit}.note{color:#745c2c}table{width:100%;border-collapse:collapse}td,th{padding:8px;text-align:left;border-bottom:1px solid #ddd}#rows p{margin:8px 0}</style>
<h1>ReportDesk 待适配核查 · 2026-09-11</h1><p id="totals"></p><p class="note">这是本机静态分析，不代表已在 Oracle 或 HIS 中验证查询结果。每个报表可能有多个原因，以下数量不能相加。原 LIB 未修改。</p>
<article><h2>本轮处理</h2><p>Electron 直接读取所选 XML 到本次会话，关闭后不恢复报表清单；移除收藏、最近、分类和说明编辑。连接设置单独保存。旧 catalog.json 保留原样，仅兼容读取其连接设置。</p><p>原 HIS 的 SQL / 字典下拉选项不使用 DefaultDataSource 中的自定义选项。按该规则解除误判，不把残留选项当作默认查询条件。</p><p id="new"></p></article>
<details><summary>各规则数量与处理方式</summary><div id="families"></div></details>
<article><h2>仍需开发处理的重点</h2><p>先处理行分组小计和树形多选，它们覆盖最多报表。行分组需核对原引擎不同分支的数值类型、小计插入位置及重复组行为；树形“全部”会展开多个编码，不能直接传 ALL 或拼接用户值。</p><p>交叉统计、组合列、指定行汇总和前置数据源按独立规则实现。全角括号需要逐条定位并核对修正副本。角色/患者上下文、跨库权限、真实汇总结果最后才需要内网验证；不用用户逐张整理全库。</p></article>
<input id="search" aria-label="搜索报表或提示" placeholder="搜索报表名称、位置、提示…"><select id="state" aria-label="报表状态"><option value="pending">仍有待适配</option><option value="blocked">全部数据源阻塞</option><option value="partial">部分可试查</option><option value="ready">无静态阻塞</option><option value="all">全部</option></select><select id="rule" aria-label="阻塞规则"><option value="">所有规则</option></select><p id="count"></p><div id="rows"></div>
<script>const data=${JSON.stringify(model).replaceAll('<', '\\u003c')};
const $=s=>document.querySelector(s),node=(tag,text)=>{const n=document.createElement(tag);n.textContent=text;return n};
$('#totals').textContent=data.totals.reports+' 张报表：'+data.totals.ready+' 张无静态阻塞，'+data.totals.partial+' 张部分可试查，'+data.totals.blocked+' 张全部数据源仍阻塞。';
$('#new').textContent='本轮新增无静态阻塞 '+data.newlyReady.length+' 张：'+data.newlyReady.join('、');
for(const f of data.families){$('#rule').add(new Option(f.title+'（'+f.visibleReports+'）',f.code));const p=node('p',f.title+'：'+f.visibleReports+' 张。'+f.action);$('#families').append(p)}
function render(){const text=$('#search').value.trim().toLowerCase(),state=$('#state').value,rule=$('#rule').value;const rows=data.rows.filter(r=>(!text||JSON.stringify([r.name,r.path,r.issues,r.locations]).toLowerCase().includes(text))&&(!rule||r.codes.includes(rule))&&(state==='all'||state==='pending'&&r.pending||state==='ready'&&!r.pending||state==='partial'&&r.pending&&r.sources.some(s=>!s.issues.length)||state==='blocked'&&r.pending&&r.sources.every(s=>s.issues.length)));$('#count').textContent='匹配 '+rows.length+' 张';$('#rows').replaceChildren();for(const r of rows){const box=document.createElement('details');box.append(node('summary',r.name+' · '+r.status),node('small',r.path));for(const s of r.sources)box.append(node('p',s.name+'：'+(s.issues.join('；')||'无静态阻塞，需内网核对')));for(const g of r.guidance)box.append(node('p',g.Title+' — '+g.Action));for(const l of r.locations)box.append(node('small',(l.Candidate?'候选（关联未确认）：':'位置：')+l.Path+'；'+l.Evidence));$('#rows').append(box)}}
for(const id of ['search','state','rule'])$('#'+id).addEventListener('input',render);render();</script></html>`;
fs.writeFileSync(path.join(directory, 'review.html'), html);
console.log(JSON.stringify({ ...model.totals, newlyReady: model.newlyReady, defaultRuleImproved: improved.length }));
