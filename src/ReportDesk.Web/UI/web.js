'use strict';
// Assemble the copied desktop resources only after the server grants capabilities.
(() => {
  const api = window.reportDesk;
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
    if (api.adminPage && !capability.admin) { fail('维护入口未获授权。请从普通查询入口使用，或由服务器维护人员核对允许访问的电脑。'); return; }
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
    await script('renderer.js');
    if (capability.admin) await script('sql-editor.js');
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
      (capability.admin ? '导入会打开系统文件窗口，从这台电脑选择 XML 或文件夹，上传到服务器后再导入。连接密码只发送给服务器，不保存在浏览器存储中。\nSQL 或列设置保存前会再次确认目标，保存不等于发布回 HIS。' : '导入、连接配置、SQL 编辑与同步由获准的维护电脑在 /admin 使用。'));
    if (capability.admin) setupAdmin();
  }
  function setupAdmin() {
    document.querySelector('#import-file').title = '从本机选择 XML 导入';
    document.querySelector('#import-folder').title = '从本机选择文件夹导入';
    bindNativeImport('import-file', false);
    bindNativeImport('import-folder', true);
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
    // Access sync without opening connection settings or requiring TNS discovery.
    const toggle = document.createElement('button'); toggle.type = 'button'; toggle.textContent = '同步状态'; toggle.id = 'web-sync-open';
    const syncDialog = document.createElement('dialog'); syncDialog.id = 'web-sync-dialog';
    const close = document.createElement('button'); close.type = 'button'; close.textContent = '关闭'; close.onclick = () => syncDialog.close();
    syncDialog.append(close); document.body.append(syncDialog);
    toggle.onclick = () => task('正在读取同步状态…', async () => { syncDialog.prepend(panel); if (!syncDialog.open) syncDialog.showModal(); showSync(await call('syncStatus'), status); });
    syncDialog.addEventListener('cancel', event => { if (busy) event.preventDefault(); });
    syncDialog.addEventListener('close', () => document.querySelector('#connection').append(panel));
    document.querySelector('.nav').append(toggle);
  }
  function showSync(data, status) {
    const conflictList = document.querySelector('#web-sync-conflicts'); conflictList?.replaceChildren();
    if (!data || typeof data !== 'object') { status.textContent = '服务端未返回同步状态。'; return; }
    const lines = [];
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
              throw new Error('所选文件过多或过大。请选择单个报表或较小文件夹；整份 HIS 目录请先复制到服务器 Reports 后再导入。');
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
