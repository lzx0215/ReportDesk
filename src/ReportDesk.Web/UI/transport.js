'use strict';
// Browser adapter for the existing preload contract. Never replay a failed POST.
(() => {
  const assetRoot = new URL('.', document.currentScript.src);
  const siteRoot = new URL('../', assetRoot);
  const adminPage = /\/admin(?:\/|\/index\.html)?$/i.test(location.pathname);
  const publicMethods = new Set(['bootstrap', 'list', 'select', 'definition', 'relatedFiles', 'query', 'view', 'page', 'lookup', 'lookupPage', 'lookupAll', 'clear', 'export']);
  const adminMethods = new Set(['settings', 'saveSettings', 'testConnection', 'import', 'browseReports', 'uploadImport', 'checkNewReports', 'recheck', 'reloadReport', 'sqlEditorOpen', 'sqlEditorCheck', 'sqlEditorSave', 'sqlEditorReveal', 'layoutPreview', 'layoutSave', 'discoverTns', 'tnsAliases', 'syncStatus', 'syncNow', 'resolveSyncConflict', 'openLogs']);
  const localAdminMethods = new Set(['sqlEditorDirty', 'sqlEditorDiscard', 'pickTns']);
  const listeners = { progress: new Set(), fatal: new Set() };
  const active = new Set();
  const definitionHashes = new Map();
  const tabId = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
  let session, editor = null, layout = null, dirty = false;
  const success = data => ({ ok: true, data });
  const failure = (message, cancelled = false) => ({ ok: false, message, cancelled });
  const emit = (kind, message) => { for (const listener of listeners[kind]) listener(message); };
  const errorMessage = (value, fallback) => typeof value === 'string' && value ? value : typeof value?.message === 'string' ? value.message : fallback;
  const pause = () => new Promise(resolve => setTimeout(resolve, 500));
  const usesAdminRoute = method => adminMethods.has(method) || (method === 'relatedFiles' && (adminPage || session?.admin === true));
  function endpoint(path) { return new URL(path, siteRoot).href; }
  async function request(path, method = 'GET', body) {
    const headers = { Accept: 'application/json' };
    if (method !== 'GET') headers['X-ReportDesk-CSRF'] = session.csrf;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    let response;
    try {
      response = await fetch(endpoint(path), { method, credentials: 'same-origin', cache: 'no-store', redirect: 'error', headers, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    } catch {
      throw new Error(method === 'POST' ? '请求中断，服务端可能已经受理；请检查状态后再操作，不要重复保存。' : '无法读取服务端状态，请检查内网连接。');
    }
    let data;
    try { data = await response.json(); }
    catch { throw new Error('服务端未返回有效 JSON（HTTP ' + response.status + '）。'); }
    if (!response.ok) {
      if (response.status === 404 && path.startsWith('api/tasks/')) throw new Error('会话或任务已失效，可能已被释放或 IIS 已回收；请重新查询。不会自动重跑或访问其他会话任务。');
      const message = errorMessage(data?.message || data?.error, '请求被拒绝（HTTP ' + response.status + '）。');
      if (response.status === 401 || response.status === 403) emit('fatal', message);
      throw new Error(message);
    }
    return data;
  }
  const ready = request('api/session').then(data => {
    const value = data?.ok === true ? data.data : data;
    if (!value || typeof value.csrf !== 'string' || !value.csrf) throw new Error('会话初始化失败，请刷新页面。');
    session = { csrf: value.csrf, admin: value.admin === true, offline: value.offline === true,
      idleMinutes: Number.isFinite(value.idleMinutes) && value.idleMinutes > 0 ? value.idleMinutes : 60 };
    return { admin: session.admin, offline: session.offline, idleMinutes: session.idleMinutes };
  });
  // The loader consumes the rejection; keep initialization failures out of global errors.
  ready.catch(() => {});
  async function cancelOperation(operation) {
    operation.cancelRequested = true;
    if (!operation.id || operation.cancelling) return;
    operation.cancelling = true;
    try {
      const result = await request('api/tasks/' + encodeURIComponent(operation.id) + '?tabId=' + encodeURIComponent(tabId), 'DELETE');
      if (result?.ok === false) throw new Error(errorMessage(result.message || result.error, '取消请求失败。'));
    } catch (error) { operation.cancelling = false; throw error; }
  }
  async function remote(method, args) {
    const operation = { id: null, cancelRequested: false, cancelling: false };
    active.add(operation);
    try {
      let result = await request('api/' + (usesAdminRoute(method) ? 'admin/' : '') + method, 'POST', { args, tabId });
      if (result?.ok === true && result.jobId) {
        operation.id = String(result.jobId);
        if (operation.cancelRequested) await cancelOperation(operation);
        for (;;) {
          let state;
          try { state = await request('api/tasks/' + encodeURIComponent(operation.id) + '?tabId=' + encodeURIComponent(tabId)); }
          catch (error) { throw new Error(error.message + ' 任务完成情况未知；不要重复提交查询或保存。'); }
          if (state?.ok === true && state.data?.state) state = state.data;
          if (state?.message) emit('progress', state.message);
          if (state?.state === 'succeeded') { result = success(state.data); break; }
          if (state?.state === 'cancelled') return failure(errorMessage(state.message, '操作已取消。'), true);
          if (state?.state === 'failed') return failure(errorMessage(state.error || state.message, '操作失败。'));
          if (state?.state !== 'running') throw new Error('无法识别任务状态；请核对服务端任务，勿重复提交。');
          await pause();
        }
      }
      if (typeof result?.ok !== 'boolean') throw new Error('服务端返回格式无效。');
      return result.ok ? success(result.data) : failure(errorMessage(result.message || result.error, '操作失败。'), result.cancelled === true);
    } finally { active.delete(operation); }
  }
  async function copy(text) {
    if (typeof text !== 'string') throw new Error('复制内容无效。');
    try { if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(text); return; } } catch { /* HTTP intranet / denied clipboard: use a local selection. */ }
    const previous = document.activeElement;
    const input = document.createElement('textarea'); input.value = text; input.className = 'web-clipboard';
    (document.querySelector('dialog[open]') || document.body).append(input); input.focus(); input.select();
    try { if (!document.execCommand('copy')) throw new Error('浏览器拒绝复制，请允许剪贴板访问或使用院内 HTTPS。'); }
    finally { input.remove(); previous?.focus(); }
  }
  function download(url) {
    const target = new URL(url, siteRoot);
    if (target.origin !== siteRoot.origin || !target.pathname.startsWith(siteRoot.pathname + 'api/') || target.username || target.password)
      throw new Error('服务端返回了不允许的下载地址。');
    const anchor = document.createElement('a'); anchor.href = target.href; anchor.download = ''; anchor.rel = 'noopener';
    document.body.append(anchor); anchor.click(); anchor.remove();
  }
  function checkEditor(args) {
    if (!editor?.token || editor.reportId !== args.reportId || editor.token !== args.token) throw new Error('编辑会话已失效，请保留草稿并重新打开。');
    return editor;
  }
  async function call(method, args = {}) {
    try {
      // Clipboard must run in the user's activation; it never reaches the server.
      if (method === 'copy') { await copy(args.text); return success({}); }
      if (method === 'logClient') return success({}); // Never upload arbitrary browser errors.
      await ready;
      if ((usesAdminRoute(method) || localAdminMethods.has(method)) && !session.admin) return failure('维护功能未获授权。');
      if (method === 'cancel') { await Promise.all([...active].map(cancelOperation)); return success({}); }
      if (method === 'sqlEditorDirty') { dirty = args.dirty === true; return success({}); }
      if (method === 'sqlEditorDiscard') return success({ discard: !dirty || window.confirm('当前 SQL 或列设置尚未保存。是否放弃草稿？') });
      if (method === 'pickTns') return failure('只能选择服务器已登记的 TNS 文件，请使用“已登记来源”。');
      if (!publicMethods.has(method) && !adminMethods.has(method)) return failure('不支持的 Web 操作。');
      if (!args || typeof args !== 'object' || Array.isArray(args)) return failure('参数格式无效。');
      if (method === 'select') definitionHashes.delete(args.reportId);
      if (method === 'clear') definitionHashes.clear();
      if (method === 'query' || method === 'lookup') {
        const definitionHash = definitionHashes.get(args.reportId);
        if (!definitionHash) return failure('缺少报表定义版本标识，请重新选择报表并核对条件。');
        // Bind the source index to the exact metadata the user selected, never a newer definition.
        args = { ...args, definitionHash };
      }
      if (method === 'import') {
        let path = typeof args.path === 'string' ? args.path : null;
        if (path === null) {
          if (typeof window.reportDeskPickImport === 'function') path = await window.reportDeskPickImport(args.folder === true);
          else path = window.prompt(args.folder ? '输入服务器批准根内的相对目录，留空导入整个根目录：' : '输入服务器批准根内 XML 的相对路径：', '');
          if (path === null || path === undefined) return success(null);
        }
        const relative = String(path).trim().replaceAll('\\', '/');
        if ((!args.folder && !relative) || relative.startsWith('/') || relative.includes(':') || relative.split('/').includes('..') || /[\x00-\x1f]/.test(relative))
          return failure('请选择 XML 或文件夹，不能使用绝对路径或上一级目录。');
        args = { folder: !!args.folder, path: relative };
      }
      if (method === 'sqlEditorOpen' && dirty) return failure('请先保存或放弃当前 SQL 草稿。');
      if (method === 'resolveSyncConflict') {
        if (!Number.isInteger(args.releaseId) || args.releaseId <= 0 || typeof args.acceptHis !== 'boolean') return failure('冲突标识或处理决定无效，请刷新同步状态。');
        if (typeof args.fingerprint !== 'string' || !args.fingerprint.trim()) return failure('冲突指纹缺失或无效，请刷新同步状态。');
        args = { releaseId: args.releaseId, acceptHis: args.acceptHis, fingerprint: args.fingerprint };
        if (!window.confirm(`发布 ${args.releaseId}：${args.acceptHis ? '接受 HIS 版本，允许同步覆盖此发布中的本地冲突文件。' : '暂保留本地版本，暂不应用此发布的冲突更新。'}\n请核对目标；操作不会回写 HIS。确认此处理决定？`)) return success(null);
      }
      if (method === 'sqlEditorReveal' && editor?.reportId === args.reportId) args = { ...args, token: editor.token };
      if (method === 'layoutPreview') { checkEditor(args); layout = null; }
      if (method === 'sqlEditorSave' || method === 'layoutSave') {
        const target = checkEditor(args);
        if (method === 'layoutSave' && (!layout || layout.previewToken !== args.previewToken || layout.sql !== args.sql || layout.sourceIndex !== args.sourceIndex))
          return failure('列预览已失效，请重新同步报表列。');
        const source = target.sources?.find(s => s.index === args.sourceIndex);
        const text = method === 'layoutSave'
          ? `将保存 SQL 与报表模板。\n查询：${layout.queryPath}\n模板：${layout.layoutPath}\n请核对预览列设置。`
          : `将覆盖原 XML 中当前数据源的 SQL。\n文件：${target.path}\n数据源：${source?.name || '(未命名)'}`;
        if (!window.confirm(text + '\n不生成备份；保存不代表 SQL 或 HIS 版式已验证。确认保存？')) return success(null);
      }
      const result = await remote(method, args);
      if (!result.ok) return result;
      if (method === 'select') {
        if (result.data?.id !== args.reportId || typeof result.data?.definitionHash !== 'string' || !result.data.definitionHash.trim())
          return failure('报表元数据缺少有效 definitionHash，请重新选择报表。');
        definitionHashes.set(args.reportId, result.data.definitionHash);
      }
      if (method === 'definition') {
        if (!Array.isArray(result.data?.textParameterNames)) return failure('条件元数据缺少 textParameterNames，无法安全判断显示名称参数。');
        return success({ title: result.data.title, textParameterNames: result.data.textParameterNames });
      }
      if (method === 'sqlEditorOpen') { editor = result.data; layout = null; }
      if (method === 'layoutPreview') layout = { ...result.data, sql: args.sql, sourceIndex: args.sourceIndex };
      if (['sqlEditorSave', 'layoutSave'].includes(method) && result.data?.saved) { editor = result.data.editor; dirty = false; layout = null; }
      if (method === 'export' && result.data) {
        if (typeof result.data.downloadUrl !== 'string' || !result.data.downloadUrl) return failure('导出缺少下载地址。');
        download(result.data.downloadUrl);
      }
      return result;
    } catch (error) { return failure(errorMessage(error, '操作失败。')); }
  }
  window.addEventListener('beforeunload', event => { if (dirty || active.size) { event.preventDefault(); event.returnValue = ''; } });
  window.reportDesk = Object.freeze({
    isWeb: true, adminPage, assetRoot: assetRoot.href, ready, call,
    onProgress: callback => { listeners.progress.add(callback); return () => listeners.progress.delete(callback); },
    onFatal: callback => { listeners.fatal.add(callback); return () => listeners.fatal.delete(callback); }
  });
})();
