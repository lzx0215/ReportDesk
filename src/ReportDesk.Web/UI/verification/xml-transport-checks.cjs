'use strict';
// Protocol-only: XML edits and cancellation never reach an actual server.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../transport.js'), 'utf8');
function harness(admin, confirm = true) {
  const requests = [], events = {}, confirmations = [];
  const window = { confirm: text => { confirmations.push(text); return confirm; }, addEventListener: (name, fn) => { events[name] = fn; } };
  const document = { currentScript: { src: 'http://localhost/assets/transport.js' } };
  vm.runInNewContext(source, { window, document, location: { pathname: '/' }, crypto: webcrypto, URL, Uint8Array,
    setTimeout, navigator: {}, fetch: async (url, init) => {
      const item = { url, ...init }; requests.push(item);
      let data;
      if (url.endsWith('/api/session')) data = { csrf: 'csrf', admin, offline: true };
      else if (url.endsWith('/xmlEditorOpen')) data = { ok: true, data: { reportId: 'r', kind: 'layout', path: 'C:\\reports\\layout.xml', token: 'one', text: '<Spread/>' } };
      else if (url.endsWith('/xmlEditorSave')) data = { ok: true, data: { saved: true, editor: { reportId: 'r', token: 'two', path: 'C:\\reports\\layout.xml' } } };
      else data = { ok: true, data: { passed: true } };
      return { ok: true, status: 200, json: async () => data };
    } });
  return { api: window.reportDesk, requests, events, confirmations };
}
(async () => {
  const denied = harness(false);
  assert.equal((await denied.api.call('xmlEditorOpen', { reportId: 'r', kind: 'layout' })).ok, false);
  assert.equal((await denied.api.call('xmlEditorSave', { reportId: 'r', token: 'one', text: '<Spread/>' })).ok, false);
  assert.equal(denied.requests.length, 1);
  const admin = harness(true);
  assert.equal((await admin.api.call('xmlEditorOpen', { reportId: 'r', kind: 'layout' })).ok, true);
  await admin.api.call('xmlEditorDirty', { dirty: true });
  let blocked = false; admin.events.beforeunload({ preventDefault() { blocked = true; } }); assert.equal(blocked, true);
  const before = admin.requests.length;
  assert.equal((await admin.api.call('xmlEditorSave', { reportId: 'r', token: 'wrong', text: '<Spread/>' })).ok, false);
  assert.equal(admin.requests.length, before);
  assert.equal((await admin.api.call('xmlEditorCheck', { reportId: 'r', token: 'one', text: '<Spread/>' })).ok, true);
  assert.equal((await admin.api.call('xmlEditorSave', { reportId: 'r', token: 'one', text: '<Spread/>' })).data.saved, true);
  assert.match(admin.requests.at(-1).url, /\/api\/admin\/xmlEditorSave$/);
  assert.match(admin.confirmations[0], /layout\.xml/); assert.match(admin.confirmations[0], /备份/);
  blocked = false; admin.events.beforeunload({ preventDefault() { blocked = true; } }); assert.equal(blocked, false);
  const cancel = harness(true, false);
  await cancel.api.call('xmlEditorOpen', { reportId: 'r', kind: 'layout' });
  await cancel.api.call('xmlEditorDirty', { dirty: true });
  const count = cancel.requests.length;
  assert.equal((await cancel.api.call('xmlEditorSave', { reportId: 'r', token: 'one', text: '<Spread/>' })).data, null);
  assert.equal(cancel.requests.length, count);
  blocked = false; cancel.events.beforeunload({ preventDefault() { blocked = true; } }); assert.equal(blocked, true);
  console.log('PASS XML transport: maintenance boundary, session token, target confirmation, cancellation and dirty warning; no server/Oracle.');
})().catch(error => { console.error(error); process.exitCode = 1; });
