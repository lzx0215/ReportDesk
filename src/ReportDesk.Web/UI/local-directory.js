'use strict';
// Directory handles stay in the browser. Only XML bytes and relative paths are
// sent to the current tab's private server workspace; never credentials/results.
(() => {
  const CHUNK = 1024 * 1024;
  const MAX_FILE = 32 * 1024 * 1024;
  const blocked = new Set(['import', 'browseReports', 'uploadImport', 'checkNewReports', 'syncNow', 'resolveSyncConflict', 'publishPreview']);
  let source = null, backup = null, activeSource = null, activeFiles = new Map(), baseline = new Map();
  let active = false, staging = false, needsScan = false, workspaceLost = false, pending = null, working = false, remembered = false, message = '', locationWarning = '';
  let lastSaveId = null, indexFiles = new Map(), indexReports = [], indexScanning = false, indexFailed = false, scanGeneration = 0, attached = false, serverReportId = null, localReportId = null, candidateNames = [], candidateSnapshot = [], candidateHashes = new Map(), queryDetail = '';
  const indexNotify = () => window.dispatchEvent(new CustomEvent('reportdesk-local-index'));
  const yieldUi = () => new Promise(resolve => setTimeout(resolve, 0));
  const owner = crypto.randomUUID ? crypto.randomUUID() : Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
  const supported = () => window.isSecureContext === true && typeof window.showDirectoryPicker === 'function' && !!crypto.subtle && !!navigator.locks;
  const notify = () => window.dispatchEvent(new CustomEvent('reportdesk-local-change'));
  function status(text) { message = text; notify(); }
  function markWorkspaceLost() {
    if (!active && !staging && !pending && !workspaceLost) return;
    // A pending save must recover against local bytes when its server context
    // is gone. Keep handles, backups, recovery metadata and editor drafts intact.
    active = false; staging = false; attached = false; needsScan = true; workspaceLost = true;
    status('本页临时工作区已失效。请先保留编辑草稿；待保存文件先核对或恢复，再明确重新扫描目录。不会自动上传或执行查询。');
  }
  function safePath(path) {
    if (typeof path !== 'string' || !path || path.startsWith('/') || path.includes('\\') || path.includes(':') || /[\x00-\x1f]/.test(path) ||
        path.split('/').some(p => !p || p === '.' || p === '..' || /[. ]$/.test(p))) throw new Error('文件相对路径无效；未保存。');
    return path;
  }
  async function digest(bytes) {
    return Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), n => n.toString(16).padStart(2, '0')).join('');
  }
  function encoded(bytes) {
    let text = '';
    for (let i = 0; i < bytes.length; i += 32768) text += String.fromCharCode(...bytes.subarray(i, i + 32768));
    return btoa(text);
  }
  function decoded(text) {
    if (typeof text !== 'string' || text.length > MAX_FILE * 1.4) throw new Error('待保存内容无效或过大。');
    const raw = atob(text), bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    return bytes;
  }
  function openDb() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open('ReportDeskLocalDirectories', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('state');
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(new Error('浏览器未允许保存目录授权和恢复记录；请检查网站存储设置。'));
    });
  }
  async function stored(key, value, remove = false) {
    const db = await openDb();
    try {
      return await new Promise((resolve, reject) => {
        const transaction = db.transaction('state', value === undefined && !remove ? 'readonly' : 'readwrite');
        const store = transaction.objectStore('state');
        const request = remove ? store.delete(key) : value === undefined ? store.get(key) : store.put(value, key);
        transaction.oncomplete = () => resolve(request.result);
        transaction.onerror = () => reject(new Error('目录恢复记录写入失败；为保护原文件，本次操作已停止。'));
        transaction.onabort = transaction.onerror;
      });
    } finally { db.close(); }
  }
  // A single pending record serializes writes across tabs in this browser. It is
  // durable BEFORE any source file is changed and contains no DB/query data.
  async function claim(record) {
    const db = await openDb();
    try {
      await new Promise((resolve, reject) => {
        const transaction = db.transaction('state', 'readwrite'), store = transaction.objectStore('state');
        const request = store.get('pending');
        let occupied = false;
        request.onsuccess = () => { occupied = !!request.result; if (occupied) transaction.abort(); else store.put(record, 'pending'); };
        transaction.oncomplete = resolve;
        transaction.onabort = () => reject(new Error(occupied ? '此浏览器还有未完成的本机保存，请先在“报表来源 → 打开本机目录”中核对和恢复。' : '无法保存恢复记录；未写入原文件。'));
        transaction.onerror = () => reject(new Error('无法保存恢复记录；未写入原文件。'));
      });
    } finally { db.close(); }
  }
  async function permission(handle, request = false) {
    if (!handle) throw new Error('请先在“报表来源 → 打开本机目录”中选择目录。');
    if (await handle.queryPermission({ mode: 'readwrite' }) === 'granted') return;
    if (request && await handle.requestPermission({ mode: 'readwrite' }) === 'granted') return;
    throw new Error('目录读写授权已失效，请在“报表来源 → 打开本机目录”中点击“重新授权”。');
  }
  async function separate(a, b) {
    if (!a || !b) return;
    if (await a.isSameEntry(b) || await a.resolve(b) !== null || await b.resolve(a) !== null)
      throw new Error('备份目录必须与 HIS 目录分开，不能相同或互相包含。请选择另一个目录。');
  }
  async function remember() { await stored('handles', { source, backup }); remembered = false; }
  async function performChoose(kind) {
    if (!supported()) throw new Error('本机目录需要可信 HTTPS 和支持目录读写的浏览器。请使用 Chrome、Edge；360 请启用极速模式并确认支持目录读写。');
    if (pending) throw new Error('请先完成或恢复当前本机保存。');
    const selected = await window.showDirectoryPicker({ id: kind === 'source' ? 'reportdesk-his' : 'reportdesk-backups', mode: 'readwrite' });
    if (kind === 'source') { await separate(selected, backup); source = selected; }
    else { await separate(source, selected); await separate(activeSource, selected); backup = selected; }
    await remember();
    status(kind === 'source' ? '已选择目录，点击“使用此目录”加载报表。数据库配置未改变。' : '备份目录已选择。保存前会先备份对应原文件。');
  }
  async function authorize() {
    await permission(source, true);
    if (backup) await permission(backup, true);
    if (pending) { await permission(pending.source, true); await permission(pending.backup, true); }
    status('目录授权已确认；可加载目录或继续核对待恢复文件。');
  }
  async function fileAt(root, path, create = false) {
    const parts = safePath(path).split('/');
    let current = root;
    for (const part of parts.slice(0, -1)) current = await current.getDirectoryHandle(part, { create });
    return current.getFileHandle(parts[parts.length - 1], { create });
  }
  async function bytesOf(handle) { return new Uint8Array(await (await handle.getFile()).arrayBuffer()); }
  async function write(handle, bytes) {
    const stream = await handle.createWritable({ keepExistingData: false });
    try { await stream.write(bytes); await stream.close(); }
    catch (error) { try { await stream.abort(); } catch { /* Closed or aborted already. */ } throw error; }
  }
  async function* scan(directory, prefix = '') {
    for await (const [name, handle] of directory.entries()) {
      const path = safePath(prefix + name);
      if (handle.kind === 'directory') yield* scan(handle, path + '/');
      else if (handle.kind === 'file' && /\.xml$/i.test(name)) yield [path, handle];
    }
  }
  function decodeXml(bytes) {
    let encoding = 'utf-8';
    if (bytes[0] === 255 && bytes[1] === 254) encoding = 'utf-16le';
    else if (bytes[0] === 254 && bytes[1] === 255) encoding = 'utf-16be';
    else if (bytes[0] === 60 && bytes[1] === 0 && bytes[2] === 63) encoding = 'utf-16le';
    else if (bytes[0] === 0 && bytes[1] === 60 && bytes[2] === 0 && bytes[3] === 63) encoding = 'utf-16be';
    else {
      const header = Array.from(bytes.subarray(0, 512), x => String.fromCharCode(x)).join('');
      const match = /^\s*(?:\u00ef\u00bb\u00bf)?<\?xml\s[^>]*encoding\s*=\s*['"]([^'"]+)['"]/i.exec(header);
      if (match) encoding = match[1];
    }
    return new TextDecoder(encoding, { fatal: true }).decode(bytes).replace(/^\uFEFF/, '');
  }
  function inspectXml(text) {
    // DTD declarations are legal only before the document root. Do not mistake
    // a literal inside an XML comment, CDATA, or SQL string for a declaration.
    let prolog = text.replace(/^\s+/, '');
    for (;;) {
      const match = /^(?:<\?[\s\S]*?\?>|<!--[\s\S]*?-->)\s*/.exec(prolog);
      if (!match) break;
      prolog = prolog.slice(match[0].length);
    }
    if (/^<!DOCTYPE\b/i.test(prolog)) throw new Error('不允许包含 DTD 的 XML');
    const doc = new DOMParser().parseFromString(text, 'application/xml');
    if (doc.getElementsByTagName('parsererror').length || !doc.documentElement) {
      // The old Core classifies FarPoint files after reading the root start tag;
      // some HIS layouts contain invalid character references later in the body.
      // Validate the actual prolog and complete root tag with the XML parser, but
      // never apply this fallback to query definitions or accept a DTD.
      let quote = '', end = -1;
      if (prolog.startsWith('<') && !/^<[!?/]/.test(prolog)) {
        for (let i = 1; i < prolog.length; i++) {
          const ch = prolog[i];
          if (quote) { if (ch === quote) quote = ''; }
          else if (ch === '"' || ch === "'") quote = ch;
          else if (ch === '>') { end = i; break; }
          else if (ch === '<') break;
        }
      }
      if (end >= 0) {
        const tag = prolog.slice(0, end + 1);
        const closed = /\/\s*>$/.test(tag) ? tag : tag.slice(0, -1) + '/>';
        const prefix = text.slice(0, text.length - prolog.length);
        const rootDoc = new DOMParser().parseFromString(prefix + closed, 'application/xml');
        const root = rootDoc.documentElement;
        if (!rootDoc.getElementsByTagName('parsererror').length && root?.localName === 'Spread' && root.getAttribute('class') === 'FarPoint.Win.Spread.FpSpread')
          return { kind: 'layout' };
      }
      throw new Error('XML 格式无效');
    }
    const root = doc.documentElement;
    if (root.localName === 'Spread' && root.getAttribute('class') === 'FarPoint.Win.Spread.FpSpread') return { kind: 'layout' };
    if (root.localName !== 'ReportQueryInfo') return { kind: 'other' };
    const child = (el, name) => Array.from(el?.children || []).find(x => x.tagName === name);
    const source = child(root, 'QueryDataSource');
    const hasSql = Array.from(source?.children || []).some(x => x.tagName === 'QueryDataSource' && (child(x, 'Sql')?.textContent || '').trim());
    const info = child(root, 'ReportInfo');
    return { kind: hasSql ? 'query' : 'other', detail: /^true$/i.test((child(info, 'IsDetail')?.textContent || '').trim()) ? (child(info, 'DetailDirectory')?.textContent || '') : '' };
  }
  function reportItem(path, locations = []) {
    const name = path.split('/').pop().replace(/\.xml$/i, '').split('查询设置').join('');
    return { id: 'local:' + path, name, aliases: '', notes: '', path, locations, status: '本机报表', demo: false, issues: [], verified: false };
  }
  async function readIndexEntry(path, handle) {
    const file = await handle.getFile();
    if (!file.size || file.size > MAX_FILE) throw new Error('XML 为空或超过 32 MiB：' + path);
    const bytes = new Uint8Array(await file.arrayBuffer());
    return { path, size: file.size, modified: file.lastModified, hash: await digest(bytes), ...inspectXml(decodeXml(bytes)) };
  }
  async function scanIndex(root, progress, generation, cache, request) {
    indexScanning = true; indexFailed = false; locationWarning = ''; notify();
    const entries = new Map(), reports = [];
    try {
      let count = 0;
      for await (const [path, handle] of scan(root)) {
        if (generation !== scanGeneration) return;
        const key = path.toLowerCase();
        if (entries.has(key)) throw new Error('目录存在大小写冲突的 XML 文件：' + path);
        const file = await handle.getFile(), previous = cache.get(key);
        let entry;
        try {
          const reusable = previous && previous.path === path && previous.size === file.size && previous.modified === file.lastModified && /^[a-f0-9]{64}$/i.test(previous.hash || '');
          // A confirmed hash-based location must match current bytes even when an
          // external program preserves file size and timestamp.
          const unchangedQuery = reusable && previous.kind === 'query' ? await digest(new Uint8Array(await file.arrayBuffer())) === previous.hash : true;
          entry = reusable && unchangedQuery ? { ...previous, locations: [] } : await readIndexEntry(path, handle);
        } catch (error) {
          entry = { path, size: file.size, modified: file.lastModified, kind: 'unknown', error: error.message };
        }
        entries.set(key, entry);
        if (entry.kind === 'query') reports.push(reportItem(path));
        count++;
        if (count % 50 === 0) {
          if (!cache.size) { indexFiles = new Map(entries); indexReports = reports.slice(); indexNotify(); }
          progress?.('正在本机建立索引：' + count + ' 份 XML，已识别 ' + reports.length + ' 张报表。');
          await yieldUi();
        }
      }
      if (!count) throw new Error('所选目录没有 XML 文件。');
      if (generation !== scanGeneration) return;
      indexFiles = entries; indexReports = reports;
      let locationError = '';
      const locationWarnings = new Set();
      const queries = Array.from(entries.values()).filter(e => e.kind === 'query');
      try {
        for (let i = 0; i < queries.length; i += 100) {
          if (generation !== scanGeneration) return;
          progress?.('正在匹配已有报表位置：' + Math.min(i + 100, queries.length) + ' / ' + queries.length);
          const batch = queries.slice(i, i + 100), result = await request('localLocations', { items: batch.map(e => ({ path: e.path, hash: e.hash })) });
          if (!Array.isArray(result?.items) || result.items.length !== batch.length) throw new Error('位置结果不完整');
          for (const warning of result.warnings || []) locationWarnings.add(String(warning));
          const mapping = new Map(result.items.map(item => [String(item.path).toLowerCase(), item]));
          if (mapping.size !== batch.length) throw new Error('位置结果包含重复路径');
          for (const entry of batch) {
            const item = mapping.get(entry.path.toLowerCase());
            if (!item || !Array.isArray(item.locations)) throw new Error('位置结果无法匹配');
            entry.locations = item.locations;
          }
          indexReports = queries.map(e => reportItem(e.path, e.locations || [])); indexNotify();
        }
      } catch (error) { locationError = '；HIS 位置搜索不完整：' + error.message; }
      if (locationWarnings.size) locationError += '；HIS 位置搜索可能不完整：' + Array.from(locationWarnings).join('；');
      locationWarning = locationError;
      if (generation !== scanGeneration) return;
      indexReports = queries.map(e => reportItem(e.path, e.locations || [])); indexScanning = false;
      try { await stored('index', { source: root, entries: Array.from(entries.values()) }); } catch { /* cache is optional */ }
      const unknown = Array.from(entries.values()).filter(e => e.kind === 'unknown');
      status('本机索引已完成：' + count + ' 份 XML，' + reports.length + ' 张报表' + (unknown.length ? '，' + unknown.length + ' 份无法识别（例如：' + unknown.slice(0, 3).map(e => e.path).join('、') + '）' : '') + locationError + '；选择报表后才上传所需文件。');
      indexNotify();
    } catch (error) {
      if (generation !== scanGeneration) return;
      indexScanning = false; indexFailed = true;
      status('本机索引未完成，不能安全确定关联文件：' + error.message + '。请处理后重新扫描。');
      indexNotify();
    }
  }
  async function performLoad(request, progress, force = false) {
    if (pending) throw new Error('请先处理未完成的本机保存，再更换或扫描目录。');
    if (!supported()) throw new Error('当前浏览器不支持安全的本机目录访问。');
    await permission(source); await separate(source, backup);
    const picked = source;
    let cached = null;
    try { const value = await stored('index'); if (!force && value?.source && await picked.isSameEntry(value.source) && Array.isArray(value.entries)) cached = value.entries; } catch { /* Scan without cache. */ }
    if (attached || staging) await request('localUseServer', {});
    active = true; attached = false; staging = false; activeSource = picked; activeFiles = new Map(); baseline = new Map(); needsScan = false; workspaceLost = false; serverReportId = localReportId = null;
    const cache = new Map((cached || []).map(entry => [entry.path.toLowerCase(), entry]));
    indexFiles = new Map(cache); indexReports = Array.from(cache.values()).filter(entry => entry.kind === 'query').map(entry => reportItem(entry.path));
    const generation = ++scanGeneration;
    void scanIndex(picked, progress, generation, cache, request);
    status(cached ? '已显示本机缓存，正在后台核对目录；核对完成后可选择报表。' : '正在本机建立索引，报表会逐步显示；核对完成后可选择。');
    indexNotify();
    return { reports: indexReports.slice() };
  }
  async function candidatePaths(root, names) {
    const paths = [], seen = new Set();
    for await (const [path] of scan(root))
      if (names.some(n => n.toLowerCase() === path.split('/').pop().toLowerCase())) {
        if (seen.has(path.toLowerCase())) throw new Error('关联候选路径存在大小写冲突：' + path);
        seen.add(path.toLowerCase()); paths.push(path);
      }
    return paths.sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
  }
  async function verifyCandidates() {
    if (!attached) return;
    const paths = await candidatePaths(activeSource, candidateNames);
    if (JSON.stringify(paths) !== JSON.stringify(candidateSnapshot)) {
      needsScan = true; notify(); throw new Error('本机报表的关联候选文件集合已变化，请重新扫描目录。');
    }
    for (const path of paths)
      if (await digest(await bytesOf(await fileAt(activeSource, path))) !== candidateHashes.get(path)) {
        needsScan = true; notify(); throw new Error('本机报表关联候选已变化，请重新扫描目录：' + path);
      }
  }
  async function performOpenReport(id, request) {
    if (!active || !activeSource || workspaceLost) throw new Error('本机目录尚未启用。');
    if (indexScanning || indexFailed) throw new Error('本机目录索引尚未完成或存在错误，暂不能安全确认关联文件。');
    if (pending) throw new Error('请先完成本机保存或恢复。');
    await permission(activeSource);
    const query = indexFiles.get(String(id).slice(6).toLowerCase());
    if (!String(id).startsWith('local:') || !query || query.kind !== 'query') throw new Error('本机报表不在当前索引中，请重新扫描。');
    const queryHandle = await fileAt(activeSource, query.path), current = await readIndexEntry(query.path, queryHandle);
    if (current.size !== query.size || current.modified !== query.modified || current.kind !== 'query' || current.detail !== query.detail)
      throw new Error('本机报表已变化，请重新扫描目录：' + query.path);
    const names = [];
    const base = query.path.split('/').pop().replace(/\.xml$/i, '');
    if (base.endsWith('查询设置')) names.push(base.slice(0, -4) + '报表设置.xml');
    if (query.detail) {
      const reference = query.detail.trim().replace(/\\/g, '/');
      if (!reference.startsWith('//') && !reference.includes(':') && !reference.split('/').includes('..') && !/[\*?"<>|]/.test(reference) && /\.xml$/i.test(reference))
        names.push(reference.split('/').pop());
    }
    const paths = await candidatePaths(activeSource, names), related = [], relatedHashes = new Map();
    for (const path of paths) {
      try {
        const handle = await fileAt(activeSource, path);
        const entry = await readIndexEntry(path, handle);
        related.push(entry);
        relatedHashes.set(path, entry.hash);
      }
      catch (error) { throw new Error('可能的关联 XML 无法识别：' + path + '；' + error.message); }
    }
    const chosen = [query, ...related.filter(e => e.kind === 'layout')];
    const files = new Map(), hashes = new Map();
    try {
      staging = true; await request('localBegin', { name: activeSource.name });
      for (const entry of chosen) {
        const handle = await fileAt(activeSource, entry.path), file = await handle.getFile();
        if (file.size !== entry.size || file.lastModified !== entry.modified) throw new Error('本机关联 XML 已变化，请重新扫描：' + entry.path);
        const bytes = new Uint8Array(await file.arrayBuffer());
        const hash = await digest(bytes);
        if (hash !== (entry.kind === 'query' ? current.hash : relatedHashes.get(entry.path)))
          throw new Error('载入期间本机 XML 发生变化：' + entry.path);
        if (entry.kind === 'layout' && inspectXml(decodeXml(bytes)).kind !== 'layout') throw new Error('本机关联 XML 已变化，请重新扫描：' + entry.path);
        hashes.set(entry.path, hash);
        for (let offset = 0; offset < bytes.length; offset += CHUNK)
          await request('localUpload', { path: entry.path, offset, total: bytes.length, content: encoded(bytes.subarray(offset, offset + CHUNK)) });
        files.set(entry.path.toLowerCase(), { path: entry.path, handle });
      }
      if (JSON.stringify(paths) !== JSON.stringify(await candidatePaths(activeSource, names)))
        throw new Error('载入期间关联文件集合发生变化，请重新扫描目录。');
      for (const path of paths)
        if (await digest(await bytesOf(await fileAt(activeSource, path))) !== relatedHashes.get(path))
          throw new Error('载入期间关联文件发生变化：' + path);
      for (const { path, handle } of files.values())
        if (await digest(await bytesOf(handle)) !== hashes.get(path)) throw new Error('载入期间本机 XML 发生变化：' + path);
      const result = await request('localActivate', {});
      const candidates = (result?.queryPaths || []).filter(r => String(r.path || '').replace(/\\/g, '/').toLowerCase() === query.path.toLowerCase());
      if (candidates.length !== 1) throw new Error('服务端无法唯一识别所选本机报表。');
      activeFiles = files; baseline = hashes; attached = true; staging = false; serverReportId = candidates[0].id; localReportId = id; candidateNames = names; candidateSnapshot = paths; candidateHashes = relatedHashes; queryDetail = current.detail;
      status('本机报表已按需载入，上传 ' + files.size + ' 份 XML。');
      return serverReportId;
    } catch (error) { attached = false; needsScan = true; status('本机报表载入未完成：' + error.message + '。请重新扫描，原文件未改变。'); throw error; }
  }
  async function performExportQueries(ids, request, all) {
    if (!active || indexScanning || indexFailed || pending) throw new Error('请先完成本机索引和保存，再导出 SQL。');
    if (!ids.length || new Set(ids).size !== ids.length) throw new Error('请选择有效的本机报表。');
    await permission(activeSource);
    const currentPaths = [];
    for await (const [path] of scan(activeSource)) currentPaths.push(path.toLowerCase());
    if (currentPaths.length !== indexFiles.size || currentPaths.some(path => !indexFiles.has(path)))
      throw new Error('本机目录文件集合已变化，请先重新扫描。');
    async function verifyAll() {
      const realQueries = [];
      for await (const [path, handle] of scan(activeSource)) {
        let entry;
        try { entry = await readIndexEntry(path, handle); }
        catch { throw new Error('目录含无法识别的 XML，无法保证全部导出的范围完整：' + path); }
        if (entry.kind === 'query') realQueries.push('local:' + path);
      }
      if (realQueries.length !== ids.length || realQueries.some(id => !ids.includes(id)))
        throw new Error('报表范围已变化，请重新扫描后导出全部 SQL。');
    }
    if (all) await verifyAll();
    const selected = ids.map(id => indexFiles.get(String(id).slice(6).toLowerCase()));
    if (selected.some((entry, i) => !String(ids[i]).startsWith('local:') || !entry || entry.kind !== 'query'))
      throw new Error('所选报表无效，请重新扫描。');
    try {
      await request('localBegin', { name: activeSource.name });
      const hashes = new Map();
      for (const entry of selected) {
        const handle = await fileAt(activeSource, entry.path), current = await readIndexEntry(entry.path, handle);
        if (current.kind !== 'query' || current.size !== entry.size || current.modified !== entry.modified)
          throw new Error('本机报表已变化，请重新扫描：' + entry.path);
        const bytes = await bytesOf(handle);
        if (await digest(bytes) !== current.hash) throw new Error('读取期间本机报表已变化：' + entry.path);
        hashes.set(entry.path, current.hash);
        for (let offset = 0; offset < bytes.length; offset += CHUNK)
          await request('localUpload', { path: entry.path, offset, total: bytes.length, content: encoded(bytes.subarray(offset, offset + CHUNK)) });
      }
      for (const entry of selected)
        if (await digest(await bytesOf(await fileAt(activeSource, entry.path))) !== hashes.get(entry.path))
          throw new Error('导出期间本机报表已变化：' + entry.path);
      if (all) await verifyAll();
      const result = await request('localActivate', {}), mapping = new Map((result.queryPaths || []).map(x => [x.path.toLowerCase(), x.id]));
      const mapped = selected.map(entry => mapping.get(entry.path.toLowerCase()));
      if (mapped.some(id => !id) || mapping.size !== selected.length) throw new Error('导出报表识别不完整，未生成部分文件。');
      return mapped;
    } finally { /* outer operation lock is released by exclusive() */ }
  }
  async function performUseServer(request) {
    if (pending) throw new Error('请先处理未完成的本机保存。');
    const result = await request('localUseServer', {});
    scanGeneration++; indexScanning = false; indexFiles.clear(); indexReports = []; attached = false; serverReportId = localReportId = null;
    active = false; staging = false; activeSource = null; activeFiles.clear(); baseline.clear(); needsScan = false; workspaceLost = false;
    const capability = await window.reportDesk?.ready;
    status(capability?.serverReportsEnabled === true ? '已切回服务器报表目录。本机目录文件未改变。' : '当前目录已关闭，临时副本已释放。本机 XML、备份和已选目录保留。'); return result;
  }
  async function exclusive(action) {
    if (working) throw new Error('本机目录操作正在进行，请稍候。');
    working = true; notify();
    try { return await action(); } finally { working = false; notify(); }
  }
  const choose = kind => exclusive(() => performChoose(kind));
  const load = (request, progress, force) => exclusive(() => performLoad(request, progress, force));
  const useServer = request => exclusive(() => performUseServer(request));
  const openReport = (id, request) => exclusive(() => performOpenReport(id, request));
  const exportQueries = (ids, request, all) => exclusive(() => performExportQueries(ids, request, all));
  async function fresh() {
    if (workspaceLost) throw new Error('本页临时工作区已失效，请保留草稿并重新扫描本机目录。');
    if (!active) return;
    if (needsScan) throw new Error('本机文件状态已变化，请在“报表来源 → 打开本机目录”中重新扫描目录。');
    await permission(activeSource);
    await verifyCandidates();
    // Check all related XML before opening an editor. Never silently edit an old
    // server snapshot while another process has changed a local companion file.
    for (const { path, handle } of activeFiles.values()) {
      if (await digest(await bytesOf(handle)) !== baseline.get(path)) {
        needsScan = true; notify(); throw new Error('本机 XML 已变化：' + path + '。请先重新扫描目录，当前文件未被覆盖。');
      }
    }
  }
  async function verifyChecks(checks) {
    if (!active) return;
    await permission(activeSource);
    await verifyCandidates();
    for (const { path, handle } of activeFiles.values())
      if (await digest(await bytesOf(handle)) !== baseline.get(path)) {
        needsScan = true; notify(); throw new Error('本机 XML 已变化：' + path + '。请重新扫描目录。');
      }
    for (const check of checks || []) {
      safePath(check.path);
      if (!activeFiles.has(check.path.toLowerCase()) || !/^[a-f0-9]{64}$/i.test(check.hash) || await digest(await bytesOf(await fileAt(activeSource, check.path))) !== check.hash.toLowerCase()) {
        needsScan = true; notify(); throw new Error('本机报表或关联 XML 已变化：' + check.path + '。请重新扫描目录，未覆盖原文件。');
      }
    }
  }
  async function saveAccess() {
    await inspectPending();
    if (!active || !activeSource) throw new Error('尚未加载本机目录。');
    if (pending || working) throw new Error('尚有未完成的本机保存，请在“报表来源 → 打开本机目录”中核对。');
    if (needsScan) throw new Error('请先重新扫描本机目录。');
    await permission(activeSource); await permission(backup); await separate(activeSource, backup);
    await fresh();
  }
  async function commitRecord(record, request) {
    await permission(record.source);
    const hashes = [];
    for (const file of record.files) {
      const hash = await digest(await bytesOf(await fileAt(record.source, file.path)));
      if (hash !== file.newHash.toLowerCase()) throw new Error('本机文件与待确认版本不一致：' + file.path + '。请先恢复或人工核对。');
      hashes.push({ path: file.path, hash });
    }
    const result = await request('localCommit', { saveId: record.saveId, files: hashes });
    for (const file of record.files) {
      baseline.set(file.path, file.newHash.toLowerCase());
      if (candidateHashes.has(file.path)) candidateHashes.set(file.path, file.newHash.toLowerCase());
    }
    for (const file of record.files) {
      if (localReportId !== 'local:' + file.path) continue;
      try {
        const updated = await readIndexEntry(file.path, await fileAt(record.source, file.path));
        if (updated.kind !== 'query' || updated.detail !== queryDetail) needsScan = true;
        else {
          indexFiles.set(file.path.toLowerCase(), updated);
          indexReports = indexReports.map(item => item.id === localReportId ? reportItem(file.path) : item);
          indexNotify();
        }
      } catch { needsScan = true; }
    }
    lastSaveId = record.saveId;
    pending = null;
    let cleanupFailed = false;
    try { await stored('pending', undefined, true); } catch { cleanupFailed = true; }
    notify();
    status('本机文件已保存并校验。' + (needsScan ? '查询定义可能改变了关联，请关闭编辑器后重新扫描目录。' : '') + (cleanupFailed ? '浏览器恢复记录未清理；下次请核对并确认已保存版本。' : ''));
    return result;
  }
  async function performSave(prepared, request) {
    if (!prepared?.localSave || typeof prepared.saveId !== 'string' || !Array.isArray(prepared.files))
      throw new Error('服务端返回的本机保存计划无效。');
    working = true; notify();
    let claimed = false, writesStarted = false;
    try {
      await verifyCandidates();
      for (const check of prepared.checks || []) {
        safePath(check.path);
        if (!activeFiles.has(check.path.toLowerCase()) || !/^[a-f0-9]{64}$/i.test(check.hash) || await digest(await bytesOf(await fileAt(activeSource, check.path))) !== check.hash.toLowerCase())
          throw new Error('报表关联文件已变化，请重新扫描：' + check.path);
      }
      if (!prepared.files.length) return await request('localCommit', { saveId: prepared.saveId, files: [] });
      const seen = new Set(), files = [];
      for (const file of prepared.files) {
        safePath(file.path);
        if (seen.has(file.path.toLowerCase()) || !activeFiles.has(file.path.toLowerCase()) || !/\.xml$/i.test(file.path)) throw new Error('保存计划包含重复或未授权文件。');
        if (!/^[a-f0-9]{64}$/i.test(file.oldHash) || !/^[a-f0-9]{64}$/i.test(file.newHash)) throw new Error('文件校验信息无效。');
        seen.add(file.path.toLowerCase());
        const bytes = decoded(file.content);
        if (await digest(bytes) !== file.newHash.toLowerCase()) throw new Error('待保存文件校验失败：' + file.path);
        const current = await bytesOf(await fileAt(activeSource, file.path));
        if (await digest(current) !== file.oldHash.toLowerCase()) throw new Error('原文件已被其他程序修改，未覆盖：' + file.path);
        files.push({ path: file.path, oldHash: file.oldHash.toLowerCase(), newHash: file.newHash.toLowerCase(), content: file.content });
      }
      const batch = new Date().toISOString().replace(/[:.]/g, '-') + '-' + owner.slice(0, 8);
      const record = { saveId: prepared.saveId, source: activeSource, backup, batch, files, owner, stage: 'backing-up' };
      await claim(record); claimed = true; pending = record; notify();
      const batchRoot = await backup.getDirectoryHandle(batch, { create: true });
      for (const file of files) {
        const original = await bytesOf(await fileAt(activeSource, file.path));
        if (await digest(original) !== file.oldHash) throw new Error('备份前原文件已变化：' + file.path);
        const target = await fileAt(batchRoot, file.path, true);
        await write(target, original);
        if (await digest(await bytesOf(target)) !== file.oldHash) throw new Error('备份校验失败；未继续保存：' + file.path);
      }
      record.stage = 'writing'; await stored('pending', record);
      await verifyCandidates();
      for (const file of files) {
        const target = await fileAt(activeSource, file.path);
        if (await digest(await bytesOf(target)) !== file.oldHash) throw new Error('保存前原文件已变化：' + file.path);
        writesStarted = true;
        await write(target, decoded(file.content));
        if (await digest(await bytesOf(target)) !== file.newHash) throw new Error('写入后校验失败：' + file.path);
      }
      record.stage = 'confirming'; await stored('pending', record);
      return await commitRecord(record, request);
    } catch (error) {
      if (!writesStarted) {
        // The server snapshot is no longer trusted after external conflict.
        try { await request('localAbort', { saveId: prepared.saveId }); needsScan = true; }
        catch { status('本机原文件尚未写入，但服务端保存状态未知；请在“报表来源 → 打开本机目录”中核对。'); }
        if (claimed) { pending = null; await stored('pending', undefined, true); }
      } else {
        status('保存尚未完成。请打开“报表来源 → 打开本机目录”，选择“核对并确认保存”或“恢复原文件”。备份批次：' + pending.batch);
      }
      throw new Error(error.message + (writesStarted ? '\n部分文件可能已写入；请通过“报表来源 → 打开本机目录”恢复，不要重复点击保存。' : '\n本机原文件未写入；请重新扫描目录。'));
    } finally { working = false; notify(); }
  }
  async function inspectPending() { pending = await stored('pending') || null; notify(); return pending; }
  async function performRecover(request, mode) {
    const record = await inspectPending();
    if (!record) throw new Error('没有待恢复的本机保存。');
    if (working) throw new Error('本机保存正在进行。');
    await permission(record.source); await permission(record.backup);
    working = true; notify();
    try {
      if (mode === 'confirm') {
        if (record.owner !== owner || !active || !activeSource || !(await activeSource.isSameEntry(record.source))) {
          for (const file of record.files)
            if (await digest(await bytesOf(await fileAt(record.source, file.path))) !== file.newHash) throw new Error('文件尚未全部保存成功：' + file.path + '。请恢复原文件后重新编辑。');
          pending = null; await stored('pending', undefined, true); needsScan = active;
          status('本机已保存版本已核对。请使用此目录重新加载报表；旧页面的服务端工作区将独立释放。');
          return { confirmed: true, reloaded: false };
        }
        return await commitRecord(record, request);
      }
      const batchRoot = await record.backup.getDirectoryHandle(record.batch);
      // Validate the entire recovery first. Never replace a third-party version.
      const restore = [];
      for (const file of record.files) {
        const current = await digest(await bytesOf(await fileAt(record.source, file.path)));
        if (current !== file.oldHash && current !== file.newHash) throw new Error('此文件又被其他程序修改，不能自动恢复：' + file.path + '。请对照备份人工处理。');
        if (current === file.newHash) {
          const original = await bytesOf(await fileAt(batchRoot, file.path));
          if (await digest(original) !== file.oldHash) throw new Error('备份内容不匹配，不能恢复：' + file.path);
          restore.push({ file, original });
        }
      }
      for (const { file, original } of restore) {
        const target = await fileAt(record.source, file.path);
        if (await digest(await bytesOf(target)) !== file.newHash) throw new Error('恢复前文件又发生变化：' + file.path);
        await write(target, original);
        if (await digest(await bytesOf(target)) !== file.oldHash) throw new Error('恢复后校验失败：' + file.path);
      }
      // After a page reload there is a new tab context. Old server context will
      // expire independently; a fresh rescan is required in either case.
      if (record.owner === owner && active) await request('localAbort', { saveId: record.saveId });
      pending = null; await stored('pending', undefined, true); needsScan = active;
      status('原文件已恢复。请重新扫描目录；备份仍保留。');
      return { restored: true };
    } finally { working = false; notify(); }
  }
  async function withWriteLock(action) {
    if (!navigator.locks) throw new Error('当前浏览器缺少多页面写入保护，请更换支持的浏览器版本。');
    return navigator.locks.request('ReportDeskLocalSave', { ifAvailable: true }, lock => {
      if (!lock) throw new Error('另一个页面正在保存或恢复本机 XML，请等它完成。');
      return action();
    });
  }
  const save = async (prepared, request) => {
    let entered = false;
    try { return await withWriteLock(() => { entered = true; return performSave(prepared, request); }); }
    catch (error) {
      if (!entered) { try { await request('localAbort', { saveId: prepared.saveId }); needsScan = true; notify(); } catch { /* State inspection is available in settings. */ } }
      throw error;
    }
  };
  const recover = (request, mode) => withWriteLock(() => performRecover(request, mode));
  async function performInspectServer(request) {
    await inspectPending();
    if (pending) { status('存在本机恢复记录，请核对并确认保存或恢复原文件。'); return; }
    if (!active) { status(workspaceLost ? '本页临时工作区已失效，没有待恢复记录。请先保留草稿，再重新扫描本机目录。' : '当前未启用本机目录，没有本页保存需要核对。'); return; }
    const server = await request('localStatus', {});
    if (server.pending) {
      await permission(activeSource);
      let allOld = true, allNew = true;
      for (const file of server.pending.files || []) {
        const hash = await digest(await bytesOf(await fileAt(activeSource, file.path)));
        allOld = allOld && hash === file.oldHash.toLowerCase();
        allNew = allNew && hash === file.newHash.toLowerCase();
      }
      if (!allOld) {
        if (!allNew) throw new Error('本机文件包含未完成或其他程序的修改。请对照独立备份目录人工核对；未自动覆盖或撤回。');
        // Another tab may already have verified and cleared the durable record.
        // Reconcile this tab's candidate using bytes on disk, without rewriting.
        await commitRecord({ ...server.pending, source: activeSource }, request);
        return;
      }
      await request('localAbort', { saveId: server.pending.saveId }); needsScan = true;
      status('确认本机原文件未写入，已释放服务端保存准备。请重新扫描目录。');
    } else {
      needsScan = server.requiresRescan === true;
      status(needsScan ? '服务端要求重新扫描目录，请保留编辑草稿后重新扫描。' : '本页没有待确认保存。');
    }
  }
  const inspectServer = request => withWriteLock(() => performInspectServer(request));
  function guard(method) {
    if (active && blocked.has(method)) throw new Error('本机目录模式不使用服务器导入、同步或发布。请在“报表来源 → 打开本机目录”中重新扫描本机目录。');
    if ((workspaceLost || staging || active && (pending || needsScan)) && !['settings', 'saveSettings', 'testConnection', 'activateConnection', 'discoverTns', 'tnsAliases', 'localBegin', 'localUpload', 'localActivate', 'localStatus', 'localCommit', 'localAbort', 'localUseServer', 'sqlEditorDirty', 'xmlEditorDirty', 'sqlEditorDiscard', 'cancel', 'copy', 'clear'].includes(method))
      throw new Error(pending ? '本机保存尚未完成，请在“报表来源 → 打开本机目录”中核对或恢复。' : workspaceLost ? '本页临时工作区已失效，请先保留草稿，再重新扫描本机目录；旧查询和编辑不会自动重试。' : '本机目录需要重新扫描，暂不能继续使用旧报表。');
  }
  const ready = (async () => {
    try { const saved = await stored('handles'); if (saved) { source = saved.source || null; backup = saved.backup || null; remembered = !!source; } await inspectPending(); }
    catch (error) { message = error.message; }
    notify();
  })();
  window.reportDeskLocal = Object.freeze({ ready, supported, choose, authorize, load, openReport, exportQueries, useServer, saveAccess, save, fresh, verifyChecks, guard, recover, inspectPending, inspectServer, markWorkspaceLost,
    get active() { return active; }, get attached() { return attached; }, get indexReports() { return indexReports.slice(); }, get localReportId() { return localReportId; }, get serverReportId() { return serverReportId; }, get pending() { return !!pending; }, get working() { return working; },
    get state() { return { supported: supported(), active, staging, indexScanning, indexFailed, locationWarning, source: source?.name || '', backup: backup?.name || '', activeName: activeSource?.name || '', remembered, needsScan, workspaceLost, working, pending: pending ? { name: pending.source.name, batch: pending.batch, stage: pending.stage, files: pending.files.map(f => f.path) } : null, message }; },
    get lastSaveId() { return lastSaveId; }
  });
  window.addEventListener('reportdesk-workspace-lost', markWorkspaceLost);
  window.addEventListener('beforeunload', event => { if (working || pending) { event.preventDefault(); event.returnValue = ''; } });
})();
