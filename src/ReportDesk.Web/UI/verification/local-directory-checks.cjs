'use strict';
// Failure/recovery logic with genuine retained XML bytes and in-memory browser
// API doubles. This is not evidence of native directory permission support.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const { DOMParser: XmlParser } = require(path.resolve(__dirname, '../../../ReportDesk.Desktop/node_modules/@xmldom/xmldom'));
class DOMParser {
  parseFromString(text, type) {
    const doc = new XmlParser().parseFromString(text, type);
    const visit = node => {
      if (node.nodeType === 1) Object.defineProperty(node, 'children', { get() { return Array.from(this.childNodes).filter(x => x.nodeType === 1); } });
      for (const child of Array.from(node.childNodes || [])) visit(child);
    };
    visit(doc);
    return doc;
  }
}
const repo = path.resolve(__dirname, '../../../..');
const script = fs.readFileSync(path.join(__dirname, '../local-directory.js'), 'utf8');
const fixture = path.join(repo, 'artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml');
const queryName = '抗菌药物消耗情况查询设置.xml';
const layoutName = '抗菌药物消耗情况报表设置.xml';
const queryPath = 'Config/Xml/' + queryName, layoutPath = 'Config/Xml/' + layoutName;
const query = fs.readFileSync(path.join(fixture, queryName));
const layout = fs.readFileSync(path.join(fixture, layoutName));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sourceHashes = [hash(query), hash(layout)];
let checks = 0;
function check(condition, message) { assert.ok(condition, message); checks++; console.log('PASS ' + message); }
async function rejects(action, pattern, message) { await assert.rejects(action, pattern); checks++; console.log('PASS ' + message); }

function clone(value) {
  if (!value || typeof value !== 'object' || value instanceof Entry) return value;
  if (Array.isArray(value)) return value.map(clone);
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, clone(item)]));
}

class Entry {
  constructor(name, parent, kind, bytes = Buffer.alloc(0)) {
    this.name = name; this.parent = parent; this.kind = kind; this.bytes = Buffer.from(bytes);
    this.children = new Map(); this.writes = 0; this.permission = 'granted';
  }
  get root() { return this.parent ? this.parent.root : this; }
  get fullPath() { return this.parent ? this.parent.fullPath + '/' + this.name : this.name; }
  async queryPermission() { return this.root.permission; }
  async requestPermission() { return this.root.permission; }
  async isSameEntry(other) { return this === other; }
  async resolve(other) {
    const parts = []; let current = other;
    while (current && current !== this) { parts.unshift(current.name); current = current.parent; }
    return current ? parts : null;
  }
  async *entries() { for (const pair of this.children) yield pair; }
  async getDirectoryHandle(name, options = {}) { return this.child(name, 'directory', options.create); }
  async getFileHandle(name, options = {}) { return this.child(name, 'file', options.create); }
  child(name, kind, create) {
    if (!this.children.has(name) && create) this.children.set(name, new Entry(name, this, kind));
    const entry = this.children.get(name);
    if (!entry || entry.kind !== kind) throw new Error('NotFoundError: ' + name);
    return entry;
  }
  async getFile() {
    const bytes = Buffer.from(this.bytes);
    return { size: bytes.length, arrayBuffer: async () => Uint8Array.from(bytes).buffer };
  }
  async createWritable() {
    const target = this; let bytes;
    return {
      async write(value) { bytes = Buffer.from(value); },
      async close() {
        const failure = target.root.failWrite?.(target);
        if (failure) throw new Error(failure);
        target.bytes = bytes; target.writes++;
      },
      async abort() {}
    };
  }
}
function add(root, name, bytes) {
  const parts = name.split('/'); let directory = root;
  for (const part of parts.slice(0, -1)) directory = directory.child(part, 'directory', true);
  const file = directory.child(parts.at(-1), 'file', true); file.bytes = Buffer.from(bytes); return file;
}
function at(root, name) { return name.split('/').reduce((directory, part) => directory.children.get(part), root); }
function all(root) { return [...root.children.values()].flatMap(entry => entry.kind === 'file' ? [entry] : all(entry)); }

// Requests and transaction completion are asynchronous; readwrite changes commit
// atomically. Handles retain identity, while recovery metadata is cloned.
class BrowserStorage {
  constructor() { this.values = new Map(); this.failPendingPut = false; this.busy = false; }
  indexedDB() {
    const shared = this;
    return { open() {
      const request = {};
      setImmediate(() => { request.result = { createObjectStore() {}, close() {}, transaction: () => shared.transaction() }; request.onsuccess?.(); });
      return request;
    } };
  }
  transaction() {
    const shared = this, staged = new Map(this.values); let pending = 0, done = false, completion = false;
    const transaction = {
      abort() { if (done) return; done = true; setImmediate(() => transaction.onabort?.()); },
      objectStore() { return {
        get: key => operation(() => clone(staged.get(key))),
        put: (value, key) => operation(() => {
          if (key === 'pending' && shared.failPendingPut) throw new Error('storage full');
          staged.set(key, clone(value)); return key;
        }),
        delete: key => operation(() => staged.delete(key))
      }; }
    };
    function finish() {
      if (completion || pending || done) return;
      completion = true;
      setImmediate(() => { completion = false; if (pending || done) return; shared.values = staged; done = true; transaction.oncomplete?.(); });
    }
    function operation(action) {
      const request = {}; pending++;
      setImmediate(() => {
        if (done) return;
        try { request.result = action(); request.onsuccess?.(); }
        catch (error) { done = true; transaction.onerror?.({ target: { error } }); }
        finally { pending--; finish(); }
      });
      return request;
    }
    return transaction;
  }
  locks() {
    const shared = this;
    return { async request(name, options, action) {
      assert.equal(name, 'ReportDeskLocalSave'); assert.equal(options.ifAvailable, true);
      if (shared.busy) return action(null);
      shared.busy = true;
      try { return await action({ name }); } finally { shared.busy = false; }
    } };
  }
}

async function browser(shared = new BrowserStorage()) {
  const choices = [], events = {};
  const window = { isSecureContext: true, showDirectoryPicker: async () => choices.shift(), dispatchEvent() {}, addEventListener: (name, action) => { events[name] = action; } };
  vm.runInNewContext(script, { window, navigator: { locks: shared.locks() }, indexedDB: shared.indexedDB(), crypto: webcrypto,
    CustomEvent: class { constructor(type) { this.type = type; } }, Uint8Array, ArrayBuffer, setTimeout, TextDecoder, DOMParser,
    btoa: text => Buffer.from(text, 'binary').toString('base64'), atob: text => Buffer.from(text, 'base64').toString('binary') });
  const api = window.reportDeskLocal; await api.ready;
  return { api, choices, events, window, shared };
}
function server() {
  const calls = [];
  const request = async (method, args) => {
    calls.push({ method, args });
    if (request.failCommit && method === 'localCommit') throw new Error('response lost after browser writes');
    if (method === 'localCommit') return { saved: true, editor: { token: 'next-editor-token' } };
    if (method === 'localActivate') return { queryPaths: [{ id: 'server-id', path: queryPath }] };
    if (method === 'localLocations') return { items: args.items.map(item => ({ path: item.path, locations: [{ Path: '测试 → 菜单', Evidence: '测试', Match: '查询文件哈希匹配', Candidate: false, Active: true }] })) };
    return { local: true };
  };
  request.calls = calls; return request;
}
async function loaded(shared) {
  const b = await browser(shared), source = new Entry('LIB-copy', null, 'directory'), backup = new Entry('HIS-backups', null, 'directory');
  const queryFile = add(source, queryPath, query), layoutFile = add(source, layoutPath, layout);
  add(source, 'ignored.txt', Buffer.from('not an XML report'));
  b.choices.push(source); await b.api.choose('source'); b.choices.push(backup); await b.api.choose('backup');
  const request = server(); await b.api.load(request);
  while (b.api.state.indexScanning) await new Promise(resolve => setTimeout(resolve, 1));
  check(request.calls.every(call => call.method === 'localLocations'), 'building the local index only requests location metadata');
  check(b.api.indexReports[0].locations[0].Path === '测试 → 菜单', 'location matches are included in searchable report metadata');
  await b.api.openReport('local:' + queryPath, request); await b.api.saveAccess();
  return { ...b, source, backup, queryFile, layoutFile, request };
}
function candidate(h, includeLayout = false) {
  const targets = includeLayout ? [h.queryFile, h.layoutFile] : [h.queryFile];
  return { localSave: true, saveId: webcrypto.randomUUID(), checks: [h.queryFile, h.layoutFile].map(file => ({ path: file === h.queryFile ? queryPath : layoutPath, hash: hash(file.bytes) })),
    files: targets.map(file => {
      // XML comment appended to a copy keeps the genuine definition and SQL intact.
      const content = Buffer.concat([file.bytes, Buffer.from('\r\n<!-- local-copy failure check -->\r\n')]);
      return { path: file === h.queryFile ? queryPath : layoutPath, oldHash: hash(file.bytes), newHash: hash(content), content: content.toString('base64') };
    }) };
}

(async () => {
  {
    const b = await browser(), source = new Entry('xml-test', null, 'directory'), request = server();
    add(source, queryPath, Buffer.concat([query, Buffer.from('\r\n<!-- literal <!DOCTYPE test> -->\r\n')]));
    b.choices.push(source); await b.api.choose('source'); await b.api.load(request);
    while (b.api.state.indexScanning) await new Promise(resolve => setTimeout(resolve, 1));
    check(b.api.indexReports.length === 1, 'a DOCTYPE literal inside a comment does not hide a valid report');
  }
  {
    const b = await browser(), source = new Entry('location-test', null, 'directory');
    add(source, queryPath, query); b.choices.push(source); await b.api.choose('source');
    await b.api.load(async method => { if (method === 'localLocations') throw new Error('location service unavailable'); });
    while (b.api.state.indexScanning) await new Promise(resolve => setTimeout(resolve, 1));
    check(b.api.indexReports.length === 1 && /HIS 位置搜索不完整/.test(b.api.state.locationWarning), 'location failure keeps name search available and warns that location search is incomplete');
  }
  {
    const h = await loaded();
    add(h.source, 'other/' + layoutName, layout);
    await rejects(() => h.api.fresh(), /候选文件集合已变化/, 'a newly added same-name candidate blocks the old snapshot');
  }
  {
    const h = await loaded(), prepared = candidate(h, true);
    add(h.source, 'other/' + layoutName, layout);
    await rejects(() => h.api.save(prepared, h.request), /候选文件集合已变化/, 'a candidate added after confirmation blocks saving');
    check(h.queryFile.writes === 0 && h.layoutFile.writes === 0, 'candidate conflict does not write either original file');
  }
  {
    const h = await loaded();
    h.layoutFile.bytes = Buffer.from(h.layoutFile.bytes);
    h.layoutFile.bytes[h.layoutFile.bytes.length - 2] ^= 1;
    await rejects(() => h.api.fresh(), /关联候选已变化/, 'candidate content changes are detected even with unchanged metadata');
  }
  {
    const h = await loaded(), second = await browser(h.shared), calls = server();
    await second.api.load(calls);
    check(second.api.indexReports.some(item => item.id === 'local:' + queryPath), 'remembered matching directory restores the cached list immediately');
    while (second.api.state.indexScanning) await new Promise(resolve => setTimeout(resolve, 1));
    check(calls.calls.every(call => call.method === 'localLocations'), 'cache verification does not upload XML');
  }
  {
    const h = await loaded(), calls = server();
    const [first, second] = await Promise.allSettled([h.api.openReport('local:' + queryPath, calls), h.api.openReport('local:' + queryPath, calls)]);
    check(first.status === 'fulfilled' && second.status === 'rejected', 'concurrent local workspace switches cannot interleave uploads');
  }
  {
    const h = await loaded();
    check(h.request.calls.filter(call => call.method === 'localUpload').length === 2, 'only real XML files upload; unrelated files are skipped');
    check(h.request.calls.filter(call => call.method === 'localUpload').every(call => hash(Buffer.from(call.args.content, 'base64')) === (call.args.path === queryPath ? sourceHashes[0] : sourceHashes[1])), 'uploaded content preserves original fixture bytes');
    const prepared = candidate(h, true); await h.api.save(prepared, h.request);
    check(h.queryFile.writes === 1 && h.layoutFile.writes === 1 && !h.api.pending, 'successful multi-file save confirms and clears pending record');
    check(all(h.backup).every(file => hash(file.bytes) === (file.name === queryName ? sourceHashes[0] : sourceHashes[1])), 'verified backup retains original bytes for every changed file');
    check(h.api.lastSaveId === prepared.saveId && !h.shared.values.has('pending'), 'successful commit records save identity and removes durable recovery state');
    await h.api.fresh(); check(!h.api.state.needsScan, 'post-save baseline accepts exactly the newly written bytes');
  }
  {
    const h = await loaded(); h.backup.failWrite = () => 'backup disk full';
    await rejects(() => h.api.save(candidate(h, true), h.request), /backup disk full/, 'backup failure rejects saving');
    check(h.queryFile.writes === 0 && h.layoutFile.writes === 0 && hash(h.queryFile.bytes) === sourceHashes[0], 'backup failure never opens a source write');
    check(h.request.calls.at(-1).method === 'localAbort' && !h.api.pending && h.api.state.needsScan, 'backup failure releases candidate and requires rescan');
  }
  {
    const h = await loaded(); h.shared.failPendingPut = true;
    await rejects(() => h.api.save(candidate(h), h.request), /恢复记录/, 'unavailable durable recovery storage rejects saving');
    check(h.queryFile.writes === 0 && all(h.backup).length === 0, 'recovery record must persist before backup or source writes');
  }
  {
    const h = await loaded(), prepared = candidate(h, true);
    h.source.failWrite = entry => entry === h.layoutFile ? 'second file write failed' : null;
    await rejects(() => h.api.save(prepared, h.request), /second file write failed/, 'multi-file failure retains incomplete save');
    check(hash(h.queryFile.bytes) === prepared.files[0].newHash && hash(h.layoutFile.bytes) === sourceHashes[1] && h.api.pending, 'partial save keeps exact changed and unchanged bytes plus durable pending');
    check(!h.request.calls.some(call => call.method === 'localCommit'), 'partial save cannot acknowledge success to server');
    await h.api.recover(h.request, 'restore');
    check(hash(h.queryFile.bytes) === sourceHashes[0] && hash(h.layoutFile.bytes) === sourceHashes[1] && !h.api.pending, 'verified restoration recovers changed file and leaves untouched companion unchanged');
    check(h.api.state.needsScan && h.request.calls.at(-1).method === 'localAbort', 'restoration invalidates old server snapshot until rescan');
  }
  {
    const h = await loaded(), prepared = candidate(h, true); h.request.failCommit = true;
    await rejects(() => h.api.save(prepared, h.request), /response lost/, 'lost acknowledgement leaves recoverable confirmed-on-disk bytes');
    h.layoutFile.bytes = Buffer.concat([h.layoutFile.bytes, Buffer.from('<!-- third-party modification -->')]);
    const writesBefore = h.queryFile.writes;
    await rejects(() => h.api.recover(h.request, 'restore'), /其他程序修改/, 'third-party hash conflict rejects automatic recovery');
    check(h.queryFile.writes === writesBefore && hash(h.queryFile.bytes) === prepared.files[0].newHash && h.api.pending, 'all restore files validate before any rollback writes');
  }
  {
    const h = await loaded(), prepared = candidate(h); h.request.failCommit = true;
    await rejects(() => h.api.save(prepared, h.request), /response lost/, 'commit response loss retains durable recovery record');
    const refreshed = await browser(h.shared);
    check(refreshed.api.pending && !refreshed.api.active && refreshed.api.state.remembered, 'new page restores remembered handles and pending recovery without activating stale reports');
    const freshRequest = server(); const recovered = await refreshed.api.recover(freshRequest, 'confirm');
    check(recovered.confirmed === true && recovered.reloaded === false && freshRequest.calls.length === 0, 'refreshed page confirms physical bytes without using old page server identity');
    check(hash(h.queryFile.bytes) === prepared.files[0].newHash && !refreshed.api.pending, 'refresh confirmation preserves saved bytes and clears recovery record');
  }
  {
    const h = await loaded(), prepared = candidate(h); h.request.failCommit = true;
    await rejects(() => h.api.save(prepared, h.request), /response lost/, 'same-page acknowledgement loss keeps recovery record');
    h.request.failCommit = false;
    const writes = h.queryFile.writes;
    await h.api.recover(h.request, 'confirm');
    check(h.queryFile.writes === writes && h.api.lastSaveId === prepared.saveId && !h.api.pending, 'same-page confirmation retries acknowledgement without rewriting source');
  }
  {
    const h = await loaded(), prepared = candidate(h, true); h.request.failCommit = true;
    await rejects(() => h.api.save(prepared, h.request), /response lost/, 'complete writes remain pending when acknowledgement fails');
    all(h.backup).find(file => file.name === layoutName).bytes = Buffer.from('corrupted backup');
    const writes = h.queryFile.writes;
    await rejects(() => h.api.recover(h.request, 'restore'), /备份内容不匹配/, 'corrupted companion backup prevents automatic restoration');
    check(h.queryFile.writes === writes && h.api.pending, 'all backup hashes validate before restoring even first source file');
  }
  {
    const h = await loaded(), prepared = candidate(h); h.shared.busy = true;
    await rejects(() => h.api.save(prepared, h.request), /另一个页面/, 'cross-tab write lock prevents concurrent save');
    check(h.queryFile.writes === 0 && h.request.calls.at(-1).method === 'localAbort' && h.api.state.needsScan, 'failed write lock does not write and releases its own candidate');
  }
  {
    const shared = new BrowserStorage(), first = await loaded(shared), second = await loaded(shared);
    first.request.failCommit = true;
    await rejects(() => first.api.save(candidate(first), first.request), /response lost/, 'first tab retains pending save after released lock');
    await rejects(() => second.api.save(candidate(second), second.request), /未完成的本机保存/, 'durable pending claim protects tabs even after transient write lock releases');
    check(second.queryFile.writes === 0 && shared.values.get('pending').source === first.source, 'second tab cannot replace first tab recovery record');
  }
  {
    const h = await loaded(), prepared = candidate(h); h.layoutFile.bytes = Buffer.concat([layout, Buffer.from('<!-- external -->')]);
    await rejects(() => h.api.save(prepared, h.request), /关联(?:文件|候选)已变化/, 'unchanged companion hash is checked before save');
    check(h.queryFile.writes === 0 && h.layoutFile.writes === 0, 'companion conflict prevents all source writes');
  }
  {
    const h = await loaded(); h.source.permission = 'denied';
    await rejects(() => h.api.saveAccess(), /授权已失效/, 'revoked source write permission blocks save preparation');
    h.source.permission = 'granted'; h.choices.push(await h.source.getDirectoryHandle('Config'));
    await rejects(() => h.api.choose('backup'), /互相包含/, 'backup directory cannot be inside selected HIS source');
    const unsupported = await browser(); unsupported.window.isSecureContext = false;
    check(!unsupported.api.supported(), 'HTTP insecure context does not advertise local write support');
  }
  {
    const h = await loaded(), before = h.request.calls.length;
    h.events['reportdesk-workspace-lost']();
    check(!h.api.active && !h.api.state.staging && h.api.state.workspaceLost && h.api.state.needsScan, 'workspace loss deactivates stale server state and requires an explicit rescan');
    check(h.api.state.source === h.source.name && h.api.state.backup === h.backup.name && h.request.calls.length === before, 'workspace loss retains directory handles without automatic requests');
    assert.throws(() => h.api.guard('query'), /失效/); assert.throws(() => h.api.guard('sqlEditorSave'), /失效/);
    check(true, 'workspace loss blocks old query and editor operations');
    await rejects(() => h.api.fresh(), /失效/, 'workspace loss cannot reuse a stale editor snapshot');
    await h.api.load(h.request);
    check(h.api.active && !h.api.state.workspaceLost && !h.api.state.needsScan, 'explicit directory reload restores a fresh workspace');
    await h.api.useServer(h.request);
    check(!h.api.active && !h.api.state.workspaceLost && h.api.state.source === h.source.name && h.api.state.backup === h.backup.name, 'closing a directory clears runtime state while retaining selected handles');
    check(hash(h.queryFile.bytes) === sourceHashes[0] && hash(h.layoutFile.bytes) === sourceHashes[1], 'close and reload leave original XML unchanged');
  }
  for (const mode of ['confirm','restore']) {
    const h = await loaded(), prepared = candidate(h); h.request.failCommit = true;
    await rejects(() => h.api.save(prepared,h.request), /response lost/, 'pending save remains before workspace loss (' + mode + ')');
    h.events['reportdesk-workspace-lost'](); const calls = h.request.calls.length;
    check(h.api.pending && !h.api.active && h.api.state.workspaceLost, 'workspace loss preserves durable pending recovery (' + mode + ')');
    await rejects(() => h.api.useServer(h.request), /未完成/, 'pending recovery prevents directory close (' + mode + ')');
    await h.api.recover(h.request,mode);
    check(h.request.calls.length === calls && !h.api.pending && h.api.state.workspaceLost, 'expired workspace recovery only checks local files without stale server acknowledgement (' + mode + ')');
    check(hash(h.queryFile.bytes) === (mode === 'confirm' ? prepared.files[0].newHash : sourceHashes[0]), 'expired workspace recovery preserves expected local bytes (' + mode + ')');
  }
  check(hash(fs.readFileSync(path.join(fixture, queryName))) === sourceHashes[0] && hash(fs.readFileSync(path.join(fixture, layoutName))) === sourceHashes[1], 'read-only retained real XML fixture hashes unchanged');
  console.log('PASS ' + checks + ' local directory logic checks. Browser APIs simulated; native permissions, Oracle and IIS NOT RUN.');
})().catch(error => { console.error(error); process.exitCode = 1; });
