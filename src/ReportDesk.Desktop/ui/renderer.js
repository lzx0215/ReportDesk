'use strict';
const $ = s => document.querySelector(s);
const api = window.reportDesk;
let reports = [], selected = null, detail = null, page = null, busy = false, dead = false, mode = 'all', sortColumn = -1, descending = false;
let selectedRows = new Set(), lookupPage = null, lookupTarget = null, settings = null;
const fields = new Map();
function element(tag, text, className) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (className) n.className = className; return n; }
// Local presentation-only icons; report names and messages still use textContent.
const iconPaths = {
  reports: 'M4 3h16v18H4z M8 7v10 M12 7v10 M16 7v10',
  star: 'm12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9Z',
  clock: 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M12 6v6h5',
  play: 'm7 4 13 8-13 8Z', plus: 'M12 5v14 M5 12h14',
  folder: 'M3 8V5h7l2 3h9v12H3z',
  settings: 'm9 3-.5 2.5-2 1.2L4 6l-2 4 2 1.7v2.6L2 16l2 4 2.5-.7 2 1.2L9 23h6l.5-2.5 2-1.2 2.5.7 2-4-2-1.7v-2.6L22 10l-2-4-2.5.7-2-1.2L15 3Z M16 13a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  logs: 'M6 3h12v18H6z M9 7h6 M9 11h6 M9 15h6',
  search: 'M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0 M15 15l6 6',
  arrow: 'M4 12h16 m-6-6 6 6-6 6', external: 'M7 17 19 5 M10 5h9v9',
  up: 'M12 20V4 m-5 5 5-5 5 5', down: 'M12 4v16 m-5-5 5 5 5-5',
  sort: 'M8 20V4 m-4 4 4-4 4 4 M16 4v16 m-4-4 4 4 4-4',
  empty: 'M13 21H3V2h15v7 M6 6h9 M6 10h5 M6 14h3 M21 16a5 5 0 1 1-10 0 5 5 0 0 1 10 0 M20 20l3 3'
};
function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true'); svg.setAttribute('focusable', 'false');
  const path = document.createElementNS(svg.namespaceURI, 'path'); path.setAttribute('d', iconPaths[name]); svg.append(path); return svg;
}
for (const [id, name] of [['all','reports'],['demo','play'],['import-file','plus'],['import-folder','folder'],['settings-open','settings'],['logs','logs']]) $('#'+id).replaceChildren(icon(name));
for (const [id, text, name] of [['query','开始查询','arrow'],['export','导出 Excel','external'],['collapse','收起','up']]) $('#'+id).replaceChildren(document.createTextNode(text), icon(name));
for (const id of ['report-search','result-filter']) { const input = $('#'+id), wrapper = element('div', undefined, 'search-field'); input.before(wrapper); wrapper.append(icon('search'), input); }
async function call(method, args = {}) { const response = await api.call(method, args); if (!response.ok) { const error = new Error(response.message); error.cancelled = response.cancelled; throw error; } return response.data; }
function notice(title, text) { $('#notice-title').textContent = title; $('#notice-body').textContent = text; if (!$('#notice').open) $('#notice').showModal(); }
function progress(text) { $('#operation-status').textContent = text; $('#progress').setAttribute('aria-valuetext', text); }
function ready() {
  if (busy || dead) return;
  const available = !!detail;
  $('#metadata-open').disabled = $('#sql').disabled = !available;
  $('#related-xml').disabled = !available || detail.demo;
  $('#query').disabled = !available || (detail.selectedIssues || detail.issues).length > 0 || detail.sources.length === 0;
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
  const term = $('#report-search').value.trim().toLocaleLowerCase();
  let list = reports.filter(r => (r.name + ' ' + r.aliases + ' ' + r.notes + ' ' + r.path + ' ' + (r.locations || []).map(l => l.Path).join(' ')).toLocaleLowerCase().includes(term));
  return list.sort((a,b) => a.name.localeCompare(b.name, 'zh-CN'));
}
function renderList() {
  const list = filteredReports(); $('#report-list').replaceChildren();
  for (const r of list) { const b = element('button', undefined, 'report-item' + (selected === r.id ? ' active' : '')); b.dataset.reportId = r.id; const name = element('strong', r.name); b.append(name, element('small', r.status)); b.onclick = () => task('正在切换报表…', () => select(r.id)); $('#report-list').append(b); }
  if (!list.length) $('#report-list').append(element('p', '没有匹配的报表', 'muted'));
  $('#library-count').textContent = list.length + ' 张可显示报表';
  document.querySelectorAll('[data-mode]').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
  for (const [id,m] of [['#all','all']]) $(id).classList.toggle('active', mode === m);
}
function clearResult() { page = null; selectedRows.clear(); sortColumn = -1; descending = false; $('#result-filter').value = ''; renderResult(); }
async function select(id, source = 0) {
  $('#report-locations').replaceChildren(); $('#report-locations').hidden = true;
  clearResult(); selected = id; detail = null; fields.clear(); $('#parameters').replaceChildren(); $('#source').replaceChildren(); $('#issues').textContent = '';
  if (!id) { await call('clear'); $('#report-title').textContent = '选择一张报表'; $('#report-status').textContent = '选择 HIS / LIB 目录或 XML，直接读取报表。'; renderList(); return; }
  try { detail = await call('select', { reportId: id, source }); }
  catch (e) { selected = null; $('#report-title').textContent = '请选择有效的报表'; throw e; }
  $('#report-title').textContent = detail.name; $('#report-status').textContent = detail.status + (detail.demo ? ' · 完全离线生成' : ' · ' + detail.path);
  if (!detail.demo) {
    const box = $('#report-locations'); box.hidden = false;
    box.append(element('strong', 'HIS 位置'));
    for (const location of detail.locations || []) { const row = element('p', (location.Candidate ? '候选（XML 关联未核实）：' : '') + location.Path + (location.Active === false ? '〔菜单已停用〕' : '')); row.title = location.Evidence + '；' + location.Match; box.append(row); }
    if (!detail.locations?.length) box.append(element('p', '位置未确认（缺少菜单与报表的对应资料）'));
    else box.append(element('small', '已知位置，可能不完整；来源及匹配依据见“报表说明”。'));
    for (const warning of detail.locationWarnings || []) box.append(element('p', warning));
  }
  const blocking = detail.selectedIssues || detail.issues;
  $('#issues').textContent = blocking.length ? '当前数据源待适配，不能查询：\n' + blocking.join('\n') : (detail.issues.length ? '当前数据源可试查；其他数据源仍待适配，详情见报表说明。\n' : '') + (detail.adaptation || '');
  for (const source of detail.sources) $('#source').add(new Option(source.name, source.index)); $('#source').value = String(detail.source);
  for (const p of detail.parameters) {
    const label = element('label', p.label + (p.implicitValue ? '（已确认编码）' : ''));
    let input;
    if (p.multiple) { input=element('input'); input.readOnly=true; input.placeholder='请加载并选择编码'; input.dataset.selections='[]'; }
    else if (p.kind === 'ComboBoxType' || p.treeSelect) { input = element('select'); input.add(new Option('请选择', '')); input.options[0].disabled = true; const choices = [...(p.hasAll && !p.treeSelect ? [{Value:p.allValue,Label:p.allLabel || '全部'}] : []), ...(p.options || [])]; for (const choice of choices) { const option = new Option(choice.Label, choice.Value); option.dataset.choice = 'true'; input.add(option); } input.selectedIndex = 0; }
    else { input = element('input'); if (p.kind === 'DateTimeType') { const dateOnly = !/[Hhmsft]/.test(p.format); input.type = dateOnly ? 'date' : 'datetime-local'; input.step = '1'; input.value = dateOnly ? p.initial.slice(0,10) : p.initial; } else if (p.kind === 'CheckBoxType') { input.type = 'checkbox'; input.checked = p.initial === 'True'; } else { input.type = 'text'; input.value = p.initial || ''; } }
    input.dataset.parameter = p.name; label.append(input); fields.set(p.name, { input, definition: p });
    if ((p.kind === 'ComboBoxType' || p.treeSelect) && p.lookup) { const row = element('div', undefined, 'field-actions'); const b = element('button', '加载选项'); b.onclick = () => task('正在加载选项…', () => loadLookup(p)); row.append(b); label.append(row); }
    $('#parameters').append(label);
  }
  renderList(); progress('当前报表尚未查询。');
}
async function refreshList(chooseFirst = false) { reports = await call('list');  renderList(); if (chooseFirst || !filteredReports().some(r => r.id === selected)) await select(filteredReports()[0]?.id ?? null); }
async function filterReports() { renderList(); if (!filteredReports().some(r => r.id === selected)) await select(filteredReports()[0]?.id ?? null); }
for (const b of document.querySelectorAll('[data-mode]')) b.onclick = () => task('正在切换列表…', async () => { mode = b.dataset.mode; await filterReports(); });
for (const [id,m] of [['#all','all']]) $(id).onclick = () => task('正在切换列表…', async () => { mode = m; await filterReports(); });
let searchTimer; $('#report-search').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => task('正在搜索报表…', filterReports), 250); };
$('#source').onchange = () => task('正在切换数据源…', () => select(selected, Number($('#source').value)));
function values() { const output = {}; for (const [name, field] of fields) {
  if(field.definition.multiple) { const choices=JSON.parse(field.input.dataset.selections||'[]'); output[name]=choices.map(c=>c.value); output[name+'.Text']=choices.map(c=>c.label).join(','); }
  else { output[name] = field.input.type === 'checkbox' ? (field.input.checked ? 'True' : 'False') : field.input.tagName === 'SELECT' && !field.input.selectedOptions[0]?.dataset.choice ? null : field.input.value; output[name+'.Text']=field.input.selectedOptions?.[0]?.dataset.label ?? field.input.selectedOptions?.[0]?.textContent ?? field.input.value; }
} return output; }
$('#query').onclick = () => task('正在执行查询…', async () => { const args = { reportId: selected, source: Number($('#source').value), values: values() }; clearResult(); page = await call('query', args); renderResult(); reports = await call('list'); renderList(); progress('查询完成 · 共 ' + page.total + ' 行完整结果。'); });
function renderResult() {
  $('#table-wrap').replaceChildren(); $('#range').textContent = ''; $('#result-count').textContent = page ? '查询结果 · ' + page.count + ' 行' : '查询结果';
  if (!page) { const empty = element('div', undefined, 'empty'); empty.append(icon('empty'), element('p', '尚未查询。填写条件后点击「开始查询」。')); $('#table-wrap').append(empty); return; }
  const table = element('table'); const thead = element('thead'); const head = element('tr'); const selection = element('th'); const all = element('input'); all.type = 'checkbox'; all.setAttribute('aria-label','选择当前段全部行'); all.onchange = () => { selectedRows = new Set(all.checked ? page.rows.map((_,i) => i) : []); renderResult(); ready(); }; selection.append(all); head.append(selection);
  page.columns.forEach((column, index) => { const th = element('th'); if (!['String','DateTime'].includes(column.type)) th.className = 'numeric'; th.setAttribute('aria-sort', sortColumn === index ? descending ? 'descending' : 'ascending' : 'none'); const b = element('button', column.name); b.append(icon(sortColumn === index ? descending ? 'down' : 'up' : 'sort')); b.onclick = () => task('正在排序已加载结果…', async () => { descending = sortColumn === index ? !descending : false; sortColumn = index; await view(); }); th.append(b); head.append(th); });
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
$('#demo').onclick = () => task('正在加载演示定义…', async () => { reports = await call('demo'); mode = 'all'; $('#report-search').value = '';  await select('built-in-demo'); });
for (const [id,folder] of [['#import-file',false],['#import-folder',true]]) $(id).onclick = () => task('请选择报表定义…', async () => {
  const imported = await call('import', { folder }); if (!imported) { progress('已取消导入。'); return; }
  reports = imported.reports;  await select(filteredReports()[0]?.id ?? null);
  const text = `导入 ${imported.imported} 张；待适配 ${imported.pending} 张；发现版式 XML ${imported.layouts} 份；其他 XML ${imported.otherXml || 0} 份；仅条件/不完整定义 ${imported.incomplete?.length || 0} 份；警告/失败 ${imported.errors.length} 项。`;
  progress(text);
  notice('导入与匹配结果', text + `\n跳过独立导入 ${imported.skipped} 份。单文件导入时，版式/其他数量是同目录配套扫描数。\n显式路径匹配 ${imported.matched} 项；名称候选 ${imported.candidates} 项；缺失/冲突/拒绝 ${imported.unresolved} 项。\n版式、其他配置和无查询 SQL 的定义不作为独立报表显示；原文件保留。待适配的查询报表仍显示。匹配到配套文件不代表已支持交叉或映射规则。\n选择报表 → 报表说明，可查看适配建议、所有已知 HIS 位置及关联 XML。` + (imported.incomplete?.length ? '\n\n仅条件/不完整定义（需要完整查询定义或 HIS 数据获取实现）：\n' + imported.incomplete.join('\n') : '') + (imported.errors.length ? '\n\n' + imported.errors.join('\n') : ''));
});
$('#sql').onclick = () => task('正在读取定义…', async () => { const def = await call('definition', { reportId: selected }); notice(def.title + ' · SQL（只读）', def.text); });
$('#metadata-open').onclick = () => { $('#meta-locations').textContent = (detail.locations || []).map(l => l.Path + '\n来源：' + l.Evidence + '\n依据：' + l.Match).join('\n\n') || '位置未确认。'; $('#meta-locations').textContent += '\n' + (detail.locationWarnings || []).join('\n'); $('#meta-guidance').textContent = (detail.guidance || []).map(g => g.Title + '\n' + g.Action).join('\n\n') || '没有静态适配阻塞项；仍需在内网与 HIS 同条件核对。'; $('#metadata').showModal(); };
$('#related-xml').onclick = () => task('正在检查关联 XML…', async () => {
  const result = await call('relatedFiles', { reportId: selected });
  const statuses = { Matched: '已按显式路径匹配', Candidate: '名称候选，关联待核对', Ambiguous: '多个候选，未自动选择', Missing: '未找到', Rejected: '引用超出扫描范围或格式不支持' };
  const lines = result.files.map(f => `${f.Role}\n${statuses[f.Status]}\n配置/约定：${f.Reference || '未填写'}${f.Paths.length ? '\n' + f.Paths.join('\n') : ''}`);
  progress('关联 XML 检查完成，匹配信息见弹窗。');
  notice('关联 XML（只读检查）', `扫描目录：${result.root}\n查询定义：${result.query}\n\n` + (lines.join('\n\n') || '没有识别到可匹配的版式引用。') + '\n\n配套文件仅识别关联，不执行版式中的映射或交叉规则；原文件保持不变。' + (result.warnings.length ? '\n\n扫描警告：\n' + result.warnings.join('\n') : ''));
});
$('#logs').onclick = () => task('正在打开日志目录…', () => call('openLogs'));
$('#help').onclick = () => notice('使用说明','首次选择 HIS / LIB 目录或 XML 后自动记住路径，下次打开自动读取；请保持原路径可访问。连接成功后自动保存配置，默认加密保存密码，也可取消勾选。\n左侧选择报表 → 选择数据源并填写条件 → 开始查询。\n下拉参数须先加载选项；隐含参数填写已确认的编码，不自动模拟 HIS 身份。\n结果分页只是展示分段，筛选、排序、Excel 导出作用于完整已加载结果，不重新查询。\n查询不设应用超时或行数上限，内存不足会失败。进度条表示正在工作，不表示已知完成百分比。\n报表显示由程序同目录 report-visibility.xml 配置，修改后重启；它不是数据库授权。');
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
$('#test-connection').onclick = () => task('正在测试连接…', async () => { $('#test-status').textContent = '正在测试连接，等待网络返回…'; try { const r = await call('testConnection', connectionArgs()); settings = r.settings; $('#conn-password').value = ''; $('#conn-keep').checked = settings.hasPassword; $('#conn-remember').checked = settings.remember; connectionMode(); clearResult(); $('#connection-state').textContent = '连接已验证'; $('#test-status').textContent = '连接成功 · Oracle ' + r.version + (settings.remember ? '。配置已保存，密码已加密保存，下次无需重新输入。' : '。配置已保存，密码仅在本次打开期间使用。') + '未验证报表权限。'; } catch(e) { $('#test-status').textContent = e.message; throw e; } });
$('#connection-form').onsubmit = e => { e.preventDefault(); task('正在保存连接设置…', async () => { settings=await call('saveSettings',connectionArgs()); $('#conn-password').value=''; $('#connection').close(); clearResult(); progress('连接设置已保存，未自动连接。'); }); };
async function loadLookup(p) { lookupTarget=p.name; lookupPage=await call('lookup',{reportId:selected,source:Number($('#source').value),parameter:p.name,values:values()}); $('#lookup-title').textContent=p.label+' · 选择编码'; $('#lookup-filter').value=''; $('#lookup-all').hidden=!p.multiple || !p.hasAll; $('#lookup-clear').hidden=!p.multiple; renderLookup(); $('#lookup').showModal(); }
function setChoices(choices) { const input=fields.get(lookupTarget).input; input.dataset.selections=JSON.stringify(choices); input.value=choices.length ? `已选择 ${choices.length} 项` : ''; }
function renderLookup() { $('#lookup-list').replaceChildren(); const field=fields.get(lookupTarget); const choices=field.definition.multiple?JSON.parse(field.input.dataset.selections||'[]'):[];
  for (const row of lookupPage.rows) { const value=row[0]??'',label=row[1]??''; const b=element('button',label+'  ['+value+']');
    if(field.definition.multiple) { b.setAttribute('aria-pressed',String(choices.some(c=>c.value===value))); b.onclick=()=>{const current=JSON.parse(field.input.dataset.selections||'[]');setChoices(current.some(c=>c.value===value)?current.filter(c=>c.value!==value):[...current,{value,label}]);renderLookup();}; }
    else b.onclick=()=>{const option=new Option(label+' ['+value+']',value);option.dataset.choice='true';option.dataset.label=label;field.input.add(option);field.input.selectedIndex=field.input.options.length-1;$('#lookup').close();};
    $('#lookup-list').append(b);
  } $('#lookup-range').textContent=`${lookupPage.offset+1}–${lookupPage.offset+lookupPage.rows.length} / ${lookupPage.count}`; $('#lookup-prev').disabled=lookupPage.offset===0;$('#lookup-next').disabled=lookupPage.offset+lookupPage.rows.length>=lookupPage.count;
}
$('#lookup-all').onclick=()=>task('正在选择全部编码…',async()=>{setChoices(await call('lookupAll',{resultId:lookupPage.resultId}));renderLookup();});
$('#lookup-clear').onclick=()=>{setChoices([]);renderLookup();};
async function lookupMove(offset){lookupPage=await call('lookupPage',{resultId:lookupPage.resultId,offset,filter:$('#lookup-filter').value});renderLookup();}
$('#lookup-prev').onclick=()=>task('正在读取选项…',()=>lookupMove(Math.max(0,lookupPage.offset-200)));$('#lookup-next').onclick=()=>task('正在读取选项…',()=>lookupMove(lookupPage.offset+200));$('#lookup-filter-form').onsubmit=e=>{e.preventDefault();task('正在查找选项…',()=>lookupMove(0));};
task('正在启动…',async()=>{const boot=await call('bootstrap');reports=boot.reports;settings=boot.settings;$('#demo').hidden=!boot.demoVisible;$('#connection-state').textContent=boot.offline?'离线验证模式':'尚未连接数据库';renderList();await select(filteredReports()[0]?.id??null);progress('准备就绪。');if(boot.warnings?.length)notice('启动提示',boot.warnings.join('\n\n'));});

$('#recheck').onclick = () => task('正在重新检查适配…', async () => {
  const current = selected, source = detail?.source || 0;
  const result = await call('recheck'); reports = result.reports;
  await select(filteredReports().some(r => r.id === current) ? current : filteredReports()[0]?.id ?? null, source);
  notice('重新检查完成', `已重新检查 ${result.checkedCount} 张；不完整定义 ${result.incomplete} 张；未更新 ${result.errors.length} 张。\n已从原 XML 刷新本次打开期间的报表定义。` + (result.errors.length ? '\n\n' + result.errors.join('\n') : ''));
});
