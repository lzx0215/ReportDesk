'use strict';
// Browser adapter for the existing preload contract. Never replay a failed POST.
(() => {
  const assetRoot = new URL('.', document.currentScript.src);
  const siteRoot = new URL('../', assetRoot);
  const adminPage = /\/admin(?:\/|\/index\.html)?$/i.test(location.pathname);
  const publicMethods = new Set(['bootstrap', 'list', 'select', 'definition', 'relatedFiles', 'query', 'view', 'page', 'lookup', 'lookupPage', 'lookupAll', 'clear', 'export']);
  const adminMethods = new Set(['settings', 'saveSettings', 'testConnection', 'import', 'browseReports', 'uploadImport', 'checkNewReports', 'recheck', 'reloadReport', 'sqlEditorOpen', 'sqlEditorCheck', 'sqlEditorSave', 'sqlEditorReveal', 'layoutPreview', 'layoutSave', 'xmlEditorOpen', 'xmlEditorCheck', 'xmlEditorSave', 'discoverTns', 'tnsAliases', 'syncStatus', 'syncNow', 'resolveSyncConflict', 'openLogs', 'exportSql', 'publishPreview', 'localBegin', 'localUpload', 'localActivate', 'localUseServer', 'localStatus', 'localCommit', 'localAbort']);
  adminMethods.add('activateConnection');
  adminMethods.add('localLocations');
  const localAdminMethods = new Set(['sqlEditorDirty', 'sqlEditorDiscard', 'xmlEditorDirty', 'pickTns']);
  const listeners = { progress: new Set(), fatal: new Set() };
  const active = new Set();
  const definitionHashes = new Map();
  const localChecks = new Map();
  const tabId = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
  let session, editor = null, layout = null, dirty = false, xmlEditor = null, xmlDirty = false;
  let lastActivity = 0, touching = false;
  const localAttached = () => !!((window.reportDeskLocal?.attached ?? window.reportDeskLocal?.active) || window.reportDeskLocal?.state?.staging);
  const workspaceLost = message => window.dispatchEvent(new CustomEvent('reportdesk-workspace-lost', { detail: { message } }));
  const success = data => ({ ok: true, data });
  const failure = (message, cancelled = false) => ({ ok: false, message, cancelled });
  const emit = (kind, message) => { for (const listener of listeners[kind]) listener(message); };
  const errorMessage = (value, fallback) => typeof value === 'string' && value ? value : typeof value?.message === 'string' ? value.message : fallback;
  const pause = () => new Promise(resolve => setTimeout(resolve, 500));
  const usesAdminRoute = method => adminMethods.has(method) || (method === 'relatedFiles' && (adminPage || session?.admin === true));
  function endpoint(path) { return new URL(path, siteRoot).href; }
  async function request(path, method = 'GET', body, isolated = false) {
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
      const localFailure = response.status === 410 || response.status === 401 &&
        (localAttached() || window.reportDeskLocal?.pending || window.reportDeskLocal?.state?.workspaceLost || path === 'api/tab-lifecycle');
      if (localFailure && !isolated) workspaceLost(message);
      if (response.status === 403 || response.status === 401 && !localFailure) emit('fatal', message);
      throw new Error(message);
    }
    return data;
  }
  async function initializeSession() {
    const data = await request('api/session');
    const value = data?.ok === true ? data.data : data;
    if (!value || typeof value.csrf !== 'string' || !value.csrf) throw new Error('会话初始化失败，请刷新页面。');
    session = { csrf: value.csrf, admin: value.admin === true, offline: value.offline === true, serverReportsEnabled: value.serverReportsEnabled === true,
      idleMinutes: Number.isFinite(value.idleMinutes) && value.idleMinutes > 0 ? value.idleMinutes : 60 };
    return { admin: session.admin, offline: session.offline, idleMinutes: session.idleMinutes,
      reportsDirectory: value.reportsDirectory, syncReady: value.syncReady === true, syncSeconds: value.syncSeconds,
      serverReportsEnabled: session.serverReportsEnabled };
  }
  const ready = initializeSession();
  // The loader consumes the rejection; keep initialization failures out of global errors.
  ready.catch(() => {});
  async function cancelOperation(operation) {
    operation.cancelRequested = true;
    if (!operation.id || operation.cancelling) return;
    operation.cancelling = true;
    try {
      const result = await request('api/tasks/' + encodeURIComponent(operation.id) + '?tabId=' + encodeURIComponent(operation.tabId), 'DELETE');
      if (result?.ok === false) throw new Error(errorMessage(result.message || result.error, '取消请求失败。'));
    } catch (error) { operation.cancelling = false; throw error; }
  }
  async function remote(method, args, operationTab = tabId, expected = localAttached()) {
    const operation = { id: null, tabId: operationTab, cancelRequested: false, cancelling: false };
    active.add(operation);
    try {
      let result = await request('api/' + (usesAdminRoute(method) ? 'admin/' : '') + method, 'POST',
        { args, tabId: operationTab, localExpected: method !== 'localBegin' && expected }, operationTab !== tabId);
      if (result?.ok === true && result.jobId) {
        operation.id = String(result.jobId);
        if (operation.cancelRequested) await cancelOperation(operation);
        for (;;) {
          let state;
          try { state = await request('api/tasks/' + encodeURIComponent(operation.id) + '?tabId=' + encodeURIComponent(operationTab), 'GET', undefined, operationTab !== tabId); }
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
  async function localRequest(method, args) {
    if (method === 'localBegin') await initializeSession();
    const result = await remote(method, args);
    if (!result.ok) throw new Error(result.message);
    return result.data;
  }
  async function exportLocalSql(args, local) {
    const ids = Array.isArray(args.reportIds) ? args.reportIds : args.reportId ? [args.reportId] : local.indexReports.map(r => r.id);
    const exportTab = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('');
    const callExport = async (method, parameters) => {
      const response = await remote(method, parameters, exportTab, method !== 'localBegin');
      if (!response.ok) throw new Error(response.message);
      return response.data;
    };
    try {
      const serverIds = await local.exportQueries(ids, callExport, !args.reportId && !args.reportIds);
      const result = await callExport('exportSql', { reportIds: serverIds });
      const target = new URL(result.downloadUrl, siteRoot);
      if (target.origin !== siteRoot.origin || !target.pathname.startsWith(siteRoot.pathname + 'api/downloads/')) throw new Error('导出下载地址无效。');
      const response = await fetch(target.href, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
      if (!response.ok) throw new Error('读取导出结果失败，请重试。');
      const blob = await response.blob(), url = URL.createObjectURL(blob);
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'ReportDesk-SQL.txt'; anchor.rel = 'noopener';
      document.body.append(anchor); anchor.click(); anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      return {};
    } finally {
      try { await request('api/tab-lifecycle', 'POST', { tabId: exportTab, args: { action: 'release' } }, true); } catch { /* idle cleanup remains in effect */ }
    }
  }
  function resetEditorState() { editor = null; xmlEditor = null; layout = null; dirty = false; xmlDirty = false; definitionHashes.clear(); localChecks.clear(); }
  async function call(method, args = {}) {
    try {
      // Clipboard must run in the user's activation; it never reaches the server.
      if (method === 'copy') { await copy(args.text); return success({}); }
      if (method === 'logClient') return success({}); // Never upload arbitrary browser errors.
      await ready;
      window.reportDeskLocal?.guard(method);
      // Only an explicit directory load may obtain a fresh session. Never replay
      // a failed query, upload, or save after a recycle or an idle expiry.
      if (method === 'localBegin') await initializeSession();
      if ((usesAdminRoute(method) || localAdminMethods.has(method)) && !session.admin) return failure('维护功能未获授权。');
      if (method === 'cancel') { await Promise.all([...active].map(cancelOperation)); return success({}); }
      if (method === 'sqlEditorDirty') { dirty = args.dirty === true; return success({}); }
      if (method === 'xmlEditorDirty') { xmlDirty = args.dirty === true; return success({}); }
      if (method === 'sqlEditorDiscard') return success({ discard: !dirty || window.confirm('当前 SQL 或列设置尚未保存。是否放弃草稿？') });
      if (method === 'pickTns') return failure('只能选择服务器已登记的 TNS 文件，请使用“已登记来源”。');
      if (!publicMethods.has(method) && !adminMethods.has(method)) return failure('不支持的 Web 操作。');
      if (!args || typeof args !== 'object' || Array.isArray(args)) return failure('参数格式无效。');
      const local = window.reportDeskLocal;
      if (local?.active && method === 'list' && Array.isArray(local.indexReports)) return success(local.indexReports);
      if (local?.active && method === 'clear' && !local.attached) return success({});
      if (local?.active && method === 'exportSql') {
        return success(await exportLocalSql(args, local));
      }
      if (local?.active && method === 'select') {
        const same = local.attached && args.reportId === local.localReportId;
        if (dirty || xmlDirty || !same && document.querySelector('#sql-editor[open], #xml-editor[open], #layout-editor[open]')) return failure('请先保存或放弃编辑草稿并关闭编辑器，再切换报表。');
        if (same) await local.fresh();
        else { await local.openReport(args.reportId, localRequest); resetEditorState(); }
      }
      if (local?.active && local.attached === false && !['settings', 'saveSettings', 'testConnection', 'activateConnection', 'discoverTns', 'tnsAliases', 'localBegin', 'localUpload', 'localActivate', 'localStatus', 'localUseServer', 'localLocations'].includes(method))
        return failure('请先选择本机报表；不会自动使用服务器报表。');
      if (method === 'select') { definitionHashes.delete(args.reportId); localChecks.delete(args.reportId); }
      if (method === 'clear') { definitionHashes.clear(); localChecks.clear(); }
      if (method === 'query' || method === 'lookup') {
        const definitionHash = definitionHashes.get(args.reportId);
        if (!definitionHash) return failure('缺少报表定义版本标识，请重新选择报表并核对条件。');
        if (window.reportDeskLocal?.active) {
          const checks = localChecks.get(args.reportId);
          if (!Array.isArray(checks) || !checks.length) return failure('缺少本机文件版本标识，请重新选择报表。');
          await window.reportDeskLocal.verifyChecks(checks);
        }
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
      if (window.reportDeskLocal?.active && ['sqlEditorOpen', 'xmlEditorOpen'].includes(method)) await window.reportDeskLocal.fresh();
      if (method === 'resolveSyncConflict') {
        if (!Number.isInteger(args.releaseId) || args.releaseId <= 0 || typeof args.acceptHis !== 'boolean') return failure('冲突标识或处理决定无效，请刷新同步状态。');
        if (typeof args.fingerprint !== 'string' || !args.fingerprint.trim()) return failure('冲突指纹缺失或无效，请刷新同步状态。');
        args = { releaseId: args.releaseId, acceptHis: args.acceptHis, fingerprint: args.fingerprint };
        if (!window.confirm(`发布 ${args.releaseId}：${args.acceptHis ? '接受 HIS 版本，允许同步覆盖此发布中的本地冲突文件。' : '暂保留本地版本，暂不应用此发布的冲突更新。'}\n请核对目标；操作不会回写 HIS。确认此处理决定？`)) return success(null);
      }
      if (method === 'sqlEditorReveal' && editor?.reportId === args.reportId) args = { ...args, token: editor.token };
      if (method === 'layoutPreview') { checkEditor(args); layout = null; }
      if (method === 'xmlEditorCheck' || method === 'xmlEditorSave') {
        if (!xmlEditor?.token || xmlEditor.reportId !== args.reportId || xmlEditor.token !== args.token)
          return failure('XML 编辑会话已失效，请保留草稿并重新打开。');
        if (method === 'xmlEditorSave') {
          const local = window.reportDeskLocal?.active;
          if (local) await window.reportDeskLocal.saveAccess();
          if (!window.confirm(`${local ? '将保存到本机 XML' : '将覆盖服务器 XML'}：\n${xmlEditor.path}\n${xmlEditor.matchNotice || ''}\n${local ? '保存前自动备份原文件，保存后读取校验。' : '不生成持久备份；保存前请确认目标并自行备份。'}确认保存？`)) return success(null);
        }
      }
      if (method === 'sqlEditorSave' || method === 'layoutSave') {
        const target = checkEditor(args);
        if (method === 'layoutSave' && (!layout || layout.previewToken !== args.previewToken || layout.sql !== args.sql || layout.sourceIndex !== args.sourceIndex))
          return failure('列预览已失效，请重新同步报表列。');
        const source = target.sources?.find(s => s.index === args.sourceIndex);
        const text = method === 'layoutSave'
          ? `将保存 SQL 与报表模板。\n查询：${layout.queryPath}\n模板：${layout.layoutPath}\n请核对预览列设置。`
          : `将覆盖原 XML 中当前数据源的 SQL。\n文件：${target.path}\n数据源：${source?.name || '(未命名)'}`;
        const local = window.reportDeskLocal?.active;
        if (local) await window.reportDeskLocal.saveAccess();
        if (!window.confirm(text + (local ? '\n目标为当前电脑，保存前自动备份原文件。' : '\n不生成备份；') + '保存不代表 SQL 或 HIS 版式已验证。确认保存？')) return success(null);
      }
      let remoteArgs = args;
      const stableId = local?.active && typeof args.reportId === 'string' ? args.reportId : null;
      if (stableId) {
        if (stableId !== local.localReportId || !local.attached) return failure('本机报表工作集已失效，请重新选择报表。');
        remoteArgs = { ...args, reportId: local.serverReportId };
      }
      let result = await remote(method, remoteArgs);
      if (result.ok && stableId && result.data && typeof result.data === 'object') {
        if (result.data.id === local.serverReportId) result.data.id = stableId;
        if (result.data.reportId === local.serverReportId) result.data.reportId = stableId;
        if (result.data.editor?.reportId === local.serverReportId) result.data.editor.reportId = stableId;
      }
      if (!result.ok) return result;
      if (window.reportDeskLocal?.active && ['sqlEditorOpen', 'xmlEditorOpen'].includes(method)) await window.reportDeskLocal.verifyChecks(result.data?.checks);
      if (result.data?.localSave) {
        if (!window.reportDeskLocal?.active) throw new Error('未授权本机目录，不能执行文件保存。');
        result = success(await window.reportDeskLocal.save(result.data, localRequest));
        if (stableId && result.data?.editor?.reportId === local.serverReportId) result.data.editor.reportId = stableId;
      }
      if (method === 'localActivate' || method === 'localUseServer') resetEditorState();
      if (method === 'select') {
        if (result.data?.id !== args.reportId || typeof result.data?.definitionHash !== 'string' || !result.data.definitionHash.trim())
          return failure('报表元数据缺少有效 definitionHash，请重新选择报表。');
        definitionHashes.set(args.reportId, result.data.definitionHash);
        if (window.reportDeskLocal?.active) localChecks.set(args.reportId, result.data.checks);
      }
      if (method === 'definition') {
        if (!Array.isArray(result.data?.textParameterNames)) return failure('条件元数据缺少 textParameterNames，无法安全判断显示名称参数。');
        return success({ title: result.data.title, textParameterNames: result.data.textParameterNames });
      }
      if (method === 'sqlEditorOpen') { editor = result.data; layout = null; }
      if (method === 'xmlEditorOpen') xmlEditor = result.data;
      if (method === 'xmlEditorSave' && result.data?.saved) { xmlEditor = result.data.editor; xmlDirty = false; }
      if (method === 'layoutPreview') layout = { ...result.data, sql: args.sql, sourceIndex: args.sourceIndex };
      if (['sqlEditorSave', 'layoutSave'].includes(method) && result.data?.saved) { editor = result.data.editor; dirty = false; layout = null; }
      if (method === 'exportSql' || (method === 'export' && result.data)) {
        if (typeof result.data?.downloadUrl !== 'string' || !result.data.downloadUrl) return failure('导出缺少下载地址。');
        download(result.data.downloadUrl);
      }
      return result;
    } catch (error) { return failure(errorMessage(error, '操作失败。')); }
  }
  async function activity() {
    if (!session || !localAttached() || active.size || touching || Date.now() - lastActivity < 30000) return;
    touching = true; lastActivity = Date.now();
    try {
      const result = await request('api/tab-lifecycle', 'POST', { tabId, args: { action: 'touch' } });
      if (result?.ok === true && result.data?.local === false) workspaceLost('本页临时工作区已释放，请保留草稿后重新加载目录。');
    } catch { /* Network loss is uncertain; request() reports definite expiry only. */ }
    finally { touching = false; }
  }
  const onActivity = () => { if (document.visibilityState !== 'hidden') void activity(); };
  window.addEventListener('pointerdown', onActivity);
  window.addEventListener('keydown', onActivity);
  // A physical browser write can take longer than an idle interval. Ordinary
  // idle pages receive no heartbeat and are eligible for cleanup.
  if (typeof setInterval === 'function') setInterval(() => { if (window.reportDeskLocal?.working) void activity(); }, 30000);
  window.addEventListener('pagehide', event => {
    if (event.persisted || !session || active.size || window.reportDeskLocal?.working || window.reportDeskLocal?.pending) return;
    void fetch(endpoint('api/tab-lifecycle'), { method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', keepalive: true,
      headers: { 'Content-Type': 'application/json', 'X-ReportDesk-CSRF': session.csrf }, body: JSON.stringify({ tabId, args: { action: 'release' } }) }).catch(() => {});
  });
  window.addEventListener('beforeunload', event => { if (dirty || xmlDirty || active.size || window.reportDeskLocal?.pending) { event.preventDefault(); event.returnValue = ''; } });
  window.reportDesk = Object.freeze({
    isWeb: true, adminPage, assetRoot: assetRoot.href, ready, call, activity,
    hasDraft: () => dirty || xmlDirty,
    resetEditors: resetEditorState,
    catalogStatus: async () => { await ready; return request('api/catalog-status'); },
    onProgress: callback => { listeners.progress.add(callback); return () => listeners.progress.delete(callback); },
    onFatal: callback => { listeners.fatal.add(callback); return () => listeners.fatal.delete(callback); }
  });
})();
