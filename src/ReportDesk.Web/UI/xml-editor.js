'use strict';
// Loaded only for a Web maintenance session. A report and file kind identify the
// server-side edit session; browser input is never used as a filesystem path.
(() => {
  function make(tag, id, label, className) {
    const node = document.createElement(tag);
    if (id) node.id = id;
    if (label !== undefined) node.textContent = label;
    if (className) node.className = className;
    if (tag === 'button') node.type = 'button';
    return node;
  }
  const openButton = make('button', 'xml-editor-open', 'XML 编辑');
  $('#sql').after(openButton);
  const dialog = make('dialog', 'xml-editor', undefined, 'sql-editor');
  dialog.setAttribute('aria-labelledby', 'xml-editor-title');
  const title = make('h2', 'xml-editor-title', 'XML 编辑');
  const close = make('button', 'xml-editor-close', undefined, 'sql-editor-close-icon');
  close.setAttribute('aria-label', '关闭 XML 编辑'); close.title = '关闭';
  const closeIcon = make('span', '', undefined, 'close-emblem'); closeIcon.setAttribute('aria-hidden', 'true'); close.append(closeIcon);
  const heading = make('div', '', undefined, 'sql-editor-header'); heading.append(title, close);
  const kind = make('select', 'xml-editor-kind'); kind.setAttribute('aria-label', '选择要编辑的 XML');
  for (const [value, label] of [['query', '查询设置 XML'], ['layout', '主报表设置 XML'], ['detail', '明细报表设置 XML']]) {
    const option = make('option', '', label); option.value = value; kind.append(option);
  }
  const check = make('button', 'xml-editor-check', '检查 XML');
  const copy = make('button', 'xml-editor-copy', '复制 XML');
  const save = make('button', 'xml-editor-save', '保存到服务器', 'primary');
  const actions = make('div', '', undefined, 'sql-editor-actions'); actions.append(check, copy, save);
  const toolbar = make('div', '', undefined, 'sql-editor-selector'); toolbar.append(kind, actions);
  const path = make('p', 'xml-editor-path', '', 'sql-editor-path');
  const match = make('p', 'xml-editor-match', '', 'xml-editor-match');
  const input = make('textarea', 'xml-editor-input'); input.setAttribute('aria-label', 'XML 文件内容'); input.spellcheck = false; input.wrap = 'off';
  const dirtyLabel = make('span', 'xml-editor-dirty', '', 'sql-editor-dirty'); dirtyLabel.setAttribute('role', 'status');
  const status = make('p', 'xml-editor-status', '', 'sql-editor-status'); status.setAttribute('role', 'status');
  const hint = make('p', 'xml-editor-save-hint', '', 'muted');
  dialog.append(heading, toolbar, path, match, input, dirtyLabel, status, hint); document.body.append(dialog);
  let session = null, baseline = '';
  const dirty = () => !!session && input.value !== baseline;
  const args = () => ({ reportId: session.reportId, token: session.token, text: input.value });
  const markDirty = () => { call('xmlEditorDirty', { dirty: dirty() }).catch(() => {}); };
  function update() {
    const locked = busy || dead || !session;
    const workspaceLost = window.reportDeskLocal?.state.workspaceLost === true;
    kind.disabled = locked || workspaceLost; input.readOnly = locked;
    check.disabled = locked || workspaceLost || !input.value.trim(); copy.disabled = locked || !input.value;
    save.disabled = locked || workspaceLost || !dirty() || !input.value.trim(); close.disabled = busy;
    dirtyLabel.textContent = dirty() ? '有未保存修改' : '与已读取文件一致';
    const local = window.reportDeskLocal?.active;
    save.textContent = workspaceLost ? '需重新扫描后保存' : local ? '保存到本机' : '保存到服务器';
    hint.textContent = workspaceLost ? '本页临时工作区已失效。草稿仍保留，可复制草稿后关闭编辑器，在“本机保存状态”中核对或恢复，再重新扫描目录。' :
      (local ? '保存到当前电脑的对应文件；先备份，再写入并读取校验。' : '直接保存服务器上的对应文件，不生成持久备份。') + '检查只验证 XML 格式和根元素，不验证 SQL、报表绑定或 HIS 显示。';
  }
  function load(data) {
    if (!data || typeof data.token !== 'string' || !data.token || typeof data.text !== 'string' || !['query', 'layout', 'detail'].includes(data.kind))
      throw new Error('XML 编辑会话无效，请重新打开。');
    session = data; kind.value = data.kind;
    title.textContent = (data.title || '报表') + ' · XML 编辑';
    path.textContent = (window.reportDeskLocal?.active ? '本机目标文件：' : '服务器目标文件：') + (data.path || ''); path.title = data.path || '';
    match.textContent = data.matchNotice || '';
    input.value = data.text; baseline = input.value; markDirty(); update();
  }
  async function run(label, action) {
    if (busy || dead) return;
    await task(label, async () => {
      try { await action(); }
      catch (error) { status.textContent = error.message; throw error; }
    });
    update();
  }
  function discardConfirmed() { return !dirty() || window.confirm('当前 XML 有未保存修改。放弃这些修改？'); }
  async function closeEditor() {
    if (busy || !discardConfirmed()) return;
    dialog.close(); session = null; baseline = ''; input.value = ''; markDirty(); update();
  }
  openButton.onclick = () => run('正在读取报表 XML…', async () => {
    if (!selected || !detail || !currentReportSession || detail.demo) throw new Error('请先选择可编辑的报表。');
    const data = await call('xmlEditorOpen', { reportId: selected, kind: 'query' });
    load(data); status.textContent = '文件已读取；保存不会执行 SQL。';
    dialog.showModal(); input.focus();
  });
  input.oninput = () => { status.textContent = '当前为未保存的编辑内容。'; update(); markDirty(); };
  kind.onchange = () => {
    const requested = kind.value, previous = session?.kind;
    if (!session || requested === previous) return;
    if (!discardConfirmed()) { kind.value = previous; return; }
    // Retain the current draft and selection if the other file cannot be opened.
    kind.value = previous;
    run('正在读取报表 XML…', async () => {
      const data = await call('xmlEditorOpen', { reportId: session.reportId, kind: requested });
      load(data); status.textContent = '文件已读取；保存不会执行 SQL。';
    });
  };
  check.onclick = () => run('正在检查 XML 格式…', async () => {
    const result = await call('xmlEditorCheck', args());
    status.textContent = result.message || (result.passed ? 'XML 格式检查通过。' : 'XML 格式检查未通过。');
  });
  copy.onclick = () => run('正在复制 XML…', async () => { await call('copy', { text: input.value }); status.textContent = 'XML 已复制。'; });
  save.onclick = () => run('正在保存报表 XML…', async () => {
    const reportId = session.reportId;
    const source = currentReportSession?.source ?? 0;
    const result = await call('xmlEditorSave', args());
    if (!result?.saved) { status.textContent = '已取消保存，草稿仍保留。'; return; }
    load(result.editor);
    status.textContent = result.message || '文件已保存。';
    if (result.reloaded) {
      try { reports = await call('list'); await select(reportId, source); }
      catch { status.textContent += '\n保存已完成，但页面重新加载失败；请重新选择报表。'; }
    }
  });
  close.onclick = closeEditor;
  dialog.addEventListener('cancel', event => { event.preventDefault(); closeEditor(); });
  window.addEventListener('beforeunload', event => { if (dirty()) { event.preventDefault(); event.returnValue = ''; } });
  window.addEventListener('reportdesk-local-change', update);
})();
