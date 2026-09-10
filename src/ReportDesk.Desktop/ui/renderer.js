'use strict';
const $ = s => document.querySelector(s);
const api = window.reportDesk;
let reports = [], selected = null, detail = null, page = null, busy = false, dead = false, mode = 'all', sortColumn = -1, descending = false;
let selectedRows = new Set(), lookupPage = null, lookupTarget = null, settings = null;
const fields = new Map();
function element(tag, text, className) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (className) n.className = className; return n; }
async function call(method, args = {}) { const response = await api.call(method, args); if (!response.ok) { const error = new Error(response.message); error.cancelled = response.cancelled; throw error; } return response.data; }
function notice(title, text) { $('#notice-title').textContent = title; $('#notice-body').textContent = text; if (!$('#notice').open) $('#notice').showModal(); }
function progress(text) { $('#operation-status').textContent = text; $('#progress').setAttribute('aria-valuetext', text); }
function ready() {
  if (busy || dead) return;
  const available = !!detail;
  $('#star').disabled = $('#metadata-open').disabled = $('#sql').disabled = !available;
  $('#query').disabled = !available || detail.issues.length > 0 || detail.sources.length === 0;
  $('#source').disabled = !available || detail.sources.length === 0;
  $('#export').disabled = $('#result-filter').disabled = $('#filter-form button').disabled = !page;
  $('#copy').disabled = !page || !selectedRows.size;
  $('#previous').disabled = !page || page.offset === 0;
  $('#next').disabled = !page || page.offset + page.rows.length >= page.count;
  $('#conn-password').disabled = $('#conn-keep').checked;
  if (lookupPage) { $('#lookup-prev').disabled = lookupPage.offset === 0; $('#lookup-next').disabled = lookupPage.offset + lookupPage.rows.length >= lookupPage.count; }
}
async function task(label, action) {
  if (busy || dead) return;
  busy = true;
  const controls = [...document.querySelectorAll('button,input,select,textarea,summary')].map(node => [node, node.disabled]);
  controls.forEach(([node]) => { node.disabled = true; });
  $('#cancel').hidden = false; $('#cancel').disabled = false;
  $('#execution').className = 'execution running'; $('#progress').removeAttribute('aria-valuenow'); progress(label);
  $('.result').setAttribute('aria-busy', 'true');
  try { await action(); $('#execution').className = 'execution done'; $('#progress').setAttribute('aria-valuenow', '100'); if ($('#operation-status').textContent === label) progress('操作完成。'); }
  catch (error) {
    $('#execution').className = error.cancelled ? 'execution' : 'execution error';
    $('#progress').setAttribute('aria-valuenow', '0'); progress(error.cancelled ? '操作已取消，没有新的完整结果。' : error.message);
    if (!error.cancelled) notice('操作未完成', error.message);
  } finally {
    busy = false; controls.forEach(([node, disabled]) => { if (node.isConnected) node.disabled = disabled; });
    $('#cancel').hidden = true; $('.result').setAttribute('aria-busy', 'false'); ready();
    if (dead) document.querySelectorAll('button,input,select,textarea').forEach(n => { n.disabled = !n.hasAttribute('data-close'); });
  }
}
api.onProgress(progress);
api.onFatal(message => { dead = true; progress(message); notice('后台已退出', message); document.querySelectorAll('button,input,select,textarea').forEach(n => { n.disabled = !n.hasAttribute('data-close'); }); });
$('#cancel').onclick = async () => { $('#cancel').disabled = true; progress('已请求取消，正在等待当前操作释放资源…'); try { await call('cancel'); } catch { progress('取消请求失败，请等待后台返回。'); } };
window.addEventListener('error', () => { api.call('logClient'); progress('界面发生错误，请查看日志后重新启动。'); });
window.addEventListener('unhandledrejection', () => { api.call('logClient'); progress('界面操作失败，请查看日志后重新启动。'); });
function filteredReports() {
  const term = $('#report-search').value.trim().toLocaleLowerCase(); const category = $('#category').value;
  let list = reports.filter(r => (!category || r.category === category) && (r.name + ' ' + r.aliases + ' ' + r.notes + ' ' + r.path).toLocaleLowerCase().includes(term));
  if (mode === 'favorite') list = list.filter(r => r.favorite);
  if (mode === 'recent') list = list.filter(r => r.lastUsed);
  if (mode === 'pending') list = list.filter(r => r.issues.length);
  return list.sort(mode === 'recent' ? (a,b) => b.lastUsed.localeCompare(a.lastUsed) : (a,b) => Number(b.favorite)-Number(a.favorite) || a.name.localeCompare(b.name, 'zh-CN'));
}
function categories() { const chosen = $('#category').value; $('#category').replaceChildren(new Option('所有分类', '')); for (const c of [...new Set(reports.map(r => r.category))].sort()) $('#category').add(new Option(c, c)); $('#category').value = chosen; if ($('#category').selectedIndex < 0) $('#category').value = ''; }
function renderList() {
  const list = filteredReports(); $('#report-list').replaceChildren();
  for (const r of list) { const b = element('button', undefined, 'report-item' + (selected === r.id ? ' active' : '')); b.dataset.reportId = r.id; b.append(element('strong', (r.favorite ? '★ ' : '') + r.name), element('small', r.category + ' · ' + r.status)); b.onclick = () => task('正在切换报表…', () => select(r.id)); $('#report-list').append(b); }
  if (!list.length) $('#report-list').append(element('p', '没有匹配的报表', 'muted'));
  $('#library-count').textContent = list.length + ' 张可显示报表';
  document.querySelectorAll('[data-mode]').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
  for (const [id,m] of [['#all','all'],['#favorites','favorite'],['#recent','recent']]) $(id).classList.toggle('active', mode === m);
}
function clearResult() { page = null; selectedRows.clear(); sortColumn = -1; descending = false; $('#result-filter').value = ''; renderResult(); }
async function select(id, source = 0) {
  clearResult(); selected = id; detail = null; fields.clear(); $('#parameters').replaceChildren(); $('#source').replaceChildren(); $('#issues').textContent = '';
  if (!id) { await call('clear'); $('#report-title').textContent = '选择一张报表'; $('#report-status').textContent = '从左侧选择报表，或导入定义。'; renderList(); return; }
  try { detail = await call('select', { reportId: id, source }); }
  catch (e) { selected = null; $('#report-title').textContent = '请选择有效的报表'; throw e; }
  $('#report-title').textContent = detail.name; $('#report-status').textContent = detail.status + (detail.demo ? ' · 完全离线生成' : ' · ' + detail.path);
  $('#issues').textContent = detail.issues.length ? '待适配，不能查询：\n' + detail.issues.join('\n') : '';
  for (const source of detail.sources) $('#source').add(new Option(source.name, source.index)); $('#source').value = String(detail.source);
  $('#star').textContent = detail.favorite ? '★ 已收藏' : '☆ 收藏';
  for (const p of detail.parameters) {
    const label = element('label', p.label + (p.implicitValue ? '（已确认编码）' : ''));
    let input;
    if (p.kind === 'ComboBoxType') { input = element('select'); input.add(new Option('请选择', '')); input.options[0].disabled = true; if (p.hasAll) { const all = new Option('全部', p.allValue); all.dataset.choice = 'true'; input.add(all); } input.selectedIndex = 0; }
    else { input = element('input'); if (p.kind === 'DateTimeType') { const dateOnly = !/[Hhmsft]/.test(p.format); input.type = dateOnly ? 'date' : 'datetime-local'; input.step = '1'; input.value = dateOnly ? p.initial.slice(0,10) : p.initial; } else input.type = 'text'; }
    input.dataset.parameter = p.name; label.append(input); fields.set(p.name, { input, definition: p });
    if (p.kind === 'ComboBoxType' && p.lookup) { const row = element('div', undefined, 'field-actions'); const b = element('button', '加载选项'); b.onclick = () => task('正在加载选项…', () => loadLookup(p)); row.append(b); label.append(row); }
    $('#parameters').append(label);
  }
  renderList(); progress('当前报表尚未查询。');
}
async function refreshList(chooseFirst = false) { reports = await call('list'); categories(); renderList(); if (chooseFirst || !filteredReports().some(r => r.id === selected)) await select(filteredReports()[0]?.id ?? null); }
async function filterReports() { renderList(); if (!filteredReports().some(r => r.id === selected)) await select(filteredReports()[0]?.id ?? null); }
for (const b of document.querySelectorAll('[data-mode]')) b.onclick = () => task('正在切换列表…', async () => { mode = b.dataset.mode; await filterReports(); });
for (const [id,m] of [['#all','all'],['#favorites','favorite'],['#recent','recent']]) $(id).onclick = () => task('正在切换列表…', async () => { mode = m; await filterReports(); });
let searchTimer; $('#report-search').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => task('正在搜索报表…', filterReports), 250); };
$('#category').onchange = () => task('正在筛选分类…', filterReports);
$('#source').onchange = () => task('正在切换数据源…', () => select(selected, Number($('#source').value)));
function values() { const output = {}; for (const [name, field] of fields) output[name] = field.input.tagName === 'SELECT' && !field.input.selectedOptions[0]?.dataset.choice ? null : field.input.value; return output; }
$('#query').onclick = () => task('正在执行查询…', async () => { const args = { reportId: selected, source: Number($('#source').value), values: values() }; clearResult(); page = await call('query', args); renderResult(); reports = await call('list'); renderList(); progress('查询完成 · 共 ' + page.total + ' 行完整结果。'); });
function renderResult() {
  $('#table-wrap').replaceChildren(); $('#range').textContent = ''; $('#result-count').textContent = page ? '查询结果 · ' + page.count + ' 行' : '查询结果';
  if (!page) { $('#table-wrap').append(element('div', '尚未查询。填写条件后点击「开始查询」。', 'empty')); return; }
  const table = element('table'); const thead = element('thead'); const head = element('tr'); const selection = element('th'); const all = element('input'); all.type = 'checkbox'; all.setAttribute('aria-label','选择当前段全部行'); all.onchange = () => { selectedRows = new Set(all.checked ? page.rows.map((_,i) => i) : []); renderResult(); ready(); }; selection.append(all); head.append(selection);
  page.columns.forEach((column, index) => { const th = element('th'); if (!['String','DateTime'].includes(column.type)) th.className = 'numeric'; const b = element('button', column.name + (sortColumn === index ? descending ? ' ↓' : ' ↑' : ' ↕')); b.onclick = () => task('正在排序已加载结果…', async () => { descending = sortColumn === index ? !descending : false; sortColumn = index; await view(); }); th.append(b); head.append(th); });
  thead.append(head); table.append(thead); const body = element('tbody');
  page.rows.forEach((row,i) => { const tr = element('tr', undefined, selectedRows.has(i) ? 'selected' : ''); const td = element('td', undefined, 'select-cell'); const box = element('input'); box.type = 'checkbox'; box.checked = selectedRows.has(i); box.setAttribute('aria-label','选择第 ' + (page.offset+i+1) + ' 行'); box.onchange = () => { box.checked ? selectedRows.add(i) : selectedRows.delete(i); tr.classList.toggle('selected',box.checked); ready(); }; td.append(box); tr.append(td); row.forEach((value,c) => { const cell = element('td', value === null ? '' : value); if (!['String','DateTime'].includes(page.columns[c].type)) cell.classList.add('numeric'); if (value === null) { cell.classList.add('null'); cell.title = 'NULL'; } else cell.title = value; tr.append(cell); }); body.append(tr); });
  table.append(body); $('#table-wrap').append(table); if (!page.count) $('#table-wrap').append(element('div','没有匹配的结果。','empty'));
  $('#range').textContent = page.count ? `当前显示 ${page.offset+1}–${page.offset+page.rows.length} / ${page.count} 行（完整结果 ${page.total} 行）` : `筛选 0 行 / 完整结果 ${page.total} 行`;
}
async function view() { page = await call('view', { resultId: page.resultId, filter: $('#result-filter').value, sort: sortColumn, descending }); selectedRows.clear(); renderResult(); progress('筛选与排序已应用于完整结果。'); }
$('#filter-form').onsubmit = e => { e.preventDefault(); if (page) task('正在筛选结果…', view); };
for (const [id,delta] of [['#previous',-200],['#next',200]]) $(id).onclick = () => task('正在读取结果…', async () => { page = await call('page', { resultId: page.resultId, revision: page.revision, offset: Math.max(0,page.offset+delta) }); selectedRows.clear(); renderResult(); });
$('#export').onclick = () => task('请选择导出位置…', async () => { const output = await call('export', { resultId: page.resultId, revision: page.revision }); progress(output ? `已导出 ${output.count} 行当前筛选与排序结果。` : '已取消导出。'); });
$('#copy').onclick = () => task('正在复制…', async () => { const rows = [...selectedRows].sort((a,b)=>a-b).map(i=>page.rows[i]); const quote = x => { const text = x ?? ''; return /[\t\r\n"]/.test(text) ? '"' + text.replaceAll('"','""') + '"' : text; }; await call('copy', { text: [page.columns.map(c=>c.name),...rows].map(row=>row.map(quote).join('\t')).join('\r\n') }); progress('已复制选中行及列标题。'); });
function toggle() { $('#conditions').hidden = !$('#conditions').hidden; $('#toggle-conditions').textContent = $('#conditions').hidden ? '展开条件' : '收起条件'; $('#toggle-conditions').setAttribute('aria-expanded', String(!$('#conditions').hidden)); }
$('#toggle-conditions').onclick = $('#collapse').onclick = toggle;
$('#demo').onclick = () => task('正在加载演示定义…', async () => { reports = await call('demo'); mode = 'all'; $('#report-search').value = ''; categories(); $('#category').value = ''; await select('built-in-demo'); });
for (const [id,folder] of [['#import-file',false],['#import-folder',true]]) $(id).onclick = () => task('请选择报表定义…', async () => { const imported = await call('import', { folder }); if (!imported) { progress('已取消导入。'); return; } reports = imported.reports; categories(); await select(filteredReports()[0]?.id ?? null); const text = `导入 ${imported.imported} 张；待适配 ${imported.pending} 张；跳过 ${imported.skipped}；失败 ${imported.errors.length}。`; progress(text); if (imported.errors.length) notice('导入结果', text + '\n' + imported.errors.join('\n')); });
$('#star').onclick = () => task('正在保存收藏…', async () => { reports = await call('favorite', { reportId: selected, favorite: !detail.favorite }); detail.favorite = !detail.favorite; $('#star').textContent = detail.favorite ? '★ 已收藏' : '☆ 收藏'; await filterReports(); });
$('#sql').onclick = () => task('正在读取定义…', async () => { const def = await call('definition', { reportId: selected }); notice(def.title + ' · SQL（只读）', def.text); });
$('#metadata-open').onclick = () => { $('#meta-category').value = detail.category; $('#meta-aliases').value = detail.aliases; $('#meta-notes').value = detail.notes; $('#meta-verified').checked = detail.verified; $('#metadata').showModal(); };
$('#metadata-form').onsubmit = e => { e.preventDefault(); task('正在保存说明…', async () => { reports = await call('metadata', { reportId: selected, category: $('#meta-category').value, aliases: $('#meta-aliases').value, notes: $('#meta-notes').value, verified: $('#meta-verified').checked }); Object.assign(detail, { category: $('#meta-category').value, aliases: $('#meta-aliases').value, notes: $('#meta-notes').value, verified: $('#meta-verified').checked }); $('#metadata').close(); categories(); await filterReports(); }); };
$('#logs').onclick = () => task('正在打开日志目录…', () => call('openLogs'));
$('#help').onclick = () => notice('使用说明','左侧选择报表 → 选择数据源并填写条件 → 开始查询。\n下拉参数须先加载选项；隐含参数填写已确认的编码，不自动模拟 HIS 身份。\n结果分页只是展示分段，筛选、排序、Excel 导出作用于完整已加载结果，不重新查询。\n查询不设应用超时或行数上限，内存不足会失败。进度条表示正在工作，不表示已知完成百分比。\n报表显示由程序同目录 report-visibility.xml 配置，修改后重启；它不是数据库授权。');
for (const b of document.querySelectorAll('[data-close]')) b.onclick = () => { if (busy && b.dataset.close !== 'notice') return; $('#'+b.dataset.close).close(); if (b.dataset.close === 'connection') $('#conn-password').value = ''; };
for (const d of document.querySelectorAll('dialog')) d.addEventListener('cancel', e => { if (busy) e.preventDefault(); else if (d.id === 'connection') $('#conn-password').value = ''; });
function connectionMode() { const tns = $('#conn-mode').value === '1'; $('#tns-fields').hidden = !tns; $('#direct-fields').hidden = tns; $('#conn-password').disabled = $('#conn-keep').checked; }
function connectionArgs() { return { name: $('#conn-name').value, mode: Number($('#conn-mode').value), host: $('#conn-host').value, port: Number($('#conn-port').value), service: $('#conn-service').value, username: $('#conn-user').value, password: $('#conn-password').value, keepPassword: $('#conn-keep').checked, remember: $('#conn-remember').checked, tnsFile: $('#conn-tns').value, tnsAlias: $('#conn-alias').value }; }
function addTns(path) { if (![...$('#conn-tns').options].some(o=>o.value===path)) $('#conn-tns').add(new Option(path,path)); }
async function aliases(path, wanted = '') { $('#conn-alias').replaceChildren(new Option('请选择网络服务','')); if (!path) return; const names = await call('tnsAliases', { path }); for (const name of names) $('#conn-alias').add(new Option(name,name)); $('#conn-alias').value = wanted; if ($('#conn-alias').selectedIndex < 0) $('#conn-alias').value = ''; }
$('#settings-open').onclick = () => task('正在读取连接设置…', async () => { settings = await call('settings'); for (const [id,key] of [['name','name'],['host','host'],['port','port'],['service','service'],['user','username']]) $('#conn-'+id).value = settings[key]; $('#conn-mode').value = String(settings.mode); $('#conn-password').value = ''; $('#conn-keep').checked = settings.hasPassword; $('#conn-remember').checked = settings.remember; $('#conn-tns').replaceChildren(new Option('请选择文件','')); $('#conn-alias').replaceChildren(new Option('请选择网络服务','')); $('#test-status').textContent = ''; connectionMode(); $('#connection').showModal(); if (settings.tnsFile) { addTns(settings.tnsFile); $('#conn-tns').value=settings.tnsFile; await aliases(settings.tnsFile,settings.tnsAlias); } else { const paths=await call('discoverTns'); paths.forEach(addTns); if(paths.length===1){$('#conn-tns').value=paths[0];await aliases(paths[0]);} } });
$('#connection-form').addEventListener('input', () => { $('#test-status').textContent = ''; });
$('#conn-mode').onchange = connectionMode; $('#conn-keep').onchange = connectionMode;
$('#conn-tns').onchange = () => task('正在读取 TNS…', () => aliases($('#conn-tns').value));
$('#tns-pick').onclick = () => task('请选择 TNS 文件…', async () => { const file = await call('pickTns'); if (!file) return; addTns(file.path); $('#conn-tns').value = file.path; $('#conn-alias').replaceChildren(new Option('请选择网络服务','')); file.aliases.forEach(name=>$('#conn-alias').add(new Option(name,name))); $('#test-status').textContent=''; });
$('#tns-reload').onclick = () => task('正在刷新网络服务…', () => aliases($('#conn-tns').value, $('#conn-alias').value));
$('#tns-discover').onclick = () => task('正在查找 TNS 来源…', async () => { const paths = await call('discoverTns'); paths.forEach(addTns); if (!$('#conn-tns').value && paths.length===1) { $('#conn-tns').value=paths[0]; await aliases(paths[0]); } $('#test-status').textContent = paths.length ? `找到 ${paths.length} 个来源，请核对当前选择。` : '未找到来源，请浏览文件。'; });
$('#test-connection').onclick = () => task('正在测试连接…', async () => { $('#test-status').textContent = '正在测试连接，等待网络返回…'; try { const r = await call('testConnection', connectionArgs()); $('#test-status').textContent = '连接成功 · Oracle ' + r.version + '。未验证报表权限。'; } catch(e) { $('#test-status').textContent = '连接测试未成功。'; throw e; } });
$('#connection-form').onsubmit = e => { e.preventDefault(); task('正在保存连接设置…', async () => { settings=await call('saveSettings',connectionArgs()); $('#conn-password').value=''; $('#connection').close(); clearResult(); progress('连接设置已保存，未自动连接。'); }); };
async function loadLookup(p) { lookupTarget=p.name; lookupPage=await call('lookup',{reportId:selected,source:Number($('#source').value),parameter:p.name,values:values()}); $('#lookup-title').textContent=p.label+' · 选择编码'; $('#lookup-filter').value=''; renderLookup(); $('#lookup').showModal(); }
function renderLookup() { $('#lookup-list').replaceChildren(); for (const row of lookupPage.rows) { const b=element('button',(row[1]??'')+'  ['+(row[0]??'')+']'); b.onclick=()=>{const field=fields.get(lookupTarget); const option=new Option((row[1]??'')+' ['+(row[0]??'')+']',row[0]??'');option.dataset.choice='true';field.input.add(option);field.input.selectedIndex=field.input.options.length-1;$('#lookup').close();};$('#lookup-list').append(b);}$('#lookup-range').textContent=`${lookupPage.offset+1}–${lookupPage.offset+lookupPage.rows.length} / ${lookupPage.count}`; $('#lookup-prev').disabled=lookupPage.offset===0;$('#lookup-next').disabled=lookupPage.offset+lookupPage.rows.length>=lookupPage.count; }
async function lookupMove(offset){lookupPage=await call('lookupPage',{resultId:lookupPage.resultId,offset,filter:$('#lookup-filter').value});renderLookup();}
$('#lookup-prev').onclick=()=>task('正在读取选项…',()=>lookupMove(Math.max(0,lookupPage.offset-200)));$('#lookup-next').onclick=()=>task('正在读取选项…',()=>lookupMove(lookupPage.offset+200));$('#lookup-filter-form').onsubmit=e=>{e.preventDefault();task('正在查找选项…',()=>lookupMove(0));};
task('正在启动…',async()=>{const boot=await call('bootstrap');reports=boot.reports;settings=boot.settings;$('#demo').hidden=!boot.demoVisible;$('#connection-state').textContent=boot.offline?'离线验证模式':'尚未连接数据库';categories();renderList();await select(filteredReports()[0]?.id??null);progress('准备就绪。');});
