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
      type: 'warning', title: 'SQL 尚未保存', message: '当前 SQL 有未保存修改。',
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
        type: 'warning', title: 'SQL 尚未保存', message: '当前 SQL 有未保存修改，是否放弃？',
        detail: '可返回编辑区先复制草稿。放弃修改不会改写原 XML。',
        buttons: ['继续编辑', '放弃修改'], defaultId: 0, cancelId: 0, noLink: true
      });
      return { discard: result.response === 1 };
    }
    if (method === 'sqlEditorOpen') {
      if (this.dirty) throw new Error('请先保存或放弃当前 SQL 草稿。');
      const data = await bridge.call(method, { reportId: args.reportId });
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
    if (method !== 'sqlEditorSave') throw new Error('不支持的 SQL 编辑操作。');
    const target = this.target;
    const source = target?.sources.find(s => s.index === args.sourceIndex);
    if (!target || !target.token || args.token !== target.token || args.reportId !== target.reportId || !source?.editable)
      throw new Error('编辑会话已失效或当前数据源不可编辑，请保留草稿并重新打开。');
    if (typeof args.sql !== 'string' || !args.sql.trim() || args.sql.length > 1024 * 1024)
      throw new Error('SQL 不能为空，且不得超过编辑大小限制。');
    const confirmation = await this.dialog.showMessageBox(window, {
      type: 'warning', title: '保存到原 XML', message: '将直接覆盖原文件中当前数据源的 SQL，不生成备份。',
      detail: `文件：${target.path}\n数据源：${source.name || '(未命名)'} · ${source.kind}\n\n保存不执行 SQL，也不代表 SQL 或 HIS 版式已验证。若该文件由 HIS 使用，HIS 下次读取时会使用新配置。请勿同时在其他程序中编辑此文件。`,
      buttons: ['取消', '覆盖并保存'], defaultId: 0, cancelId: 0, noLink: true
    });
    if (confirmation.response !== 1) return null;
    const saved = await bridge.call('sqlEditorSave', {
      reportId: target.reportId, token: target.token, sourceIndex: source.index, sql: args.sql
    });
    if (saved?.saved) { this.target = saved.editor; this.dirty = false; }
    return saved;
  }
}
module.exports = { SqlEditorMain };
