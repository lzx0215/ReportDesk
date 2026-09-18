'use strict';
// Local dialogs and file reveal; XML reads/writes remain in the Host.
class SqlEditorMain {
  constructor(dialog, shell, files = require('node:fs')) { this.dialog = dialog; this.shell = shell; this.files = files; this.dirty = false; this.target = null; }
  setDirty(args) {
    if (!args || typeof args.dirty !== 'boolean') return { ok: false, message: '编辑状态无效。' };
    this.dirty = args.dirty;
    return { ok: true, data: {} };
  }
  allowClose(window) {
    if (!this.dirty) return true;
    const response = this.dialog.showMessageBoxSync(window, {
      type: 'warning', title: '编辑内容尚未保存', message: '当前 SQL 或报表列设置有未保存修改。',
      detail: '关闭程序将丢弃草稿，不会写入原 XML。',
      buttons: ['继续编辑', '放弃并关闭'], defaultId: 0, cancelId: 0, noLink: true
    });
    if (response !== 1) return false;
    this.dirty = false;
    return true;
  }
  async handle(method, args, window, bridge) {
    if (method === 'sqlEditorDiscard') {
      if (!this.dirty) return { discard: true };
      const result = await this.dialog.showMessageBox(window, {
        type: 'warning', title: '编辑内容尚未保存', message: '当前 SQL 或报表列设置有未保存修改，是否放弃？',
        detail: '可返回编辑区先复制草稿。放弃修改不会改写原 XML。',
        buttons: ['继续编辑', '放弃修改'], defaultId: 0, cancelId: 0, noLink: true
      });
      return { discard: result.response === 1 };
    }
    if (method === 'sqlEditorOpen') {
      if (this.dirty) throw new Error('请先保存或放弃当前 SQL 草稿。');
      const data = await bridge.call(method, { reportId: args.reportId });
      this.layout = null;
      this.target = data;
      return data;
    }
    if (method === 'sqlEditorReveal') {
      const target = this.target;
      if (!target || target.demo || !target.token || args.reportId !== target.reportId || args.token !== target.token)
        throw new Error('原文件位置不可用，请重新打开 SQL 编辑窗口。');
      // The renderer cannot choose the path: use the source returned by the Host when opening this editor.
      try { if (!this.files.statSync(target.path).isFile()) throw new Error(); }
      catch { throw new Error('原 XML 已移动、删除或不可访问，请核对来源路径。'); }
      this.shell.showItemInFolder(target.path);
      return { path: target.path };
    }
    if (!['sqlEditorSave', 'layoutPreview', 'layoutSave'].includes(method)) throw new Error('不支持的 SQL 编辑操作。');
    const target = this.target;
    const source = target?.sources.find(s => s.index === args.sourceIndex);
    if (!target || !target.token || args.token !== target.token || args.reportId !== target.reportId || !source?.editable)
      throw new Error('编辑会话已失效或当前数据源不可编辑，请保留草稿并重新打开。');
    if (typeof args.sql !== 'string' || !args.sql.trim() || args.sql.length > 1024 * 1024)
      throw new Error('SQL 不能为空，且不得超过编辑大小限制。');
    if (method === 'layoutPreview') {
      this.layout = null;
      const data = await bridge.call(method, { reportId: target.reportId, token: target.token, sourceIndex: source.index, sql: args.sql, values: args.values });
      this.layout = { ...data, sql: args.sql, sourceIndex: source.index, token: target.token };
      return data;
    }
    if (method === 'layoutSave') {
      const plan = this.layout;
      if (!plan || plan.token !== target.token || args.previewToken !== plan.previewToken || args.sql !== plan.sql || source.index !== plan.sourceIndex)
        throw new Error('列预览已失效，请重新同步报表列。');
      if (!Array.isArray(args.columns) || args.columns.length !== plan.columns.length || args.columns.some((c, i) =>
        !c || c.index !== i || typeof c.header !== 'string' || c.header.length > 128 || (!plan.columns[i].hidden && !c.header.trim()) || !Number.isInteger(c.width) || c.width < 24 || c.width > 1000))
        throw new Error('列设置无效：表头最多 128 字，列宽须为 24–1000。');
      const confirmation = await this.dialog.showMessageBox(window, {
        type: 'warning', title: '同步保存 SQL 与报表模板', message: '将保存下列 SQL 与模板，内容未变的文件不重写，不生成备份。',
        detail: `查询：${plan.queryPath}\n模板：${plan.layoutPath}\n数据源：${source.name}\n新增列：${plan.columns.filter(c => c.added).length}\n${plan.reconciled ? '\nSQL 已保存、模板待补齐；已按原表头逐列匹配，请确认对应关系。\n' : ''}\n请确认模板路径与预览列设置。保存不查询数据；两文件替换不是断电安全的整体事务，请勿同时用其他程序编辑。保存后仍需在 HIS 核对显示和打印。`,
        buttons: ['取消', '覆盖并保存两份 XML'], defaultId: 0, cancelId: 0, noLink: true
      });
      if (confirmation.response !== 1) return null;
      try {
        const saved = await bridge.call(method, { reportId: target.reportId, token: target.token, sourceIndex: source.index,
          sql: plan.sql, previewToken: plan.previewToken, columns: args.columns.map(c => ({ index: c.index, header: c.header, width: c.width })) });
        if (saved?.saved) { this.target = saved.editor; this.dirty = false; }
        return saved;
      } finally { this.layout = null; }
    }
    const confirmation = await this.dialog.showMessageBox(window, {
      type: 'warning', title: '保存到原 XML', message: '将直接覆盖原文件中当前数据源的 SQL，不生成备份。',
      detail: `文件：${target.path}\n数据源：${source.name || '(未命名)'} · ${source.kind}\n\n保存不执行 SQL，也不代表 SQL 或 HIS 版式已验证。若该文件由 HIS 使用，HIS 下次读取时会使用新配置。请勿同时在其他程序中编辑此文件。`,
      buttons: ['取消', '覆盖并保存'], defaultId: 0, cancelId: 0, noLink: true
    });
    if (confirmation.response !== 1) return null;
    const saved = await bridge.call('sqlEditorSave', {
      reportId: target.reportId, token: target.token, sourceIndex: source.index, sql: args.sql
    });
    if (saved?.saved) { this.target = saved.editor; this.dirty = false; this.layout = null; }
    return saved;
  }
}
module.exports = { SqlEditorMain };
