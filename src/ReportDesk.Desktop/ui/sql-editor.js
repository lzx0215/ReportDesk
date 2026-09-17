'use strict';
// Loaded after renderer.js: reuse its task, clipboard IPC and single-report selection flow.
// Drafts live only in this dialog. The source XML remains the sole saved definition.
(() => {
  function make(tag, id, text, className) {
    const node = document.createElement(tag);
    if (id) node.id = id;
    if (text !== undefined) node.textContent = text;
    if (className) node.className = className;
    if (tag === 'button') node.type = 'button';
    return node;
  }
  const dialog = make('dialog', 'sql-editor', undefined, 'sql-editor');
  dialog.setAttribute('aria-labelledby', 'sql-editor-title');
  const heading = make('h2', 'sql-editor-title', 'SQL 编辑');
  const filePath = make('p', 'sql-editor-path', '', 'sql-editor-path');
  const sourceSelect = make('select', 'sql-editor-source');
  sourceSelect.setAttribute('aria-label', 'SQL 数据源');
  const dirtyLabel = make('span', 'sql-editor-dirty', '', 'sql-editor-dirty');
  dirtyLabel.setAttribute('role', 'status');
  const selector = make('div', '', undefined, 'sql-editor-selector'); selector.append(sourceSelect);
  const input = make('textarea', 'sql-editor-input');
  input.setAttribute('aria-label', '当前数据源的原文件 SQL'); input.spellcheck = false; input.wrap = 'off';
  const status = make('p', 'sql-editor-status', '', 'sql-editor-status'); status.setAttribute('role', 'status');
  const hint = make('p', '', '保存直接覆盖原文件中当前数据源的 SQL，不生成备份。其他数据源不变，不执行查询。外部已打开的 XML 请重新加载后查看。', 'muted');
  const copy = make('button', 'sql-editor-copy', '复制 SQL');
  const check = make('button', 'sql-editor-check', '静态检查');
  const reload = make('button', 'sql-editor-reload', '重新加载当前报表');
  const reveal = make('button', 'sql-editor-reveal', '打开原报表位置');
  const save = make('button', 'sql-editor-save', '保存到原 XML', 'primary');
  const close = make('button', 'sql-editor-close', '关闭');
  const actions = make('div', '', undefined, 'sql-editor-actions'); actions.append(copy, check, reload, reveal, close, save);
  selector.append(actions);
  dialog.append(heading, filePath, selector, input, dirtyLabel, status, hint); document.body.append(dialog);
  let session = null, sourceIndex = -1, baseline = '';
  const source = () => session?.sources.find(s => s.index === sourceIndex);
  const dirty = () => !!source()?.editable && input.value !== baseline;
  const args = () => ({ reportId: session.reportId, token: session.token, sourceIndex, sql: input.value });
  async function markDirty(value) { await call('sqlEditorDirty', { dirty: value }); }
  function updateControls() {
    const locked = busy || dead;
    sourceSelect.disabled = locked || !session?.sources.length;
    input.disabled = busy; input.readOnly = locked || !source()?.editable;
    copy.disabled = locked || !input.value;
    check.disabled = locked || !session?.token || !input.value || input.value.length > 1024 * 1024;
    reload.disabled = locked || !session || session.demo;
    reveal.disabled = locked || !session?.token || session.demo;
    save.disabled = locked || !source()?.editable || !dirty() || !input.value.trim() || input.value.length > 1024 * 1024;
    close.disabled = busy;
    dirtyLabel.textContent = input.value.length > 1024 * 1024 ? '超过编辑大小限制，请先复制保留 SQL' : dirty() ? '有未保存修改' : source()?.editable ? '与已读取文件一致' : '只读';
  }
  function showSource(index) {
    sourceIndex = index;
    sourceSelect.value = String(index);
    input.value = source()?.sql ?? '';
    baseline = input.value; // textarea normalizes CRLF; do not mark an untouched document dirty.
    updateControls();
  }
  function load(data, preferredIndex) {
    session = data; heading.textContent = data.title + ' · SQL 编辑';
    filePath.textContent = '原文件：' + data.path; filePath.title = data.path;
    sourceSelect.replaceChildren();
    const kinds = { MainReportUsing: '主表', DetailReportUsing: '明细', ConditionUsing: '条件' };
    for (const s of data.sources) {
      const option = make('option', '', `${s.index + 1}. ${s.name || '(未命名)'} · ${kinds[s.kind] || s.kind}${s.editable ? '' : '（只读）'}`);
      option.value = String(s.index); sourceSelect.append(option);
    }
    const selectedSource = data.sources.find(s => s.index === preferredIndex) || data.sources.find(s => s.editable) || data.sources[0];
    showSource(selectedSource?.index ?? -1);
  }
  async function run(label, action) {
    if (busy || dead) return;
    await task(label, async () => {
      try { await action(); }
      catch (error) { status.textContent = error.message; throw error; }
    });
    updateControls();
  }
  async function discardConfirmed() { return !dirty() || (await call('sqlEditorDiscard')).discard; }
  async function refreshSelected(queryIndex) {
    reports = await call('list');
    await select(session.reportId, Math.max(0, queryIndex ?? 0));
  }
  $('#sql').textContent = 'SQL 编辑';
  $('#sql').onclick = () => run('正在读取原文件 SQL…', async () => {
    const queryIndex = currentReportSession?.source ?? 0;
    const data = await call('sqlEditorOpen', { reportId: selected });
    const preferred = data.sources.find(s => s.queryIndex === queryIndex)?.index;
    load(data, preferred); await markDirty(false);
    status.textContent = data.stale ? '原 XML 已被外部修改；已停止使用旧定义。可编辑后保存，或点击重新加载当前报表。' : '直接读取原 XML；保存不会执行 SQL。';
    if (data.stale) { clearResult(); await refreshSelected(queryIndex); }
    if (!dialog.open) dialog.showModal();
    input.focus();
  });
  input.oninput = () => {
    status.textContent = '当前为未保存的编辑内容。'; updateControls();
    markDirty(dirty()).catch(() => { status.textContent = '编辑状态同步失败，请先复制保留草稿。'; });
  };
  input.onkeydown = event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault(); if (!save.disabled) save.onclick();
    }
  };
  sourceSelect.onchange = () => {
    const next = Number(sourceSelect.value); sourceSelect.value = String(sourceIndex);
    return run('正在切换 SQL 数据源…', async () => {
      if (!await discardConfirmed()) return;
      await markDirty(false); showSource(next); status.textContent = '已读取该数据源的原文件 SQL。';
    });
  };
  copy.onclick = () => run('正在复制 SQL…', async () => {
    await call('copy', { text: input.value }); status.textContent = '已复制当前编辑区的完整 SQL，未代入参数值。'; progress('SQL 已复制。');
  });
  check.onclick = () => run('正在静态检查 SQL…', async () => {
    const result = await call('sqlEditorCheck', args());
    status.textContent = result.message + (result.missing?.length ? '\n新增参数需核对条件定义：' + result.missing.join('、') : '');
    progress(result.passed ? '静态检查通过，未执行 SQL。' : '静态检查未通过，未执行 SQL。');
  });
  reveal.onclick = () => run('正在打开原报表位置…', async () => {
    await call('sqlEditorReveal', { reportId: session.reportId, token: session.token });
    status.textContent = '已请求在资源管理器中选中原 XML：' + session.path;
    progress('已打开原报表位置。');
  });
  save.onclick = () => run('请确认保存目标…', async () => {
    const index = sourceIndex;
    const result = await call('sqlEditorSave', args());
    if (!result) { status.textContent = '已取消保存，草稿仍保留。'; return; }
    // Mark clean before refreshing the UI. A refresh error must not be presented as a failed disk write.
    load(result.editor, index); clearResult();
    const message = result.message + '\n已核对文件：' + (result.savedPath || result.editor.path) + '\n数据源：' + (source()?.name || '(未命名)') + '。外部已打开的 XML 请重新加载后查看。';
    try { await refreshSelected(source()?.queryIndex); status.textContent = message; }
    catch { status.textContent = message + '\n界面更新失败。文件已保存，请重新加载当前报表，不要重复覆盖文件。'; }
    progress(result.changed === false ? 'SQL 与磁盘文件一致，无需写入。' : result.reloaded ? 'XML 已保存并回读核对；请核对条件后手动查询。' : 'XML 已保存，重新加载未完成。');
  });
  reload.onclick = () => run('正在重新加载当前报表…', async () => {
    if (!await discardConfirmed()) return;
    const index = sourceIndex, wasDirty = dirty();
    clearResult();
    if (detail) detail.selectedIssues = ['原 XML 与内存定义未同步，请重新加载当前报表后再查询。'];
    await call('reloadReport', { reportId: session.reportId });
    await markDirty(false);
    try {
      const data = await call('sqlEditorOpen', { reportId: session.reportId });
      load(data, index); await refreshSelected(source()?.queryIndex);
      status.textContent = '当前报表已重新加载；旧结果已清除，查询条件已重新初始化。未执行 SQL。';
    } catch (error) { await markDirty(wasDirty && dirty()); throw error; }
  });
  close.onclick = () => run('正在关闭 SQL 编辑…', async () => {
    if (!await discardConfirmed()) return;
    await markDirty(false); dialog.close(); input.value = ''; baseline = ''; session = null;
  });
  dialog.addEventListener('cancel', event => { event.preventDefault(); if (!busy) close.onclick(); });
  updateControls();
})();
