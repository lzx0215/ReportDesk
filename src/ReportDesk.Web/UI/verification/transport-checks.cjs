'use strict';
// Protocol-only envelopes, no Oracle results or database access.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../transport.js'), 'utf8');
function harness(handler, options = {}) {
  const requests = [], events = {}, downloads = [];
  const document = {
    currentScript: { src: 'http://localhost' + (options.prefix || '/') + 'assets/transport.js' },
    body: { append() {} }, querySelector: () => null,
    createElement: () => ({ click() { downloads.push(this.href); }, remove() {} })
  };
  const window = { confirm: () => options.confirm !== false, prompt: () => options.prompt ?? '', addEventListener: (name, fn) => { events[name] = fn; } };
  const context = { window, document, location: { pathname: options.route || '/' }, crypto: webcrypto, URL, Uint8Array,
    navigator: { clipboard: { writeText: async text => { context.copied = text; } } }, setTimeout: fn => setImmediate(fn),
    fetch: async (url, init) => {
      const item = { url, ...init, parsed: init.body ? JSON.parse(init.body) : undefined }; requests.push(item);
      const reply = url.endsWith('api/session') ? { csrf: 'test-csrf', admin: options.admin === true, offline: true, idleMinutes: 23 } : await handler(item, requests);
      return { ok: !reply.http || reply.http < 400, status: reply.http || 200, json: async () => reply.http ? reply.data : reply };
    }
  };
  vm.runInNewContext(source, context);
  return { api: window.reportDesk, requests, events, context, downloads };
}
async function run() {
  const sync = harness(() => ({ ok: true, data: { title: 'report', textParameterNames: ['科室'], text: 'PRIVATE_SQL' } }));
  assert.equal((await sync.api.ready).idleMinutes, 23);
  const def = await sync.api.call('definition'); assert.equal(def.ok, true); assert.equal(def.data.text, undefined);
  assert.equal(sync.requests[1].url, 'http://localhost/api/definition');
  assert.equal(sync.requests[1].headers['X-ReportDesk-CSRF'], 'test-csrf');
  assert.match(sync.requests[1].parsed.tabId, /^[a-f0-9]{32}$/);
  assert.equal(sync.requests[1].credentials, 'same-origin');
  assert.equal(sync.requests[1].redirect, 'error');
  assert.equal((await sync.api.call('settings')).ok, false);
  assert.equal((await sync.api.call('sqlEditorOpen')).ok, false);
  assert.equal((await sync.api.call('unknown')).ok, false);
  assert.equal(sync.requests.length, 2);
  const adminOnPublic = harness(() => ({ ok: true, data: {} }), { admin: true });
  assert.equal((await adminOnPublic.api.call('settings')).ok, true);
  assert.match(adminOnPublic.requests.at(-1).url, /api\/admin\/settings$/);
  const denied = harness(() => assert.fail(), { route: '/admin' });
  assert.equal((await denied.api.call('sqlEditorDirty', { dirty: true })).ok, false);
  assert.equal((await denied.api.call('relatedFiles', { reportId: 'r' })).ok, false);
  for (const [route, admin, endpoint] of [['/', false, '/api/relatedFiles'], ['/', true, '/api/admin/relatedFiles'], ['/admin', true, '/api/admin/relatedFiles']]) {
    const related = harness(() => ({ ok: true, data: { files: [], warnings: [] } }), { route, admin });
    assert.equal((await related.api.call('relatedFiles', { reportId: 'r' })).ok, true);
    assert.equal(related.requests.at(-1).url, 'http://localhost' + endpoint);
  }
  let polled = 0;
  const job = harness(item => item.method === 'POST' ? { ok: true, jobId: 'job-one' } : ++polled === 1 ? { state: 'running', message: 'working' } : { state: 'succeeded', data: ['complete'] });
  const progress = []; job.api.onProgress(value => progress.push(value));
  assert.deepEqual(JSON.parse(JSON.stringify(await job.api.call('list'))), { ok: true, data: ['complete'] });
  assert.deepEqual(progress, ['working']);
  let release, cancellation = false;
  const cancel = harness(async item => {
    if (item.url.endsWith('/select')) return { ok: true, data: { id: 'r', definitionHash: 'cancel-version' } };
    if (item.method === 'POST') return new Promise(resolve => { release = resolve; });
    if (item.method === 'DELETE') { cancellation = true; assert.equal(item.headers['X-ReportDesk-CSRF'], 'test-csrf'); return { ok: true, data: {} }; }
    assert.equal(cancellation, true); return { state: 'cancelled' };
  });
  await cancel.api.call('select', { reportId: 'r' });
  const pending = cancel.api.call('query', { reportId: 'r' });
  while (!release) await new Promise(resolve => setImmediate(resolve));
  await cancel.api.call('cancel'); release({ ok: true, jobId: 'race' });
  assert.equal((await pending).cancelled, true);
  const missing = harness(item => item.method === 'POST' ? { ok: true, jobId: 'gone' } : { http: 404, data: { ok: false, message: 'missing' } });
  assert.match((await missing.api.call('view')).message, /会话或任务已失效/);
  assert.equal(missing.requests.filter(item => item.method === 'POST').length, 1);
  const failed = harness(item => item.method === 'POST' ? { ok: true, jobId: 'bad' } : { state: 'failed', error: { message: 'controlled failure' } });
  assert.equal((await failed.api.call('view')).message, 'controlled failure');
  let selectionHash = 'AbC-EXACT-hash', rejectSelection = false, rejectExecution = false;
  const versioned = harness(item => {
    if (item.url.endsWith('/select')) return rejectSelection ? { ok: false, message: '选择失败' } : { ok: true, data: { id: item.parsed.args.reportId, definitionHash: selectionHash } };
    if (rejectExecution) return { ok: false, message: '报表定义已变化，请重新选择。' };
    return { ok: true, data: {} };
  });
  for (const method of ['query', 'lookup']) assert.equal((await versioned.api.call(method, { reportId: 'a', definitionHash: 'caller-hash' })).ok, false);
  assert.equal(versioned.requests.length, 1, 'Missing selected hash must not reach HTTP');
  await versioned.api.call('select', { reportId: 'a' });
  selectionHash = 'REPORT-B'; await versioned.api.call('select', { reportId: 'b' });
  for (const [reportId, hash] of [['a', 'AbC-EXACT-hash'], ['b', 'REPORT-B']]) for (const method of ['query', 'lookup']) {
    const args = { reportId, source: 2, values: { parameter: 'value' }, definitionHash: 'caller-hash' };
    assert.equal((await versioned.api.call(method, args)).ok, true);
    assert.deepEqual(versioned.requests.at(-1).parsed.args, { ...args, definitionHash: hash });
    assert.equal(args.definitionHash, 'caller-hash', 'Caller payload must remain unchanged');
  }
  selectionHash = 'NEW-HASH'; await versioned.api.call('select', { reportId: 'a' });
  await versioned.api.call('query', { reportId: 'a' }); assert.equal(versioned.requests.at(-1).parsed.args.definitionHash, 'NEW-HASH');
  rejectExecution = true;
  const beforeRejected = versioned.requests.length;
  assert.match((await versioned.api.call('lookup', { reportId: 'a' })).message, /定义已变化/);
  assert.equal(versioned.requests.length, beforeRejected + 1, 'Never refresh and replay a stale source index');
  rejectExecution = false; rejectSelection = true;
  await versioned.api.call('select', { reportId: 'a' });
  const beforeInvalid = versioned.requests.length;
  assert.equal((await versioned.api.call('query', { reportId: 'a' })).ok, false); assert.equal(versioned.requests.length, beforeInvalid);
  rejectSelection = false;
  for (const hash of [undefined, '', ' ', 123]) {
    selectionHash = hash; assert.equal((await versioned.api.call('select', { reportId: 'a' })).ok, false);
  }
  await versioned.api.call('clear');
  assert.equal((await versioned.api.call('lookup', { reportId: 'b' })).ok, false);
  const hashJob = harness(item => item.method === 'POST' ? { ok: true, jobId: 'selected' } : { state: 'succeeded', data: { id: 'job-report', definitionHash: 'JOB-HASH' } });
  assert.equal((await hashJob.api.call('select', { reportId: 'job-report' })).ok, true);
  await hashJob.api.call('lookup', { reportId: 'job-report', source: 1 });
  assert.equal(hashJob.requests.filter(item => item.method === 'POST').at(-1).parsed.args.definitionHash, 'JOB-HASH');
  const admin = harness(item => {
    if (item.url.endsWith('sqlEditorOpen')) return { ok: true, data: { reportId: 'r', token: 'token', path: 'work/report.xml', sources: [{ index: 0, editable: true, name: 'main' }] } };
    if (item.url.endsWith('sqlEditorSave')) return { ok: true, data: { saved: true, editor: { reportId: 'r', token: 'new' } } };
    return { ok: true, data: {} };
  }, { route: '/admin', admin: true });
  await admin.api.call('sqlEditorOpen', { reportId: 'r' });
  await admin.api.call('sqlEditorDirty', { dirty: true });
  let blocked = false; admin.events.beforeunload({ preventDefault() { blocked = true; } }); assert.equal(blocked, true);
  assert.equal((await admin.api.call('sqlEditorOpen')).ok, false);
  await admin.api.call('sqlEditorReveal', { reportId: 'r' });
  assert.equal(admin.requests.at(-1).parsed.args.token, 'token');
  assert.match(admin.requests.at(-1).url, /api\/admin\/sqlEditorReveal$/);
  await admin.api.call('sqlEditorSave', { reportId: 'r', token: 'token', sourceIndex: 0, sql: 'draft' });
  blocked = false; admin.events.beforeunload({ preventDefault() { blocked = true; } }); assert.equal(blocked, false);
  await admin.api.call('import', { folder: true }); assert.equal(admin.requests.at(-1).parsed.args.path, '');
  for (const fingerprint of [undefined, null, '', '   ', 123]) {
    const before = admin.requests.length;
    assert.equal((await admin.api.call('resolveSyncConflict', { releaseId: 42, acceptHis: true, fingerprint })).ok, false);
    assert.equal(admin.requests.length, before, 'Invalid fingerprints must not reach HTTP');
  }
  for (const acceptHis of [true, false]) {
    await admin.api.call('resolveSyncConflict', { releaseId: 42, acceptHis, fingerprint: 'exact-fingerprint', expected: 'legacy-marker' });
    assert.deepEqual(admin.requests.at(-1).parsed.args, { releaseId: 42, acceptHis, fingerprint: 'exact-fingerprint' });
  }
  const dismissed = harness(() => assert.fail('Dismissed conflict reached HTTP'), { route: '/admin', admin: true, confirm: false });
  assert.equal((await dismissed.api.call('resolveSyncConflict', { releaseId: 42, acceptHis: true, fingerprint: 'current' })).data, null);
  const stale = harness(item => item.method === 'POST' ? { ok: true, jobId: 'stale' } : { state: 'failed', error: '冲突指纹已变化，请刷新状态。' }, { route: '/admin', admin: true });
  assert.match((await stale.api.call('resolveSyncConflict', { releaseId: 42, acceptHis: false, fingerprint: 'old' })).message, /指纹已变化/);
  assert.equal(stale.requests.filter(item => item.method === 'POST').length, 1, 'Stale decisions must not be replayed');
  for (const input of ['../outside', 'C:\\report.xml', '\\\\server\\report.xml']) {
    const invalid = harness(() => assert.fail('Invalid import reached HTTP'), { route: '/admin', admin: true, prompt: input });
    assert.equal((await invalid.api.call('import', { folder: true })).ok, false);
  }
  const download = harness(() => ({ ok: true, data: { downloadUrl: '/api/downloads/d1', count: 0 } }));
  assert.equal((await download.api.call('export')).ok, true); assert.deepEqual(download.downloads, ['http://localhost/api/downloads/d1']);
  const external = harness(() => ({ ok: true, data: { downloadUrl: 'https://example.invalid/result' } }));
  assert.equal((await external.api.call('export')).ok, false); assert.equal(external.downloads.length, 0);
  const prefix = harness(() => ({ ok: true, data: [] }), { prefix: '/desk/' });
  await prefix.api.call('list'); assert.equal(prefix.requests.at(-1).url, 'http://localhost/desk/api/list');
  assert.notEqual(prefix.requests.at(-1).parsed.tabId, sync.requests[1].parsed.tabId);
  await sync.api.call('copy', { text: 'local only' }); assert.equal(sync.context.copied, 'local only');
  assert.equal(sync.requests.length, 2);
  const stateSource = fs.readFileSync(path.join(__dirname, '../../../ReportDesk.Desktop/ui/query-form.js'), 'utf8');
  const details = { id: 'r', source: 0, parameters: [] };
  const desktopWindow = {}; vm.runInNewContext(stateSource, { window: desktopWindow });
  const desktopState = desktopWindow.QueryState.create(details, 'select &科室.Text from dual');
  const webWindow = { reportDesk: { isWeb: true } }; vm.runInNewContext(stateSource, { window: webWindow });
  const webState = webWindow.QueryState.create(details, undefined, ['科室']);
  assert.deepEqual([...webState.textParameters], [...desktopState.textParameters]);
  assert.equal(webWindow.QueryState.key({ reportId: 'r', source: 0, values: { '科室.Text': 'A' } }, webState), desktopWindow.QueryState.key({ reportId: 'r', source: 0, values: { '科室.Text': 'A' } }, desktopState));
  const desktopHtml = fs.readFileSync(path.join(__dirname, '../../../ReportDesk.Desktop/ui/index.html'), 'utf8');
  const webHtml = fs.readFileSync(path.join(__dirname, '../index.html'), 'utf8');
  const ids = html => [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]).sort();
  assert.deepEqual(ids(webHtml), ids(desktopHtml), 'Web template must retain every existing desktop control');
  const renderer = fs.readFileSync(path.join(__dirname, '../../../ReportDesk.Desktop/ui/renderer.js'), 'utf8');
  const exportHandler = renderer.split(/\r?\n/).find(line => line.startsWith("$('#export').onclick ="));
  for (const isWeb of [true, false]) {
    const button = {}; let message;
    vm.runInNewContext(exportHandler, { $: () => button, api: { isWeb }, page: { resultId: 'protocol-only', revision: 1, count: 200, total: 1000 },
      task: (_label, action) => action(), call: async () => ({ count: 55 }), progress: text => { message = text; } });
    await button.onclick();
    assert.equal(message, isWeb ? '已请求下载完整筛选与排序结果，请检查浏览器下载记录。' : '已导出 55 行当前筛选与排序结果。');
  }
  console.log('PASS: definitionHash exact injection/isolation/invalidation, relatedFiles routing, export copy; HTTP envelopes, CSRF, jobs/cancel/404, admin gate, drafts, downloads, imports, tabs, Text aliases. No Oracle access.');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
