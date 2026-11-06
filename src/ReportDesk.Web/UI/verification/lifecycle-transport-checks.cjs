'use strict';
// Protocol-only lifecycle checks; no Oracle or filesystem writes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../transport.js'), 'utf8');
let count = 0;
function check(value, message) { assert.ok(value, message); count++; }
function harness() {
  const requests = [], events = {}, emitted = [], intervals = [], fatal = [];
  let now = 100000, sessions = 0, mode = 'ok';
  const local = { active: true, pending: false, working: false, state: { staging: false, workspaceLost: false }, guard() {} };
  const window = { reportDeskLocal: local, confirm: () => true,
    addEventListener: (key, value) => { events[key] = value; },
    dispatchEvent: event => { emitted.push(event); if (event.type === 'reportdesk-workspace-lost') { local.active = false; local.state.staging = false; local.state.workspaceLost = true; } } };
  const document = { currentScript: { src: 'https://local.test/desk/assets/transport.js' }, visibilityState: 'visible' };
  const context = { window, document, location: { pathname: '/desk/admin' }, navigator: {}, URL, Uint8Array, crypto: webcrypto,
    CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init.detail; } }, Date: { now: () => now },
    setTimeout: fn => setImmediate(fn), setInterval: fn => { intervals.push(fn); },
    fetch: async (url, init) => {
      const item = { url, ...init, body: init.body && JSON.parse(init.body) }; requests.push(item);
      let status = 200, data;
      if (url.endsWith('/api/session')) data = { csrf: 'csrf-' + ++sessions, admin: true, serverReportsEnabled: false };
      else if (mode === 'expired') { status = 401; data = { message: 'expired' }; }
      else if (mode === 'stale') { status = 410; data = { message: 'workspace gone' }; }
      else if (mode === 'denied') { status = 403; data = { message: 'denied' }; }
      else if (url.endsWith('/tab-lifecycle')) data = { ok: true, data: { alive: mode !== 'missing', local: mode !== 'missing', busy: false, released: init.keepalive === true } };
      else data = { ok: true, data: {} };
      return { ok: status === 200, status, json: async () => data };
    } };
  vm.runInNewContext(source, context);
  window.reportDesk.onFatal(message => fatal.push(message));
  return { api: window.reportDesk, local, document, requests, events, emitted, intervals, fatal,
    mode: value => { mode = value; }, advance: () => { now += 30001; } };
}
const tick = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  const h = harness();
  check((await h.api.ready).serverReportsEnabled === false, 'Local-first capability projected');
  await h.api.call('xmlEditorDirty', { dirty: true });
  await h.api.activity();
  let item = h.requests.at(-1);
  check(item.url === 'https://local.test/desk/api/tab-lifecycle' && item.body.args.action === 'touch', 'Activity uses application prefix and dedicated route');
  check(item.headers['X-ReportDesk-CSRF'] === 'csrf-1', 'Activity includes CSRF');
  let before = h.requests.length;
  await h.api.activity(); check(h.requests.length === before, 'Activity throttled');
  h.advance(); h.document.visibilityState = 'hidden'; h.events.pointerdown(); await tick();
  check(h.requests.length === before, 'Hidden page interaction does not touch');
  h.document.visibilityState = 'visible'; h.events.keydown(); await tick();
  check(h.requests.length === before + 1, 'Visible keyboard activity touches');
  h.advance(); before = h.requests.length; h.intervals[0](); await tick();
  check(h.requests.length === before, 'Idle page has no recurring heartbeat');
  h.local.working = true; h.intervals[0](); await tick();
  check(h.requests.length === before + 1, 'Physical file operation receives heartbeat');
  h.local.working = false;
  await h.api.call('list'); check(h.requests.at(-1).body.localExpected === true, 'Local data operation is bound to local context');
  h.mode('stale'); before = h.requests.length; const stale = await h.api.call('list');
  check(!stale.ok && h.requests.length === before + 1, 'Stale request rejected without replay');
  check(h.emitted.length === 1 && !h.local.active && h.local.state.workspaceLost, '410 signals workspace loss');
  check(h.api.hasDraft() && h.fatal.length === 0, '410 preserves editor draft and recovery UI');
  h.mode('ok'); before = h.requests.length; await h.api.call('localBegin', { name: 'My XML' });
  check(h.requests.length === before + 2 && h.requests[before].method === 'GET', 'Explicit load first obtains a session');
  item = h.requests.at(-1);
  check(item.body.localExpected === false && item.headers['X-ReportDesk-CSRF'] === 'csrf-2', 'Explicit load uses refreshed CSRF without requiring obsolete context');
  h.local.active = true; h.local.state.workspaceLost = false; h.mode('expired'); before = h.requests.length;
  await h.api.call('list');
  check(h.requests.length === before + 1 && h.fatal.length === 0 && h.api.hasDraft(), 'Local session expiry preserves draft without replay or fatal disable');
  const restored = harness(); await restored.api.ready; restored.local.active = false; restored.local.pending = true; restored.mode('expired');
  await restored.api.call('localStatus');
  check(restored.emitted.length === 1 && restored.fatal.length === 0 && restored.local.pending, 'Expired session preserves a restored pending record even before local activation');
  h.mode('denied'); await h.api.call('settings'); check(h.fatal.length === 1, '403 still closes unauthorized operations');
  const missing = harness(); await missing.api.ready; missing.mode('missing'); await missing.api.activity();
  check(missing.emitted.length === 1 && !missing.local.active, 'Absent tab reported by heartbeat invalidates browser workspace');
  const close = harness(); await close.api.ready;
  before = close.requests.length; close.events.pagehide({ persisted: true }); await tick();
  check(close.requests.length === before, 'Back-forward cached page is retained');
  close.local.pending = true; close.events.pagehide({ persisted: false }); await tick();
  check(close.requests.length === before, 'Pending recovery is not eagerly released');
  close.local.pending = false; close.local.working = true; close.events.pagehide({ persisted: false }); await tick();
  check(close.requests.length === before, 'Physical save is not eagerly released');
  close.local.working = false; close.events.pagehide({ persisted: false }); await tick(); item = close.requests.at(-1);
  check(item.keepalive === true && item.body.args.action === 'release' && item.headers['X-ReportDesk-CSRF'] === 'csrf-1', 'Normal page exit sends authenticated best-effort release');
  console.log(`PASS ${count} lifecycle transport checks; protocol-only, no database.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
