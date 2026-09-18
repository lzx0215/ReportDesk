'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { SqlEditorMain } = require('../../src/ReportDesk.Desktop/sql-editor-main.cjs');
const fixture = () => ({ reportId: 'report', title: '报表', path: 'C:\\reports\\query.xml', token: 'opened', hash: 'hash', demo: false, stale: false, sources: [
  { index: 0, queryIndex: -1, name: '空定义', kind: 'MainReportUsing', sql: '', editable: false },
  { index: 1, queryIndex: 0, name: 'same', kind: 'MainReportUsing', sql: 'select 1 from dual', editable: true },
  { index: 2, queryIndex: 1, name: 'same', kind: 'DetailReportUsing', sql: 'select 2 from dual', editable: true },
  { index: 3, queryIndex: 2, name: 'options', kind: 'ConditionUsing', sql: 'select 3 from dual', editable: false }
] });
const layoutFixture = () => ({ previewToken: 'preview', queryPath: fixture().path, layoutPath: 'C:\\reports\\layout.xml', matchNotice: '核对路径', message: '确认后保存', columns: [
  { index: 0, field: 'A', header: '旧表头', width: 100, added: false, hidden: false },
  { index: 1, field: '新增', header: '新增', width: 120, added: true, hidden: false }
] });
function mainHarness() {
  const calls = [], prompts = []; let response = 0;
  const dialog = {
    showMessageBox: async (_window, options) => { prompts.push(options); return { response }; },
    showMessageBoxSync: (_window, options) => { prompts.push(options); return response; }
  };
  const revealed = [];
  const files = { statSync: () => ({ isFile: () => true }) };
  const controller = new SqlEditorMain(dialog, { showItemInFolder: file => revealed.push(file) }, files);
  const bridge = { call: async (method, args) => {
    calls.push({ method, args });
    if (method === 'sqlEditorOpen') return fixture();
    return { saved: true, editor: { ...fixture(), token: 'saved' }, savedPath: fixture().path };
  } };
  return { controller, bridge, calls, prompts, revealed, files, choose: value => { response = value; } };
}

test('layout preview/save use trusted paths and validate preview token and settings', async () => {
  const h = mainHarness();
  const original = h.bridge.call;
  h.bridge.call = async (method, args) => method === 'layoutPreview' ? layoutFixture() : original(method, args);
  await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  const payload = { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 1 A, 2 新增 from dual', values: {} };
  await h.controller.handle('layoutPreview', payload, {}, h.bridge); h.controller.setDirty({ dirty: true });
  const save = { ...payload, previewToken: 'preview', columns: layoutFixture().columns, path: 'FAKE', layoutPath: 'FAKE' };
  for (const patch of [{ previewToken: 'stale' }, { sql: 'changed' }, { sourceIndex: 1 }, { columns: [] }, { columns: [{ index: 0, header: '', width: 0 }, save.columns[1]] }])
    await assert.rejects(h.controller.handle('layoutSave', { ...save, ...patch }, {}, h.bridge));
  assert.equal(h.prompts.length, 0);
  assert.equal(await h.controller.handle('layoutSave', save, {}, h.bridge), null);
  assert.equal(h.controller.dirty, true); assert.ok(h.controller.layout);
  h.choose(1); await h.controller.handle('layoutSave', save, {}, h.bridge);
  assert.match(h.prompts[1].detail, /C:\\reports\\layout.xml/); assert.ok(!h.prompts[1].detail.includes('FAKE'));
  assert.equal(h.calls.at(-1).args.path, undefined); assert.equal(h.calls.at(-1).args.layoutPath, undefined);
  assert.equal(h.controller.dirty, false); assert.equal(h.controller.layout, null);
});

test('layout write failure invalidates preview but keeps dirty draft', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  const payload = { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 1 A, 2 新增 from dual' };
  h.bridge.call = async method => { if (method === 'layoutPreview') return layoutFixture(); throw new Error('conflict'); };
  await h.controller.handle('layoutPreview', payload, {}, h.bridge); h.controller.setDirty({ dirty: true }); h.choose(1);
  await assert.rejects(h.controller.handle('layoutSave', { ...payload, previewToken: 'preview', columns: layoutFixture().columns }, {}, h.bridge));
  assert.equal(h.controller.layout, null); assert.equal(h.controller.dirty, true);
});

test('reveal uses the Host source, ignores injected paths and preserves a dirty draft', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  h.controller.setDirty({ dirty: true });
  await h.controller.handle('sqlEditorReveal', { reportId: 'report', token: 'opened', path: 'C:\\unrelated.xml' }, {}, h.bridge);
  assert.deepEqual(h.revealed, [fixture().path]);
  assert.equal(h.controller.dirty, true); assert.equal(h.calls.length, 1); assert.equal(h.prompts.length, 0);
});

test('reveal rejects stale sessions, demos, missing files and directories', async () => {
  const h = mainHarness(); const args = { reportId: 'report', token: 'opened' };
  await assert.rejects(h.controller.handle('sqlEditorReveal', args, {}, h.bridge));
  await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  for (const patch of [{ token: 'stale' }, { reportId: 'another' }])
    await assert.rejects(h.controller.handle('sqlEditorReveal', { ...args, ...patch }, {}, h.bridge));
  h.controller.target.demo = true;
  await assert.rejects(h.controller.handle('sqlEditorReveal', args, {}, h.bridge));
  h.controller.target.demo = false;
  h.files.statSync = () => ({ isFile: () => false });
  await assert.rejects(h.controller.handle('sqlEditorReveal', args, {}, h.bridge), /不可访问/);
  h.files.statSync = () => { throw new Error('missing'); };
  await assert.rejects(h.controller.handle('sqlEditorReveal', args, {}, h.bridge), /不可访问/);
  assert.deepEqual(h.revealed, []);
});
test('opening reads a server-selected source, never a client file path', async () => {
  const h = mainHarness();
  await h.controller.handle('sqlEditorOpen', { reportId: 'report', path: 'C:\\other.xml' }, {}, h.bridge);
  assert.deepEqual(h.calls[0], { method: 'sqlEditorOpen', args: { reportId: 'report' } });
});
test('cancelled confirmation does not save and retains dirty state', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  h.controller.setDirty({ dirty: true });
  const saved = await h.controller.handle('sqlEditorSave', { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 4 from dual' }, {}, h.bridge);
  assert.equal(saved, null); assert.equal(h.calls.length, 1); assert.equal(h.controller.dirty, true);
});
test('save prompt uses the Host path; approved payload contains no client path', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge); h.choose(1);
  h.controller.setDirty({ dirty: true });
  await h.controller.handle('sqlEditorSave', { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 4 from dual', path: 'FAKE' }, {}, h.bridge);
  assert.match(h.prompts[0].detail, /C:\\reports\\query.xml/); assert.ok(!h.prompts[0].detail.includes('FAKE'));
  assert.match(h.prompts[0].message, /不生成备份/); assert.equal(h.prompts[0].buttons[1], '覆盖并保存');
  assert.equal(h.calls[1].args.sourceIndex, 2); assert.equal(h.calls[1].args.path, undefined);
  assert.equal(h.controller.dirty, false); assert.equal(h.controller.target.token, 'saved');
});
test('invalid token, readonly source, oversized or empty SQL rejected before confirmation', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge);
  const args = { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 4 from dual' };
  for (const patch of [{ token: 'stale' }, { sourceIndex: 3 }, { sourceIndex: 9 }, { sql: '' }, { sql: 'x'.repeat(1024 * 1024 + 1) }])
    await assert.rejects(h.controller.handle('sqlEditorSave', { ...args, ...patch }, {}, h.bridge));
  assert.equal(h.prompts.length, 0); assert.equal(h.calls.length, 1);
});
test('close and discard default to preserving draft', async () => {
  const h = mainHarness(); h.controller.setDirty({ dirty: true });
  assert.equal(h.controller.allowClose({}), false);
  assert.equal((await h.controller.handle('sqlEditorDiscard', {}, {}, h.bridge)).discard, false);
  h.choose(1); assert.equal(h.controller.allowClose({}), true); assert.equal(h.controller.dirty, false);
  assert.equal(h.prompts[0].defaultId, 0); assert.equal(h.prompts[0].cancelId, 0);
});
test('opening another document cannot silently replace a dirty draft', async () => {
  const h = mainHarness(); h.controller.setDirty({ dirty: true });
  await assert.rejects(h.controller.handle('sqlEditorOpen', { reportId: 'other' }, {}, h.bridge)); assert.equal(h.calls.length, 0);
});
test('save failure keeps the editor and dirty draft', async () => {
  const h = mainHarness(); await h.controller.handle('sqlEditorOpen', { reportId: 'report' }, {}, h.bridge); h.choose(1);
  h.controller.setDirty({ dirty: true }); h.bridge.call = async () => { throw new Error('conflict'); };
  await assert.rejects(h.controller.handle('sqlEditorSave', { reportId: 'report', token: 'opened', sourceIndex: 2, sql: 'select 4 from dual' }, {}, h.bridge));
  assert.equal(h.controller.dirty, true); assert.equal(h.controller.target.token, 'opened');
});

function uiHarness() {
  const nodes = new Map(), calls = [], failures = [];
  class Element {
    constructor(tag) { this.tagName = tag.toUpperCase(); this.children = []; this.disabled = false; this.open = false; this.events = {}; this._value = ''; }
    set id(value) { this._id = value; nodes.set(value, this); } get id() { return this._id; }
    set value(value) { this._value = this.tagName === 'TEXTAREA' ? String(value).replace(/\r\n?/g, '\n') : String(value); } get value() { return this._value; }
    append(...items) { this.children.push(...items); }
    replaceChildren(...items) { this.children = items; }
    setAttribute(key, value) { this[key] = value; }
    addEventListener(type, fn) { this.events[type] = fn; }
    showModal() { this.open = true; } close() { this.open = false; } focus() { this.focused = true; }
  }
  const button = new Element('button'); button.id = 'sql';
  const h = { nodes, calls, failures, data: fixture(), discard: false, saveCancel: false, saveError: false, refreshError: false, clears: 0, refreshes: [] };
  const context = {
    document: { createElement: tag => new Element(tag), body: new Element('body') },
    $: selector => nodes.get(selector.slice(1)), busy: false, dead: false, selected: 'report',
    currentReportSession: { source: 1 }, reports: [], detail: {}, values: () => ({ date: '2026-01-01' }),
    call: async (method, args = {}) => {
      calls.push({ method, args });
      if (method === 'sqlEditorOpen') return structuredClone(h.data);
      if (method === 'sqlEditorDiscard') return { discard: h.discard };
      if (method === 'sqlEditorSave') {
        if (h.saveError) throw new Error('原 XML 已被修改');
        if (h.saveCancel) return null;
        h.data.sources.find(s => s.index === args.sourceIndex).sql = args.sql; h.data.token = 'saved';
        return { saved: true, reloaded: true, editor: structuredClone(h.data), savedPath: h.data.path, message: '原 XML 已保存并重新加载' };
      }
      if (method === 'sqlEditorCheck') return { passed: true, missing: [], message: '静态检查通过' };
      if (method === 'layoutPreview') return { ...layoutFixture(), reconciled: !!h.reconciled,
        columns: layoutFixture().columns.map(c => ({ ...c, originalIndex: c.added ? -1 : c.index })) };
      if (method === 'layoutSave') {
        if (h.saveError) throw new Error('模板已被修改');
        if (h.saveCancel) return null;
        h.data.sources.find(s => s.index === args.sourceIndex).sql = args.sql; h.data.token = 'saved';
        return { saved: true, reloaded: true, editor: structuredClone(h.data), savedPath: h.data.path, layoutPath: layoutFixture().layoutPath, message: '两份文件已保存并回读核对' };
      }
      if (method === 'list') return [];
      return {};
    },
    clearResult: () => { h.clears++; },
    select: async (id, source) => { h.refreshes.push({ id, source }); if (h.refreshError) throw new Error('refresh failed'); },
    progress: text => { h.progress = text; }
  };
  context.task = async (_label, action) => {
    context.busy = true;
    try { await action(); } catch (error) { failures.push(error); } finally { context.busy = false; }
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/ReportDesk.Desktop/ui/sql-editor.js'), 'utf8'), context);
  h.context = context;
  h.open = () => nodes.get('sql').onclick();
  h.edit = async value => { const input = nodes.get('sql-editor-input'); input.value = value; input.oninput(); await Promise.resolve(); };
  return h;
}
test('close emblem has an accessible name and preserves unsaved-draft confirmation', async () => {
  const h = uiHarness(); await h.open();
  const close = h.nodes.get('sql-editor-close');
  assert.equal(close['aria-label'], '关闭'); assert.equal(close.title, '关闭');
  assert.equal(close.children[0].className, 'close-emblem');
  assert.equal(close.children[0]['aria-hidden'], 'true');
  await h.edit('select 42 from dual'); await close.onclick();
  assert.equal(h.nodes.get('sql-editor').open, true);
  h.discard = true; await close.onclick();
  assert.equal(h.nodes.get('sql-editor').open, false);
});
test('editor places close in header, save after sync, and file path below toolbar', async () => {
  const h = uiHarness(); await h.open();
  const children = h.nodes.get('sql-editor').children;
  assert.equal(children[0].className, 'sql-editor-header');
  assert.deepEqual(children[0].children.map(n => n.id), ['sql-editor-title', 'sql-editor-close']);
  assert.equal(children[1].className, 'sql-editor-selector');
  assert.equal(children[2].id, 'sql-editor-path'); assert.equal(children[3].id, 'sql-editor-input');
  assert.deepEqual(children[1].children[1].children.map(n => n.id), [
    'sql-editor-copy', 'sql-editor-check', 'sql-editor-reload', 'sql-editor-reveal', 'sql-editor-sync', 'sql-editor-save'
  ]);
});
test('editor uses XML ordinal rather than imported query ordinal', async () => {
  const h = uiHarness(); await h.open();
  assert.equal(h.nodes.get('sql-editor-source').value, '2'); assert.equal(h.nodes.get('sql-editor-input').value, 'select 2 from dual');
  assert.equal(h.nodes.get('sql-editor-save').disabled, true);
});

test('reveal from editor keeps text and dirty state without querying or saving', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 99 from dual');
  await h.nodes.get('sql-editor-reveal').onclick();
  assert.equal(h.nodes.get('sql-editor-input').value, 'select 99 from dual');
  assert.equal(h.nodes.get('sql-editor-save').disabled, false);
  const request = h.calls.find(c => c.method === 'sqlEditorReveal');
  assert.equal(request.args.reportId, 'report'); assert.equal(request.args.token, 'opened'); assert.equal(request.args.path, undefined);
  assert.ok(!h.calls.some(c => ['query', 'sqlEditorSave', 'reloadReport'].includes(c.method)));
  assert.equal(h.clears, 0);
});
test('copy includes current draft only, without report headings or parameter substitution', async () => {
  const h = uiHarness(); await h.open(); const sql = "select '&科室' as NAME\nfrom dual"; await h.edit(sql);
  await h.nodes.get('sql-editor-copy').onclick();
  assert.equal(h.calls.find(c => c.method === 'copy').args.text, sql);
});
test('cancelling source switch preserves unsaved text and source', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 9 from dual');
  h.nodes.get('sql-editor-source').value = '1'; await h.nodes.get('sql-editor-source').onchange();
  assert.equal(h.nodes.get('sql-editor-input').value, 'select 9 from dual'); assert.equal(h.nodes.get('sql-editor-source').value, '2');
});
test('confirmed source switch selects readonly condition safely', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 9 from dual'); h.discard = true;
  h.nodes.get('sql-editor-source').value = '3'; await h.nodes.get('sql-editor-source').onchange();
  assert.equal(h.nodes.get('sql-editor-input').readOnly, true); assert.equal(h.nodes.get('sql-editor-save').disabled, true);
});
test('save clears old result, refreshes one report and never executes query', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 8 from dual'); await h.nodes.get('sql-editor-save').onclick();
  assert.equal(h.calls.find(c => c.method === 'sqlEditorSave').args.sourceIndex, 2);
  assert.equal(h.clears, 1); assert.deepEqual(h.refreshes, [{ id: 'report', source: 1 }]);
  assert.equal(h.nodes.get('sql-editor-save').disabled, true); assert.ok(h.nodes.get('sql-editor-status').textContent.includes(h.data.path));
  assert.ok(!h.nodes.get('sql-editor-status').textContent.includes('.bak'));
  assert.ok(!h.calls.some(c => ['query', 'testConnection', 'import', 'recheck'].includes(c.method)));
});
test('failed or cancelled save preserves draft and old baseline', async () => {
  for (const flag of ['saveError', 'saveCancel']) {
    const h = uiHarness(); await h.open(); await h.edit('select 8 from dual'); h[flag] = true;
    await h.nodes.get('sql-editor-save').onclick();
    assert.equal(h.nodes.get('sql-editor-input').value, 'select 8 from dual'); assert.equal(h.nodes.get('sql-editor-save').disabled, false);
    assert.equal(h.clears, 0);
  }
});
test('UI refresh failure after successful save is not reported as a failed write', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 8 from dual'); h.refreshError = true;
  await h.nodes.get('sql-editor-save').onclick();
  assert.equal(h.nodes.get('sql-editor-save').disabled, true); assert.match(h.nodes.get('sql-editor-status').textContent, /文件已保存/);
  assert.equal(h.failures.length, 0);
});
test('Escape respects unsaved confirmation and accepted close clears draft', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 8 from dual');
  await h.nodes.get('sql-editor-close').onclick(); assert.equal(h.nodes.get('sql-editor').open, true);
  h.discard = true; await h.nodes.get('sql-editor-close').onclick();
  assert.equal(h.nodes.get('sql-editor').open, false); assert.equal(h.nodes.get('sql-editor-input').value, '');
});
test('oversized input is retained, not silently truncated, and cannot save', async () => {
  const h = uiHarness(); await h.open(); const text = 'x'.repeat(1024 * 1024 + 1); await h.edit(text);
  assert.equal(h.nodes.get('sql-editor-input').value.length, text.length); assert.equal(h.nodes.get('sql-editor-save').disabled, true);
});
test('new assets are local and existing IPC origin validation still precedes editor handling', () => {
  const main = fs.readFileSync(path.join(__dirname, '../../src/ReportDesk.Desktop/main.cjs'), 'utf8');
  const html = fs.readFileSync(path.join(__dirname, '../../src/ReportDesk.Desktop/ui/index.html'), 'utf8');
  assert.ok(main.indexOf("event.senderFrame.url !== ui") < main.indexOf("method === 'sqlEditorDirty'"));
  assert.ok(main.includes("'/sql-editor.js'") && main.includes("'/sql-editor.css'"));
  assert.ok(html.indexOf('src="renderer.js"') < html.indexOf('src="sql-editor.js"'));
  assert.ok(html.includes("script-src 'self'") && !html.includes('unsafe-inline'));
});

test('layout UI previews aliases and returns without discarding SQL', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 1 A, 2 新增 from dual');
  await h.nodes.get('sql-editor-sync').onclick();
  assert.equal(h.nodes.get('layout-editor').open, true);
  assert.match(h.nodes.get('layout-editor-paths').textContent, /layout.xml/);
  const body = h.nodes.get('layout-editor-table').children[1];
  assert.equal(body.children[1].children[2].children[0].value, '新增');
  assert.equal(h.calls.find(c => c.method === 'layoutPreview').args.values.date, '2026-01-01');
  await h.nodes.get('layout-editor-cancel').onclick();
  assert.equal(h.nodes.get('layout-editor').open, false);
  assert.equal(h.nodes.get('sql-editor-input').value, 'select 1 A, 2 新增 from dual');
  assert.equal(h.calls.some(c => c.method === 'layoutSave'), false);
});

test('saved SQL can open recovery preview while ordinary SQL save is disabled', async () => {
  const h = uiHarness(); h.reconciled = true; await h.open();
  const before = h.nodes.get('sql-editor-input').value;
  assert.equal(h.nodes.get('sql-editor-save').disabled, true);
  assert.equal(h.nodes.get('sql-editor-sync').disabled, false);
  await h.nodes.get('sql-editor-sync').onclick();
  assert.match(h.nodes.get('layout-editor-title').textContent, /SQL 已保存/);
  const body = h.nodes.get('layout-editor-table').children[1];
  assert.equal(body.children[0].children[4].textContent, '原第 1 列');
  assert.equal(body.children[1].children[4].textContent, '新增');
  await h.nodes.get('layout-editor-save').onclick();
  assert.equal(h.calls.find(c => c.method === 'layoutSave').args.sql, before);
  assert.equal(h.nodes.get('sql-editor-input').value, before);
  assert.equal(h.nodes.get('layout-editor').open, false);
});

test('layout UI validates width and preserves preview after confirmation cancellation', async () => {
  const h = uiHarness(); await h.open(); await h.nodes.get('sql-editor-sync').onclick();
  const width = h.nodes.get('layout-editor-table').children[1].children[1].children[3].children[0];
  width.value = '0'; await h.nodes.get('layout-editor-save').onclick();
  assert.equal(h.calls.some(c => c.method === 'layoutSave'), false);
  width.value = '180'; h.saveCancel = true; await h.nodes.get('layout-editor-save').onclick();
  assert.equal(h.nodes.get('layout-editor').open, true); assert.match(h.nodes.get('layout-editor-status').textContent, /已取消保存/);
});

test('layout UI successful save reloads report and does not execute a data query', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 1 A, 2 新增 from dual'); await h.nodes.get('sql-editor-sync').onclick();
  await h.nodes.get('layout-editor-save').onclick();
  assert.equal(h.nodes.get('layout-editor').open, false); assert.equal(h.nodes.get('sql-editor-save').disabled, true);
  assert.match(h.nodes.get('sql-editor-status').textContent, /两份文件已保存/);
  assert.equal(h.calls.some(c => c.method === 'query'), false); assert.equal(h.failures.length, 0);
});

test('layout UI failed write invalidates preview and keeps SQL draft', async () => {
  const h = uiHarness(); await h.open(); await h.edit('select 1 A, 2 新增 from dual'); await h.nodes.get('sql-editor-sync').onclick();
  h.saveError = true; await h.nodes.get('layout-editor-save').onclick();
  assert.equal(h.nodes.get('layout-editor-save').disabled, true); assert.equal(h.nodes.get('layout-editor').open, true);
  assert.equal(h.nodes.get('sql-editor-input').value, 'select 1 A, 2 新增 from dual');
  assert.match(h.nodes.get('layout-editor-status').textContent, /重新同步/);
});
