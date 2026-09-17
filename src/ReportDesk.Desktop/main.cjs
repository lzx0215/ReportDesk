const { app, BrowserWindow, ipcMain, dialog, shell, protocol, net, session } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { Bridge } = require('./bridge.cjs');
const { SqlEditorMain } = require('./sql-editor-main.cjs');
const sqlEditor = new SqlEditorMain(dialog, shell);
let window, bridge, closing = false, uiBusy = false;
const testing = !app.isPackaged && process.env.REPORTDESK_TEST === '1';
const dataDirectory = testing ? path.resolve(process.env.REPORTDESK_TEST_DATA) : path.join(process.env.LOCALAPPDATA, 'ReportDesk');
const configDirectory = testing ? path.resolve(process.env.REPORTDESK_TEST_CONFIG) : app.isPackaged ? path.dirname(process.execPath) : path.resolve(__dirname, '../..');
const ui = 'reportdesk://app/index.html';
protocol.registerSchemesAsPrivileged([{ scheme: 'reportdesk', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);
app.setPath('userData', path.join(dataDirectory, 'desktop-ui'));
app.commandLine.appendSwitch('disable-gpu-shader-disk-cache');
app.commandLine.appendSwitch('disable-http-cache');
const lock = app.requestSingleInstanceLock();
if (!lock) app.quit();
else {
  app.on('second-instance', () => { if (window) { if (window.isMinimized()) window.restore(); window.focus(); } });
  app.whenReady().then(async () => {
    const assets = new Set(['/index.html', '/styles.css', '/execution.css', '/query-form.js', '/renderer.js', '/sql-editor.js', '/sql-editor.css']);
    const ses = session.fromPartition('reportdesk');
    ses.protocol.handle('reportdesk', request => {
      const url = new URL(request.url);
      if (url.host !== 'app' || !assets.has(url.pathname)) return new Response('', { status: 404 });
      return net.fetch(pathToFileURL(path.join(__dirname, 'ui', url.pathname.slice(1))).href);
    });
    // Non-persistent session. No query values, result rows or credentials in local storage/cache.
    ses.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
    ses.setPermissionCheckHandler(() => false);
    ses.webRequest.onBeforeRequest((details, callback) => callback({ cancel: !details.url.startsWith('reportdesk://app/') }));
    window = new BrowserWindow({ title: 'ReportDesk · 报表查询', width: 1440, height: 930, minWidth: 1040, minHeight: 700, backgroundColor: '#F7F6F3', show: false, autoHideMenuBar: true,
      webPreferences: { preload: path.join(__dirname, 'preload.cjs'), partition: 'reportdesk', nodeIntegration: false, contextIsolation: true, sandbox: true, devTools: !app.isPackaged, spellcheck: false } });
    window.removeMenu();
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('will-navigate', e => e.preventDefault());
    window.webContents.on('will-attach-webview', e => e.preventDefault());
    const executable = app.isPackaged ? path.join(process.resourcesPath, 'host', 'ReportDesk.Host.exe') : path.resolve(__dirname, '../../artifacts/host/ReportDesk.Host.exe');
    bridge = new Bridge(executable, dataDirectory, configDirectory, testing, message => window?.webContents.send('reportdesk:progress', message), message => {
      writeSafeLog('BackendExit'); window?.webContents.send('reportdesk:fatal', message);
    });
    window.on('close', e => {
      if (closing) return;
      if (uiBusy || bridge.active !== null) { e.preventDefault(); bridge.cancel().catch(() => {}); dialog.showMessageBox(window, { type: 'info', message: '已请求取消当前操作，请等待结束后关闭。连接握手可能仍需等待网络返回。' }); return; }
      if (!sqlEditor.allowClose(window)) { e.preventDefault(); return; }
      closing = true; bridge.close();
    });
    window.webContents.on('render-process-gone', () => { writeSafeLog('RendererExit'); bridge.cancel().catch(() => {}); });
    await window.loadURL(ui);
    if (!testing) window.show();
  }).catch(() => { writeSafeLog('DesktopStartup'); dialog.showErrorBox('ReportDesk', '启动失败，请检查程序文件及本地日志。'); app.quit(); });
}
function writeSafeLog(operation) {
  try { const date = new Date(); const directory = path.join(dataDirectory, 'logs'); fs.mkdirSync(directory, { recursive: true }); fs.appendFileSync(path.join(directory, `ReportDesk-${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}.log`), `${date.toISOString()} ERROR operation=${operation} version=${app.getVersion()} process=x64 message=[上下文已省略]\n`); return ''; }
  catch { return '本地错误日志写入失败。'; }
}
const allowed = new Set(['bootstrap','list','select','demo','definition','relatedFiles','recheck','checkNewReports','query','view','page','lookup','lookupPage','lookupAll','settings','saveSettings','testConnection','discoverTns','clear','sqlEditorCheck','reloadReport']);
ipcMain.handle('reportdesk:call', async (event, method, args = {}) => {
  if (event.sender !== window?.webContents || event.senderFrame !== window.webContents.mainFrame || event.senderFrame.url !== ui) return { ok: false, message: '非法调用来源。' };
  if (method === 'cancel') { await bridge.cancel(); return { ok: true, data: {} }; }
  if (method === 'logClient') { const notice = writeSafeLog('RendererError'); return { ok: true, data: { notice } }; }
  if (method === 'sqlEditorDirty') return sqlEditor.setDirty(args);
  if (uiBusy) return { ok: false, message: '正在处理另一项操作。' };
  uiBusy = true;
  try {
    if (!args || typeof args !== 'object' || Array.isArray(args)) throw new Error('参数格式无效。');
    let data;
    if (['sqlEditorOpen', 'sqlEditorSave', 'sqlEditorDiscard', 'sqlEditorReveal'].includes(method)) data = await sqlEditor.handle(method, args, window, bridge);
    else if (method === 'import') {
      const choice = await dialog.showOpenDialog(window, { title: args.folder ? '选择报表根目录或报表目录（自动识别并匹配 XML）' : '导入报表查询 XML', properties: args.folder ? ['openDirectory'] : ['openFile'], filters: [{ name: '报表 XML', extensions: ['xml'] }] });
      data = choice.canceled ? null : await bridge.call('import', { folder: !!args.folder, path: choice.filePaths[0] });
    } else if (method === 'export') {
      const choice = await dialog.showSaveDialog(window, { title: '导出当前结果', defaultPath: '报表结果.xlsx', filters: [{ name: 'Excel 工作簿', extensions: ['xlsx'] }] });
      data = choice.canceled ? null : await bridge.call('export', { ...args, path: choice.filePath });
    } else if (method === 'pickTns') {
      const choice = await dialog.showOpenDialog(window, { properties: ['openFile'], filters: [{ name: 'Oracle TNS', extensions: ['ora'] }] });
      data = choice.canceled ? null : { path: choice.filePaths[0], aliases: await bridge.call('tnsAliases', { path: choice.filePaths[0] }) };
    } else if (method === 'tnsAliases') data = await bridge.call('tnsAliases', { path: args.path });
    else if (method === 'openLogs') { const dir = path.join(dataDirectory, 'logs'); fs.mkdirSync(dir, { recursive: true }); const error = await shell.openPath(dir); if (error) throw new Error('无法打开日志目录。'); data = {}; }
    else if (method === 'copy') { const { clipboard } = require('electron'); if (typeof args.text !== 'string') throw new Error('复制内容无效。'); await clipboard.writeText(args.text); data = {}; }
    else if (allowed.has(method)) data = await bridge.call(method, args);
    else throw new Error('不支持的操作。');
    return { ok: true, data };
  } catch (e) { return { ok: false, cancelled: !!e.cancelled, message: e.message || '操作失败。' }; }
  finally { uiBusy = false; }
});
app.on('window-all-closed', () => app.quit());
