'use strict';
// Assemble the copied desktop resources only after the server grants capabilities.
(() => {
  const api = window.reportDesk;
  let serverReportsEnabled = false;
  const maintenanceIds = ['import-file', 'import-folder', 'check-new', 'settings-open', 'logs', 'sql'];
  function script(name) {
    return new Promise((resolve, reject) => {
      const node = document.createElement('script'); node.src = new URL(name, api.assetRoot).href;
      node.onload = resolve; node.onerror = () => reject(new Error('页面资源加载失败，请刷新：' + name)); document.body.append(node);
    });
  }
  function fail(message) {
    document.documentElement.classList.add('web-unavailable');
    document.querySelectorAll('button,input,select,textarea').forEach(node => { node.disabled = true; });
    const status = document.querySelector('#operation-status'); status.textContent = message;
    const alert = document.createElement('p'); alert.className = 'web-access-error'; alert.setAttribute('role', 'alert'); alert.textContent = message;
    document.body.prepend(alert);
  }
  async function start() {
    const capability = await api.ready;
    serverReportsEnabled = capability.serverReportsEnabled === true;
    if (api.adminPage && !capability.admin) { fail('维护入口未获授权。请从普通查询入口使用，或由服务器维护人员核对允许访问的电脑。'); return; }
    const initialCatalog = serverReportsEnabled ? await api.catalogStatus() : {version:null};
    document.documentElement.classList.add(capability.admin ? 'web-admin' : 'web-public');
    for (const id of maintenanceIds) document.getElementById(id).hidden = !capability.admin;
    const connection = document.querySelector('#connection');
    if (!capability.admin) {
      // Even a programmatic showModal() cannot open maintenance UI on the public page.
      connection.showModal = () => { throw new Error('维护功能未获授权。'); };
      document.addEventListener('click', event => {
        if (event.target.closest(maintenanceIds.map(id => '#' + id).join(','))) { event.preventDefault(); event.stopImmediatePropagation(); }
      }, true);
    }
    await script('query-form.js');
    if (capability.admin) await script('local-directory.js');
    await script('renderer.js');
    if (capability.admin) { await script('sql-editor.js'); await script('xml-editor.js'); }
    // These globals are provided by the unchanged desktop renderer.
    const retained = `查询结果仅在服务器内存保留；空闲 ${capability.idleMinutes} 分钟后释放（服务器可配置），执行中不清理。IIS 回收或会话失效后需重新查询。`;
    document.querySelector('footer span').textContent = retained;
    document.querySelector('footer span:last-child').textContent = capability.admin ? 'ReportDesk Web · 维护' : 'ReportDesk Web · 查询';
    document.querySelector('.nav strong').textContent = capability.admin ? '报表维护与试查' : '报表查询';
    document.title = 'ReportDesk Web · ' + (capability.admin ? '维护' : '报表查询');
    document.querySelector('#help').onclick = () => notice('Web 使用说明',
      '选择报表 → 修改条件 → 开始查询。下拉参数需加载并选择编码；不模拟登录身份。\n' +
      '分页只分段显示；筛选、排序和 Excel 下载作用于本次完整结果，不重新查询。下载完成请查看浏览器下载记录。复制受浏览器剪贴板权限约束。\n' +
      retained + '\n不同浏览器页面分别保有任务；本页刷新后重新选择报表并查询，不会自动重跑。\n' +
      '程序不设置查询时间或总行数上限；进度文字表示当前阶段，取消需等待服务端释放资源。\n' +
      (capability.admin ? '在“报表来源”中选择本机 HIS 报表目录，再点击“使用此目录”。SQL/XML 修改写回当前电脑；保存前须选择独立备份目录。索引在本机建立，选择报表时仅发送其查询及关联候选 XML 到服务器临时解析；请先用目录副本验证。关闭当前目录会释放临时副本，本机文件和备份保留。\n连接密码只发送给服务器，不保存在浏览器存储中。保存不等于发布回 HIS。' + (serverReportsEnabled ? '\n服务器兼容模式：“导入到服务器”将文件副本上传到服务器，修改保存在服务器；本机模式不使用发布同步。' : '') : '本机目录、连接配置与 SQL/XML 编辑由获准的维护电脑在 /admin 使用。'));
    if (capability.admin) setupAdmin();
    if (capability.admin) {
      const source = document.createElement('p'); source.id = 'web-report-source'; source.className = 'muted';
      source.textContent = serverReportsEnabled ? '服务器报表目录：' + (capability.reportsDirectory || '请查看同步状态') + '。' +
        (capability.syncReady ? `已配置每 ${capability.syncSeconds} 秒检查 HIS 发布；实际下载结果见“同步状态”。` : '自动下载尚未就绪，请打开“同步状态”查看原因。') : '尚未打开本机报表目录。各页面独立读取，SQL/XML 修改保存回当前电脑。';
      document.querySelector('#report-list').before(source);
      document.querySelector('#check-new').title = '扫描服务器报表目录中的新增报表；不会下载 HIS 发布，也不会更新已有定义';
      await setupLocalDirectory(capability);
      setupSettings();
    }
    if (serverReportsEnabled) startCatalogPolling(initialCatalog.version);
    else setupLocalStart(capability);
  }
  function setupLocalStart(capability) {
    document.documentElement.classList.add('local-reports-default');
    const table = document.getElementById('table-wrap'), paging = document.querySelector('.paging');
    const panel = element('div', undefined, 'local-start'); panel.id = 'local-start';
    const title = element('h2', '打开本机报表目录');
    const hint = element('p', capability.admin ? '选择当前电脑上的 HIS 报表 XML 目录，即可查询和编辑；修改保存回本机文件。' : '本机报表目录功能需要维护权限，请从获准电脑进入维护入口。');
    const open = element('button', '打开本机报表目录', 'primary'); open.id = 'local-start-open'; open.type = 'button'; open.hidden = !capability.admin;
    open.onclick = () => window.reportDeskSources.open('local');
    panel.append(icon('folder'), title, hint, open); table.before(panel);
    const warning = element('div', undefined, 'local-workspace-warning'); warning.id = 'local-workspace-warning'; warning.hidden = true; warning.setAttribute('role','status');
    const message = element('p'); const recover = element('button','核对本机目录'); recover.type = 'button'; recover.onclick = () => window.reportDeskSources.open('local');
    warning.append(message,recover); document.getElementById('issues').before(warning);
    function render() {
      const state = window.reportDeskLocal?.state;
      const empty = !state?.active;
      document.documentElement.classList.toggle('local-reports-empty', empty);
      panel.hidden = !empty; table.hidden = empty; paging.hidden = empty;
      title.textContent = state?.staging ? '本机目录尚未加载完成' : '打开本机报表目录';
      if (capability.admin) hint.textContent = state?.staging ? '原文件未改变。请打开报表来源，重新扫描或关闭当前目录。' : state?.pending ? '存在待核对的本机保存，请先在报表来源中核对或恢复。' : '选择当前电脑上的 HIS 报表 XML 目录，即可查询和编辑；修改保存回本机文件。';
      open.disabled = busy || dead || !!state?.working;
      warning.hidden = !state?.workspaceLost;
      message.textContent = state?.workspaceLost ? '本页临时工作区已失效。编辑草稿仍在页面中，请先复制保留草稿并关闭编辑器，再重新扫描本机目录；待恢复的保存应先核对。不会自动上传或重跑查询。' : '';
      recover.disabled = busy || dead || !!state?.working;
      if (state?.workspaceLost) for (const id of ['query','conditions-query']) document.getElementById(id).disabled = true;
    }
    new MutationObserver(render).observe(document.querySelector('.result'),{attributes:true,attributeFilter:['aria-busy']});
    window.addEventListener('reportdesk-local-change',render); render();
  }
  function startCatalogPolling(initialVersion) {
    let version = initialVersion, polling = false;
    const message = document.createElement('p'); message.id = 'web-catalog-update'; message.className = 'muted';
    message.setAttribute('role', 'status'); message.hidden = true;
    document.querySelector('#report-list').before(message);
    const searchHasFocus = () => document.activeElement === document.querySelector('#report-search');
    async function poll() {
      if (polling || window.reportDeskLocal?.active || window.reportDeskLocal?.state.staging || document.hidden || busy || dead || searchHasFocus() || document.querySelector('dialog[open]')) return;
      polling = true;
      try {
        const latest = await api.catalogStatus();
        if (!latest.version || latest.version === version || busy || dead || searchHasFocus() || document.querySelector('dialog[open]')) return;
        await task('正在更新报表目录…', async () => {
          reports = await call('list'); renderList(); version = latest.version;
          message.textContent = '报表目录已更新。当前条件和查询结果已保留；使用新版定义请重新选择报表。'; message.hidden = false;
          progress('报表目录已更新。');
        });
      } catch {
        message.textContent = '暂时无法检查报表更新，请检查网络或刷新页面。'; message.hidden = false;
      } finally { polling = false; }
    }
    // Only metadata is refreshed. Never rerun SQL or replace an editor/condition draft.
    const timer = setInterval(poll, 10000);
    document.addEventListener('visibilitychange', poll);
    window.addEventListener('pagehide', () => clearInterval(timer), { once: true });
  }
  async function setupLocalDirectory(capability) {
    const local = window.reportDeskLocal;
    await local.ready;
    const panel = document.createElement('section'); panel.id = 'local-directory-settings'; panel.setAttribute('aria-labelledby', 'local-directory-title');
    const title = document.createElement('h3'); title.id = 'local-directory-title'; title.textContent = '本机 HIS 报表目录';
    const hint = document.createElement('p'); hint.className = 'muted'; hint.textContent = '每个页面独立使用所选目录，修改 SQL 或 XML 后保存回当前电脑。索引在本机建立；匹配已有报表位置时发送查询文件相对路径和哈希，选择报表时才发送该查询及关联候选 XML 到服务器临时解析。数据库连接配置不变。请先用目录副本验证。';
    const source = document.createElement('p'); source.id = 'local-directory-source';
    const backup = document.createElement('p'); backup.id = 'local-directory-backup';
    const status = document.createElement('p'); status.id = 'local-directory-status'; status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const actions = document.createElement('div'); actions.className = 'local-directory-actions';
    const recovery = document.createElement('div'); recovery.id = 'local-directory-recovery'; recovery.hidden = true;
    const recoveryText = document.createElement('p'); recoveryText.id = 'local-directory-recovery-text'; recoveryText.setAttribute('role', 'alert');
    const recoveryActions = document.createElement('div'); recoveryActions.className = 'local-directory-actions';
    recovery.append(recoveryText, recoveryActions);
    let actionMessage = '';
    function button(id, label, action, target = actions) {
      const node = document.createElement('button'); node.id = id; node.type = 'button'; node.textContent = label;
      node.onclick = async () => {
        if (busy || dead || local.working) return;
        actionMessage = '';
        await task(label + '…', async () => {
          try { await action(); }
          catch (error) {
            if (error.name === 'AbortError') { actionMessage = '已取消选择，当前报表来源未改变。'; return; }
            actionMessage = error.message; throw error;
          }
        });
        render();
      };
      target.append(node); return node;
    }
    function canSwitch() {
      if (api.hasDraft() || document.querySelector('#sql-editor[open], #xml-editor[open], #layout-editor[open]'))
        throw new Error('请先保存或放弃编辑草稿并关闭编辑器，再更换或重新扫描目录。');
      if (local.pending) throw new Error('请先核对或恢复未完成的本机保存。');
    }
    async function reloadReports(result) {
      reports = Array.isArray(result?.reports) ? result.reports : await call('list');
      if (local.active) { await select(null); renderList(); } else await select(filteredReports()[0]?.id ?? null);
      document.querySelector('#web-catalog-update')?.setAttribute('hidden', '');
    }
    button('local-directory-pick', '选择目录', async () => { canSwitch(); await local.choose('source'); });
    button('local-directory-backup-pick', '选择备份目录', () => local.choose('backup'));
    button('local-directory-authorize', '重新授权', () => local.authorize());
    button('local-directory-inspect', '核对保存状态', () => local.inspectServer(call));
    const use = button('local-directory-use', '使用此目录', async () => { canSwitch(); await reloadReports(await local.load(call, progress)); }); use.className = 'primary';
    button('local-directory-rescan', '重新扫描', async () => { canSwitch(); await reloadReports(await local.load(call, progress, true)); });
    const server = button('local-directory-server', serverReportsEnabled ? '切回服务器目录' : '关闭当前目录', async () => {
      canSwitch();
      if ((local.active || local.state.staging || local.state.workspaceLost) && !window.confirm(serverReportsEnabled ? '切回服务器报表目录，并清除本页当前查询结果？本机 XML 不会改变。' : '关闭当前目录并释放本页临时副本？本页查询结果将清除，本机 XML、备份和已选目录保留。')) return;
      await reloadReports(await local.useServer(call));
    }, serverReportsEnabled ? document.getElementById('source-server-switch') : actions);
    button('local-directory-confirm', '核对并确认保存', async () => {
      const result = await local.recover(call, 'confirm');
      if (local.active && !local.state.needsScan) await reloadReports();
      actionMessage = result?.saved ? '本机保存已确认。原编辑草稿仍保留；关闭编辑器后重新打开，读取已保存文件。' : '本机文件已核对，请保留草稿并关闭编辑器，然后重新加载本机目录。';
      progress(actionMessage);
    }, recoveryActions);
    button('local-directory-restore', '恢复原文件', async () => {
      if (!window.confirm('恢复此批次保存前的原文件？只有当前内容仍匹配原版本或本次新版本时才恢复；其他程序的修改不会覆盖。')) return;
      await local.recover(call, 'restore');
      actionMessage = '原文件已恢复，编辑草稿仍保留。请复制保留草稿并关闭编辑器，然后重新扫描目录。';
    }, recoveryActions);
    panel.append(title, hint, source, backup, actions, status, recovery);
    document.getElementById('source-panel-local').append(panel);
    const serverSource = document.querySelector('#web-report-source').textContent;
    const sqlHint = document.querySelector('#sql-editor > .muted');
    const serverSqlHint = sqlHint?.textContent;
    function render() {
      const state = local.state;
      source.textContent = '已选本机目录：' + (state.source || '尚未选择') + (state.active ? '；本页正在使用：' + state.activeName : '；尚未启用本机模式');
      backup.textContent = '本机备份目录：' + (state.backup || '尚未选择（保存前必须选择）');
      status.textContent = !state.supported ? '当前环境不支持本机目录读写。请使用可信 HTTPS 和支持目录读写的 Chrome、Edge；360 需启用极速模式并确认功能可用。数据库连接功能仍可使用。' :
        actionMessage || state.message || (state.remembered ? '已记住目录。点击“重新授权”后再使用；不会自动读取本机文件。' : '选择目录后点击“使用此目录”。目录操作不会保存或测试数据库连接。');
      recovery.hidden = !state.pending;
      recoveryText.textContent = state.pending ? '本机保存待核对 · 目录：' + state.pending.name + '\n备份批次：' + state.pending.batch + '\n文件：' + state.pending.files.join('、') : '';
      document.querySelector('#web-report-source').textContent = state.staging ? '本机目录尚未加载完成，请打开“报表来源”继续处理。' : state.active ? '本机报表目录：' + state.activeName + '。SQL/XML 保存回当前电脑；服务器同步不作用于此目录。' + (state.locationWarning || '') : serverSource;
      document.getElementById('source-current-status').textContent = state.workspaceLost ? '当前来源：本机目录 · 临时工作区已失效' : state.staging ? '当前来源：本机目录加载待完成' : state.active ? '当前来源：本机目录 · ' + state.activeName : serverReportsEnabled ? '当前来源：服务器报表库' : '当前来源：尚未打开本机目录';
      document.getElementById('source-server-local-hint').hidden = !(state.active || state.staging);
      for (const id of ['import-file', 'import-folder', 'check-new', 'web-sync-open', 'web-publish-preview-open', 'web-sync']) {
        const node = document.getElementById(id); if (node) node.hidden = !serverReportsEnabled || state.active || state.staging;
      }
      const exportSql = document.getElementById('web-sql-export-open'); if (exportSql) exportSql.hidden = !serverReportsEnabled && !state.active;
      document.getElementById('local-directory-server').hidden = !(state.active || state.staging || state.workspaceLost);
      document.getElementById('local-directory-rescan').hidden = !(state.active || state.staging || state.workspaceLost);
      for (const node of panel.querySelectorAll('button')) node.disabled = busy || state.working || !state.supported;
      server.disabled = busy || dead || state.working;
      if (!state.source) { use.disabled = true; document.getElementById('local-directory-authorize').disabled = !state.pending; }
      document.documentElement.classList.toggle('local-reports-active', state.active);
      const sqlSave = document.getElementById('sql-editor-save'); if (sqlSave) sqlSave.textContent = state.workspaceLost ? '需重新扫描后保存' : state.active ? '保存到本机 XML' : '保存到原 XML';
      if (sqlHint) sqlHint.textContent = state.workspaceLost ? '本页临时工作区已失效。草稿仍保留，可复制草稿后关闭编辑器，在“本机保存状态”中核对或恢复，再重新扫描目录。' : state.active ? '保存到当前电脑的原 XML，先备份再回读校验。同步报表列可能同时修改查询和模板文件；保存不代表已与 HIS 引擎核对。' : serverSqlHint;
      const reveal = document.getElementById('sql-editor-reveal'); if (reveal) reveal.textContent = state.active || state.workspaceLost ? '查看本机来源说明' : '查看服务器来源说明';
      if (state.workspaceLost) lockLostSql();
      for (const node of document.querySelectorAll('.local-editor-settings')) { node.hidden = !state.active && !state.pending && !state.workspaceLost; node.disabled = busy || state.working; }
    }
    window.addEventListener('reportdesk-local-change', () => { actionMessage = ''; render(); });
    window.addEventListener('reportdesk-local-index', () => {
      if (!local.active) return;
      reports = local.indexReports; renderList();
    });
    if (local.state.remembered && !local.pending) {
      const resume = async () => {
        if (busy) { setTimeout(resume, 100); return; }
        if (api.hasDraft() || local.active) return;
        try { await reloadReports(await local.load(call, progress)); } catch { /* Explicit authorization remains available. */ }
      };
      void resume();
    }
    function lockLostSql() {
      if (!local.state.workspaceLost) return;
      for (const id of ['sql-editor-source','sql-editor-check','sql-editor-reload','sql-editor-reveal','sql-editor-sync','sql-editor-save','layout-editor-save']) {
        const control = document.getElementById(id); if (control && !control.disabled) control.disabled = true;
      }
    }
    // The copied SQL editor owns its controls. Apply the Web-only expired-workspace
    // boundary after its updates, while keeping local copy, draft and close usable.
    for (const id of ['sql-editor','layout-editor']) new MutationObserver(lockLostSql).observe(document.getElementById(id),{subtree:true,attributes:true,attributeFilter:['disabled']});
    // The shared renderer snapshots existing controls when starting a task.
    // Our controls may be created during bootstrap, after that snapshot. Observe
    // its public busy indicator so every task completion restores our buttons.
    new MutationObserver(render).observe(document.querySelector('.result'), { attributes: true, attributeFilter: ['aria-busy'] });
    for (const editorId of ['sql-editor', 'xml-editor', 'layout-editor']) {
      const editor = document.getElementById(editorId);
      const node = document.createElement('button'); node.type = 'button'; node.id = editorId + '-local-settings';
      node.className = 'local-editor-settings'; node.textContent = '本机保存状态';
      node.onclick = () => { if (!busy && !dead) window.reportDeskSources.open('local'); };
      editor.querySelector('.sql-editor-actions, .layout-editor-actions').append(node);
    }
    document.getElementById('locations-open').addEventListener('click', () => {
      if (local.active && detail) document.getElementById('location-source').textContent = '本机目录 ' + local.state.activeName + ' · ' + (detail.path || '当前报表');
    });
    render();
  }
  function setupAdmin() {
    document.querySelector('#import-file').title = '从本机选择 XML 导入';
    document.querySelector('#import-folder').title = '从本机选择文件夹导入';
    bindNativeImport('import-file', false);
    bindNativeImport('import-folder', true);
    setupReportSources();
    document.querySelector('#tns-pick').hidden = true;
    document.querySelector('#tns-discover').textContent = '已登记来源';
    document.querySelector('#tns-discover').onclick = () => task('正在读取已登记 TNS 来源…', async () => {
      const paths = await call('discoverTns');
      paths.forEach(addTns);
      if (!document.querySelector('#conn-tns').value && paths.length === 1) { document.querySelector('#conn-tns').value = paths[0]; await aliases(paths[0]); }
      document.querySelector('#test-status').textContent = paths.length ? `共 ${paths.length} 个已登记来源，请核对选择。` : '服务器尚未登记 TNS 文件，请联系服务器维护人员。';
    });
    document.querySelector('#conn-remember').parentNode.lastChild.textContent = '保存密码（服务器运行账号 DPAPI 加密）';
    document.querySelector('#connection > .muted').textContent = '连接测试成功后保存服务器配置；密码仅发送给服务器，不写入浏览器存储。取消保存密码时，仅在服务进程内使用，IIS 回收后需重新输入。测试不验证报表权限。';
    document.querySelector('#logs').onclick = () => task('正在读取维护日志…', async () => { const data = await call('openLogs'); notice(data.title || '维护日志', data.text || '暂无日志。'); });
    const reveal = document.querySelector('#sql-editor-reveal');
    reveal.textContent = '查看服务器来源说明';
    reveal.onclick = () => task('正在读取来源说明…', async () => {
      const data = await call('sqlEditorReveal', { reportId: selected });
      notice(data.title || '服务器来源说明', data.text || '暂无来源说明。');
    });
    // Retain the desktop check-new handler: its addedCount/sourceCount/errors
    // projection also preserves the current report and query-condition draft.
    const panel = document.createElement('section'); panel.id = 'web-sync'; panel.setAttribute('aria-labelledby', 'web-sync-title');
    const title = document.createElement('h3'); title.id = 'web-sync-title'; title.textContent = 'HIS 发布同步';
    const status = document.createElement('pre'); status.id = 'web-sync-status'; status.setAttribute('role', 'status'); status.textContent = '尚未读取同步状态。';
    const conflicts = document.createElement('div'); conflicts.id = 'web-sync-conflicts';
    const actions = document.createElement('div'); actions.className = 'actions';
    for (const [method, text] of [['syncStatus', '刷新状态'], ['syncNow', '立即检查']]) {
      const button = document.createElement('button'); button.type = 'button'; button.id = 'web-' + method; button.textContent = text;
      button.onclick = () => task(method === 'syncNow' ? '正在检查 HIS 已发布更新…' : '正在读取同步状态…', async () => {
        try {
          const data = await call(method);
          if (method === 'syncNow') { await refreshList(); showSync(await call('syncStatus'), status); }
          else showSync(data, status);
        } catch (error) { status.textContent = error.message; throw error; }
      });
      actions.append(button);
    }
    panel.append(title, status, conflicts, actions); document.querySelector('#connection').append(panel);
    // The shortcut opens the sync tab without loading database settings or TNS.
    const toggle = document.createElement('button'); toggle.type = 'button'; toggle.textContent = '同步状态'; toggle.id = 'web-sync-open';
    toggle.onclick = () => window.reportDeskSettings.open('sync');
    document.querySelector('.nav').append(toggle);
    setupSqlExport();
    setupPublishPreview();
  }
  function setupReportSources() {
    const file = document.getElementById('import-file'), folder = document.getElementById('import-folder');
    const open = element('button'); open.id = 'web-import-open'; open.type = 'button';
    open.title = '报表来源'; open.setAttribute('aria-label', '报表来源'); open.append(icon('folder'));
    open.setAttribute('aria-haspopup', 'dialog'); open.setAttribute('aria-controls', 'web-report-sources');
    file.before(open);
    const dialog = element('dialog'); dialog.id = 'web-report-sources'; dialog.setAttribute('aria-labelledby', 'source-title');
    const header = element('div', undefined, 'web-settings-header');
    const title = element('h2', '报表来源'); title.id = 'source-title';
    const close = element('button', '关闭'); close.type = 'button'; close.id = 'source-close'; header.append(title, close);
    const current = element('p', '', 'muted'); current.id = 'source-current-status'; current.setAttribute('role', 'status');
    const tabs = element('div', undefined, 'web-settings-tabs'); tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', '报表来源方式');
    dialog.append(header, current, tabs);
    const sections = new Map(); let activeTab = serverReportsEnabled ? 'server' : 'local';
    for (const [key, label] of [['local', '打开本机目录'], ['server', '导入到服务器']]) {
      const tab = element('button', label); tab.type = 'button'; tab.id = 'source-tab-' + key; tab.setAttribute('role', 'tab');
      const panel = element('section'); panel.id = 'source-panel-' + key; panel.setAttribute('role', 'tabpanel'); panel.setAttribute('aria-labelledby', tab.id);
      tab.setAttribute('aria-controls', panel.id); tabs.append(tab); dialog.append(panel); sections.set(key, {tab, panel});
      if (key === 'server' && !serverReportsEnabled) tab.hidden = true;
      tab.onclick = () => selectTab(key);
    }
    tabs.hidden = !serverReportsEnabled;
    const serverPanel = sections.get('server').panel;
    const hint = element('p', '将 XML 副本上传到服务器报表库，后续 SQL / XML 修改保存在服务器；本机原文件保持不变。', 'muted');
    const blocked = element('p', '本页正在使用本机目录。先切回服务器目录，再导入报表；切回时会清除本页查询结果。', 'muted'); blocked.id = 'source-server-local-hint'; blocked.hidden = true;
    const switchActions = element('div', undefined, 'local-directory-actions'); switchActions.id = 'source-server-switch';
    const importActions = element('div', undefined, 'source-import-actions');
    file.replaceChildren(icon('plus'), document.createTextNode('选择 XML 文件'));
    folder.replaceChildren(icon('folder'), document.createTextNode('选择文件夹'));
    file.setAttribute('aria-label', '选择 XML 文件'); folder.setAttribute('aria-label', '选择文件夹');
    importActions.append(file, folder);
    serverPanel.append(hint, blocked, switchActions, importActions, element('p', '选择文件夹可批量识别 XML，并匹配配套版式。导入后请在报表列表中选择报表。', 'muted'));
    document.body.append(dialog);
    async function selectTab(key) {
      if (busy || dead) return;
      activeTab = !serverReportsEnabled ? 'local' : sections.has(key) ? key : 'server';
      for (const [name, entry] of sections) { const selected = name === activeTab; entry.tab.setAttribute('aria-selected', String(selected)); entry.tab.tabIndex = selected ? 0 : -1; entry.panel.hidden = !selected; }
      if (activeTab === 'local') {
        try { await window.reportDeskLocal.inspectPending(); }
        catch (error) { document.getElementById('local-directory-status').textContent = error.message; }
      }
    }
    tabs.onkeydown = async event => {
      const keys = [...sections.keys()].filter(key => serverReportsEnabled || key === 'local'), index = keys.indexOf(activeTab);
      const next = event.key === 'ArrowRight' ? (index + 1) % keys.length : event.key === 'ArrowLeft' ? (index + keys.length - 1) % keys.length : event.key === 'Home' ? 0 : event.key === 'End' ? keys.length - 1 : -1;
      if (next < 0 || busy) return;
      event.preventDefault(); await selectTab(keys[next]);
      if (dialog.open && activeTab === keys[next]) sections.get(keys[next]).tab.focus();
    };
    window.reportDeskSources = { open: async key => {
      if (busy || dead) return;
      await selectTab(key || (!serverReportsEnabled || window.reportDeskLocal.active || window.reportDeskLocal.state.staging || window.reportDeskLocal.state.workspaceLost ? 'local' : 'server'));
      if (!dialog.open) dialog.showModal();
      sections.get(activeTab).tab.focus();
    } };
    open.onclick = () => window.reportDeskSources.open();
    close.onclick = () => { if (!busy && !window.reportDeskLocal.working) dialog.close(); };
    dialog.addEventListener('cancel', event => { event.preventDefault(); close.click(); });
  }
  function setupSettings() {
    const dialog = document.getElementById('connection'), form = document.getElementById('connection-form');
    const title = dialog.querySelector('h2'); title.textContent = serverReportsEnabled ? '连接与同步设置' : '数据库连接'; title.id = 'web-settings-title';
    dialog.setAttribute('aria-labelledby', title.id);
    const header = element('div', undefined, 'web-settings-header'); title.before(header); header.append(title);
    const close = element('button', '关闭'); close.type = 'button'; close.id = 'web-settings-close'; header.append(close);
    const tabs = element('div', undefined, 'web-settings-tabs'); tabs.setAttribute('role', 'tablist'); tabs.setAttribute('aria-label', '设置分类'); header.after(tabs);
    const intro = dialog.querySelector(':scope > .muted');
    const sections = new Map();
    for (const [key, label, content] of [['database', '数据库连接', form], ['sync', 'HIS 发布同步', document.getElementById('web-sync')]]) {
      const tab = element('button', label); tab.type = 'button'; tab.id = 'settings-tab-' + key; tab.setAttribute('role', 'tab');
      const panel = element('section'); panel.id = 'settings-panel-' + key; panel.setAttribute('role', 'tabpanel'); panel.setAttribute('aria-labelledby', tab.id);
      tab.setAttribute('aria-controls', panel.id); tabs.append(tab); dialog.append(panel); panel.append(content);
      if (key === 'sync' && !serverReportsEnabled) tab.hidden = true;
      if (key === 'database') panel.prepend(intro);
      if (key === 'sync') { const hint = element('p', '本页正在使用本机报表目录。切回服务器目录后，可查看和执行 HIS 发布同步。', 'muted'); hint.id = 'web-sync-local-hint'; panel.prepend(hint); }
      sections.set(key, {tab, panel}); tab.onclick = () => selectTab(key);
    }
    tabs.hidden = !serverReportsEnabled;
    let loaded = false, baseline = null, current = null, creating = false, activeTab = 'database';
    const toolbar = element('div', undefined, 'web-connection-picker');
    const label = element('label', '已保存的连接'); label.htmlFor = 'conn-profile';
    const profiles = element('select'); profiles.id = 'conn-profile'; label.append(profiles);
    const add = element('button', '新增连接'); add.type = 'button'; add.id = 'conn-new'; toolbar.append(label, add); form.prepend(toolbar);
    const context = element('p', '', 'web-connection-context'); context.id = 'conn-profile-status'; context.setAttribute('role', 'status'); toolbar.after(context);
    const activate = element('button', '设为当前连接'); activate.id = 'conn-activate'; activate.type = 'button';
    const save = form.querySelector('button[type=submit]'); save.textContent = '保存连接'; save.before(activate);
    document.getElementById('test-connection').textContent = '测试并保存';
    form.querySelector('[data-close="connection"]').hidden = true;
    document.getElementById('conn-name').required = true;
    intro.textContent = '可保存多组 Oracle 连接，选择后编辑，点击“设为当前连接”切换。当前连接用于后续查询' + (serverReportsEnabled ? '与 HIS 同步' : '') + '。密码仅发送给服务器，不写入浏览器存储。';
    const passwordHint = element('p', '取消保存密码后，仅在服务进程内使用；IIS 回收后需重新输入。连接测试只验证登录，不验证报表权限。', 'muted');
    document.getElementById('conn-remember').parentElement.after(passwordHint);
    const status = document.getElementById('test-status'); status.setAttribute('aria-live', 'polite');
    const draft = () => JSON.stringify(connectionArgs());
    const dirty = () => loaded && (creating || (baseline !== null && baseline !== draft()));
    const canDiscard = () => !dirty() || window.confirm('当前连接尚未保存。放弃这些修改？');
    function refreshControls() {
      for (const node of form.querySelectorAll('input,select,button')) node.disabled = busy || dead || !loaded;
      if (!busy && !dead && loaded) connectionMode();
      activate.disabled = busy || dead || !loaded || creating || !current?.profileId || current.profileId === current.activeProfileId || dirty();
      profiles.disabled = add.disabled = busy || dead || !loaded;
      document.getElementById('web-sync-local-hint').hidden = !(window.reportDeskLocal.active || window.reportDeskLocal.state.staging);
    }
    function setSummary(data) {
      const all = Array.isArray(data.profiles) ? data.profiles : [];
      const active = all.find(item => item.id === data.activeProfileId);
      context.textContent = creating ? '新增连接 · 保存后可设为当前连接；现有连接保持不变。' :
        data.profileId === data.activeProfileId ? '当前连接 · 保存修改将用于后续查询' + (serverReportsEnabled ? '和 HIS 同步' : '') + '。' :
        '正在编辑：' + data.name + ' · 当前使用：' + (active?.name || '原连接') + '。保存后不会自动切换。';
      profiles.replaceChildren(...all.map(item => new Option(item.name + (item.isActive ? '（当前）' : ''), item.id)));
      if (!all.length && !creating) profiles.add(new Option(data.name || '当前连接', data.profileId || ''));
      if (creating) profiles.add(new Option('新增连接（未保存）', ''));
      profiles.value = creating ? '' : (data.profileId || '');
    }
    async function fill(data, isNew = false) {
      loaded = true; current = data; settings = data; creating = isNew;
      for (const [id, key] of [['name','name'], ['host','host'], ['port','port'], ['service','service'], ['user','username']]) document.getElementById('conn-' + id).value = data[key] ?? '';
      document.getElementById('conn-mode').value = String(data.mode || 0);
      document.getElementById('conn-password').value = '';
      document.getElementById('conn-keep').checked = !!data.hasPassword;
      document.getElementById('conn-remember').checked = !!data.remember;
      document.getElementById('conn-tns').replaceChildren(new Option('请选择文件', ''));
      document.getElementById('conn-alias').replaceChildren(new Option('请选择网络服务', ''));
      connectionMode(); setSummary(data); status.textContent = ''; baseline = draft();
      // Registered TNS discovery is lazy; direct connections and other tabs do not depend on it.
      if (data.tnsFile) {
        addTns(data.tnsFile); document.getElementById('conn-tns').value = data.tnsFile;
        try { await aliases(data.tnsFile, data.tnsAlias); }
        catch (error) { status.textContent = error.message; }
      }
      baseline = draft(); refreshControls();
    }
    async function load(profileId) {
      await task('正在读取连接设置…', async () => {
        try { await fill(await call('settings', profileId ? {profileId} : {})); }
        catch (error) { profiles.value = creating ? '' : (current?.profileId || ''); status.textContent = error.message; throw error; }
      });
      refreshControls();
    }
    async function selectTab(key) {
      if (!serverReportsEnabled) key = 'database';
      activeTab = key;
      for (const [name, entry] of sections) { const selected = name === key; entry.tab.setAttribute('aria-selected', String(selected)); entry.tab.tabIndex = selected ? 0 : -1; entry.panel.hidden = !selected; }
      dialog.scrollTop = 0;
      if (key === 'database' && !loaded) await load();
      if (key === 'sync' && !window.reportDeskLocal.active && !window.reportDeskLocal.state.staging)
        await task('正在读取同步状态…', async () => { const target = document.getElementById('web-sync-status'); try { showSync(await call('syncStatus'), target); } catch (error) { target.textContent = error.message; throw error; } });
      refreshControls();
    }
    tabs.onkeydown = async event => {
      const keys = [...sections.keys()].filter(key => serverReportsEnabled || key === 'database'), index = keys.indexOf(activeTab);
      const next = event.key === 'ArrowRight' ? (index + 1) % keys.length : event.key === 'ArrowLeft' ? (index + keys.length - 1) % keys.length : event.key === 'Home' ? 0 : event.key === 'End' ? keys.length - 1 : -1;
      if (next < 0 || busy) return;
      event.preventDefault(); await selectTab(keys[next]);
      if (dialog.open && activeTab === keys[next]) sections.get(keys[next]).tab.focus();
    };
    window.reportDeskSettings = { open: async key => { if (busy || dead) return; if (!dialog.open) dialog.showModal(); await selectTab(key || 'database'); } };
    document.getElementById('settings-open').title = serverReportsEnabled ? '连接与同步设置' : '数据库连接';
    document.getElementById('settings-open').setAttribute('aria-label', serverReportsEnabled ? '连接与同步设置' : '数据库连接');
    document.getElementById('settings-open').onclick = () => window.reportDeskSettings.open('database');
    close.onclick = () => { if (!busy && canDiscard()) dialog.close(); };
    // Capture before the shared renderer clears the password on Escape.
    dialog.addEventListener('cancel', event => { event.preventDefault(); event.stopImmediatePropagation(); close.click(); }, true);
    dialog.addEventListener('close', () => { document.getElementById('conn-password').value = ''; loaded = false; baseline = null; creating = false; });
    form.addEventListener('input', refreshControls); form.addEventListener('change', refreshControls);
    profiles.onchange = () => { const id = profiles.value; if (!canDiscard()) { profiles.value = creating ? '' : (current?.profileId || ''); return; } load(id); };
    add.onclick = async () => { if (!canDiscard()) return; await fill({name:'', mode:0, host:'', port:1521, service:'', username:'', remember:true, hasPassword:false, activeProfileId:current?.activeProfileId, profiles:current?.profiles}, true); document.getElementById('conn-name').focus(); };
    async function saveProfile(test) {
      if (!form.reportValidity()) return;
      const args = {...connectionArgs(), ...(creating ? {newProfile:true} : current?.profileId ? {profileId:current.profileId} : {})};
      await task(test ? '正在测试连接…' : '正在保存连接…', async () => {
        status.textContent = test ? '正在测试连接，等待网络返回…' : '正在保存连接…';
        try {
          const result = await call(test ? 'testConnection' : 'saveSettings', args), data = test ? result.settings : result;
          await fill(data);
          const active = !data.profileId || data.profileId === data.activeProfileId;
          if (active) { clearResult(); document.getElementById('connection-state').textContent = (test ? '连接已验证：' : '当前连接：') + data.name; }
          status.textContent = (test ? '连接成功 · Oracle ' + result.version + '。' : '') + '连接已保存。' + (active ? '后续查询使用此连接。' : '当前使用的连接未改变，可点击“设为当前连接”。') + (test ? '未验证报表权限。' : '未执行连接测试。');
          progress(status.textContent);
        } catch (error) { status.textContent = error.message; throw error; }
      }); refreshControls();
    }
    form.onsubmit = event => { event.preventDefault(); saveProfile(false); };
    document.getElementById('test-connection').onclick = () => saveProfile(true);
    activate.onclick = async () => {
      if (activate.disabled || !window.confirm('将“' + current.name + '”设为服务器当前连接？后续查询' + (serverReportsEnabled ? '和 HIS 同步' : '') + '将使用此连接；正在执行的查询不变，本页已有结果将清除。此操作不会自动测试连接。')) return;
      await task('正在切换当前连接…', async () => {
        try { await fill(await call('activateConnection', {profileId:current.profileId})); clearResult(); document.getElementById('connection-state').textContent = '当前连接：' + current.name; status.textContent = '已切换当前连接。后续查询' + (serverReportsEnabled ? '和 HIS 同步' : '') + '将使用此连接；未执行连接测试。'; progress(status.textContent); }
        catch (error) { status.textContent = error.message; throw error; }
      }); refreshControls();
    };
    new MutationObserver(refreshControls).observe(document.querySelector('.result'), {attributes:true, attributeFilter:['aria-busy']});
    window.addEventListener('reportdesk-local-change', refreshControls);
    // Initial tab state only; do not read connection settings during page bootstrap.
    for (const [name, entry] of sections) { entry.tab.setAttribute('aria-selected', String(name === 'database')); entry.tab.tabIndex = name === 'database' ? 0 : -1; entry.panel.hidden = name !== 'database'; }
  }
  function setupPublishPreview() {
    const open = document.createElement('button');
    open.type = 'button'; open.id = 'web-publish-preview-open'; open.textContent = '发布准备';
    open.title = '检查当前报表的已保存文件；此功能暂不能发布到 HIS';
    document.querySelector('.work-head .actions').append(open);
    open.onclick = () => task('正在检查发布文件…', async () => {
      if (!selected) { notice('发布准备', '请先选择一张报表。'); return; }
      const data = await call('publishPreview', { reportId: selected });
      if (!data || !Array.isArray(data.files) || !Array.isArray(data.warnings)) throw new Error('发布准备结果格式无效，请重试。');
      const lines = [
        '仅检查服务器已保存的 XML；未保存的编辑草稿不包含在内。',
        '实际发布尚未接入，本操作不会发布或写入 HIS。',
        '主表版式如为候选匹配，需要现场核对；缺失或存在歧义时不能直接发布。',
        '', '待核对文件：'
      ];
      const roles = { Query: '查询设置', Main: '主表版式', Detail: '明细版式' };
      const statuses = { Source: '报表来源', Candidate: '候选匹配', Matched: '引用匹配' };
      for (const file of data.files) {
        if (!file || typeof file.path !== 'string' || typeof file.role !== 'string' || typeof file.status !== 'string' || typeof file.sha256 !== 'string')
          throw new Error('发布文件信息不完整，请重试。');
        const role = Object.prototype.hasOwnProperty.call(roles, file.role) ? roles[file.role] : `未知类型（${file.role}）`;
        const status = Object.prototype.hasOwnProperty.call(statuses, file.status) ? statuses[file.status] : `未知状态（${file.status}）`;
        lines.push(`${role}（${status}）：${file.path}`, `SHA-256：${file.sha256}`);
      }
      if (!data.files.length) lines.push('未找到可检查的文件。');
      if (data.warnings.length) lines.push('', '需注意：', ...data.warnings.map(item => String(item)));
      notice(data.title || '发布准备', lines.join('\n'));
    });
  }
  function setupSqlExport() {
    const open = document.createElement('button'); open.type = 'button'; open.id = 'web-sql-export-open'; open.textContent = '导出 SQL';
    const dialog = document.createElement('dialog'); dialog.id = 'web-sql-export'; dialog.setAttribute('aria-labelledby', 'web-sql-export-title');
    const heading = document.createElement('h2'); heading.id = 'web-sql-export-title'; heading.textContent = '导出报表 SQL';
    const hint = document.createElement('p'); hint.className = 'muted'; hint.textContent = '按名称搜索并勾选报表，可一次导出已选报表；也可单独导出或导出全部。更换搜索内容不会清除已选报表。';
    const search = document.createElement('input'); search.id = 'web-sql-export-search'; search.type = 'search'; search.placeholder = '搜索报表名称'; search.setAttribute('aria-label', '搜索报表名称');
    const status = document.createElement('p'); status.id = 'web-sql-export-status'; status.setAttribute('role', 'status');
    const selection = document.createElement('p'); selection.id = 'web-sql-export-selection'; selection.setAttribute('role', 'status');
    const selectionActions = document.createElement('div'); selectionActions.className = 'web-sql-export-selection-actions';
    const selectVisible = document.createElement('button'); selectVisible.type = 'button'; selectVisible.id = 'web-sql-export-select-visible'; selectVisible.textContent = '选中当前搜索结果';
    const clearSelection = document.createElement('button'); clearSelection.type = 'button'; clearSelection.id = 'web-sql-export-clear-selection'; clearSelection.textContent = '清除已选';
    selectionActions.append(selectVisible, clearSelection);
    const list = document.createElement('div'); list.id = 'web-sql-export-list';
    const actions = document.createElement('div'); actions.className = 'dialog-actions';
    const chosen = document.createElement('button'); chosen.type = 'button'; chosen.id = 'web-sql-export-selected'; chosen.textContent = '导出已选报表 SQL';
    const all = document.createElement('button'); all.type = 'button'; all.id = 'web-sql-export-all'; all.textContent = '导出全部报表 SQL';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '关闭'; close.addEventListener('click', () => dialog.close());
    actions.append(chosen, all, close); dialog.append(heading, hint, search, status, selection, selectionActions, list, actions); document.body.append(dialog);
    let catalog = [], composing = false;
    const selectedReports = new Set();
    function runExport(args) {
      task('正在准备 SQL 下载…', async () => {
        try {
          await call('exportSql', args);
          status.textContent = '已请求下载，请查看浏览器下载记录。'; progress(status.textContent);
        } catch (error) { status.textContent = error.message; throw error; }
      });
    }
    function updateSelection() {
      selection.textContent = `已选 ${selectedReports.size} 张报表。`;
      chosen.disabled = selectedReports.size === 0;
      clearSelection.disabled = selectedReports.size === 0;
    }
    function render() {
      const term = search.value.trim().toLocaleLowerCase();
      const matches = catalog.filter(report => String(report.name || '').toLocaleLowerCase().includes(term));
      list.replaceChildren();
      status.textContent = matches.length ? `找到 ${matches.length} 张报表。` : '没有匹配的报表。';
      selectVisible.disabled = matches.length === 0;
      for (const report of matches) {
        const row = document.createElement('div'); row.className = 'web-sql-export-row';
        const label = document.createElement('label'); label.className = 'web-sql-export-choice';
        const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = selectedReports.has(report.id);
        checkbox.setAttribute('aria-label', '选择 ' + report.name + '，报表 ID ' + report.id);
        checkbox.addEventListener('change', () => { if (checkbox.checked) selectedReports.add(report.id); else selectedReports.delete(report.id); updateSelection(); });
        const details = document.createElement('span');
        const name = document.createElement('strong'); name.textContent = report.name;
        const id = document.createElement('small'); id.textContent = '报表 ID：' + report.id;
        const button = document.createElement('button'); button.type = 'button'; button.textContent = '导出';
        button.setAttribute('aria-label', '导出 ' + report.name + '，报表 ID ' + report.id);
        button.addEventListener('click', () => runExport({ reportId: report.id }));
        details.append(name, id); label.append(checkbox, details); row.append(label, button); list.append(row);
      }
      updateSelection();
    }
    search.addEventListener('compositionstart', () => { composing = true; });
    search.addEventListener('compositionend', () => { composing = false; render(); });
    search.addEventListener('input', event => { if (!composing && !event.isComposing) render(); });
    selectVisible.addEventListener('click', () => {
      const term = search.value.trim().toLocaleLowerCase();
      for (const report of catalog) if (String(report.name || '').toLocaleLowerCase().includes(term)) selectedReports.add(report.id);
      render();
    });
    clearSelection.addEventListener('click', () => { selectedReports.clear(); render(); });
    chosen.addEventListener('click', () => { if (selectedReports.size) runExport({ reportIds: Array.from(selectedReports) }); });
    all.addEventListener('click', () => runExport({ reportId: '' }));
    dialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
    open.addEventListener('click', async () => {
      await task('正在读取报表列表…', async () => {
        catalog = (await call('list')).filter(report => !report.demo); selectedReports.clear(); search.value = ''; render(); dialog.showModal();
      });
      if (dialog.open) { updateSelection(); search.focus(); }
    });
    document.querySelector('.nav').append(open);
  }
  function showSync(data, status) {
    const conflictList = document.querySelector('#web-sync-conflicts'); conflictList?.replaceChildren();
    if (!data || typeof data !== 'object') { status.textContent = '服务端未返回同步状态。'; return; }
    const lines = [];
    if (data.reportsDirectory) lines.push('服务器报表目录：' + data.reportsDirectory);
    if (data.intervalSeconds) lines.push(`发布检查间隔：${data.intervalSeconds} 秒（运行耗时和繁忙等待另计）`);
    if (data.offline === true) lines.push('未下载原因：Offline=true，程序处于离线模式。');
    if (data.configuredEnabled === false) lines.push('未下载原因：SyncEnabled=false，自动下载未开启。');
    if (data.sampleValidated === false) lines.push('未下载原因：SyncSampleValidated=false，尚未确认现场发布格式。');
    if (data.enabled !== undefined) lines.push(data.enabled ? '自动同步已启用' : '自动同步未启用');
    if (data.running !== undefined) lines.push(data.running ? '正在同步，执行中不清理任务。' : '当前没有同步任务运行。');
    lines.push('最后成功同步：' + (data.lastSuccess || data.lastSuccessUtc || '尚无成功记录'));
    if (data.message) lines.push(data.message);
    if (data.error) lines.push('失败说明：' + (typeof data.error === 'string' ? data.error : data.error.message || '请检查维护日志。'));
    if (Array.isArray(data.conflicts) && data.conflicts.length) {
      lines.push('存在同步冲突，未自动覆盖。请逐项核对后决定。');
      for (const conflict of data.conflicts) {
        const row = document.createElement('div'); row.className = 'web-conflict';
        const label = document.createElement('p');
        const id = typeof conflict === 'object' && conflict ? Number(conflict.releaseId) : NaN;
        label.textContent = (Number.isInteger(id) && id > 0 ? '发布 ' + id + '：' : '') + (typeof conflict === 'string' ? conflict : conflict?.message || '本地内容与 HIS 发布有冲突');
        row.append(label);
        if (conflict?.canResolve === true && Number.isInteger(id) && id > 0 && typeof conflict.fingerprint === 'string' && conflict.fingerprint.trim()) {
          for (const [acceptHis, text] of [[true, '接受 HIS 版本'], [false, '暂保留本地']]) {
            const button = document.createElement('button'); button.type = 'button'; button.textContent = text;
            button.onclick = () => task('正在处理同步冲突…', async () => {
              // Preserve the exact fingerprint from the displayed status; the server revalidates it.
              const args = { releaseId: id, acceptHis, fingerprint: conflict.fingerprint };
              const result = await call('resolveSyncConflict', args);
              if (result === null) { progress('已取消冲突处理。'); return; }
              showSync(await call('syncStatus'), status); await refreshList();
              progress('冲突处理决定已提交；请查看服务端同步状态。');
            });
            row.append(button);
          }
        } else { const hint = document.createElement('p'); hint.textContent = '当前冲突不可直接处理，或缺少有效发布标识/指纹；请刷新状态或联系服务器维护人员。'; row.append(hint); }
        conflictList?.append(row);
      }
    } else if (typeof data.conflicts === 'number' && data.conflicts > 0) lines.push(`存在 ${data.conflicts} 项冲突，服务端尚未提供具体发布标识，请由维护人员检查。`);
    if (data.text) lines.push(data.text);
    status.textContent = lines.join('\n');
  }
  function readFileText(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ''));
      reader.onerror = () => reject(new Error('无法读取所选文件。'));
      reader.readAsText(file);
    });
  }
  function bindNativeImport(id, folder) {
    document.getElementById(id).onclick = () => {
      const input = document.createElement('input');
      input.type = 'file';
      if (folder) { input.setAttribute('webkitdirectory', 'true'); input.multiple = true; }
      else input.accept = '.xml,text/xml';
      input.onchange = () => {
        const chosen = [...(input.files || [])];
        input.remove();
        if (!chosen.length) return;
        task('正在导入所选报表…', async () => {
          const files = [];
          let total = 0;
          for (const file of chosen) {
            const raw = (folder ? (file.webkitRelativePath || file.name) : file.name).replaceAll('\\', '/');
            const rel = folder && !raw.includes('/') ? '本机导入/' + file.name : raw;
            const base = rel.split('/').pop();
            if (!base || !/\.xml$/i.test(base)) continue;
            if (file.size > 5 * 1024 * 1024) throw new Error('单个 XML 超过 5 MB，请改选较小文件。');
            total += file.size;
            if (files.length >= 200 || total > 5 * 1024 * 1024)
              throw new Error('所选文件过多或过大。整份 LIB 请放在页面显示的服务器报表目录，再点“检查新增”，无需浏览器上传。');
            files.push({ path: rel, text: await readFileText(file) });
          }
          if (!files.length) throw new Error('请选择 XML 文件。');
          const uploaded = await call('uploadImport', { folder, files });
          const imported = await call('import', { folder, path: uploaded.path });
          if (!imported) { progress('已取消导入。'); return; }
          reports = imported.reports; await select(filteredReports()[0]?.id ?? null);
          const text = `导入 ${imported.imported} 张；待适配 ${imported.pending} 张；发现版式 XML ${imported.layouts} 份；其他 XML ${imported.otherXml || 0} 份；仅条件/不完整定义 ${imported.incomplete?.length || 0} 份；警告/失败 ${imported.errors.length} 项。`;
          progress(text);
          notice('导入与匹配结果', text + `\n跳过独立导入 ${imported.skipped} 份。单文件导入时，版式/其他数量是同目录配套扫描数。\n显式路径匹配 ${imported.matched} 项；名称候选 ${imported.candidates} 项；缺失/冲突/拒绝 ${imported.unresolved} 项。\n版式、其他配置和无查询 SQL 的定义不作为独立报表显示；原文件保留。待适配的查询报表仍显示。匹配到配套文件不代表已支持交叉或映射规则。` + (imported.incomplete?.length ? '\n\n仅条件/不完整定义（需要完整查询定义或数据获取实现）：\n' + imported.incomplete.join('\n') : '') + (imported.errors.length ? '\n\n' + imported.errors.join('\n') : ''));
        });
      };
      document.body.append(input);
      input.click();
    };
  }
  start().catch(error => fail(error.message || '页面初始化失败，请刷新。'));
})();
