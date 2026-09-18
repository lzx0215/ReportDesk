'use strict';
// Real packaged Electron -> IPC -> Host -> loopback Oracle -> XML copies. No patient data.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const executable = process.argv[2] || path.join(root, 'artifacts/layout-editor/ReportDesk-win32-x64/ReportDesk.exe');
const original = process.argv[3] || 'E:/his/LIB/Config/Xml/(病案)观察室工作日志查询设置.xml';
const originalLayout = original.replace('查询设置.xml', '报表设置.xml');
const configFile = process.argv[4] || 'E:/his/LIB/Conf/ObjectConfig.xml';
const reconcile = process.argv[5] === '--reconcile';
const run = path.join(root, 'artifacts/verification/desktop', 'layout-e2e-' + Date.now());
const source = path.join(run, 'source'), data = path.join(run, 'data');
for (const dir of [source, data]) fs.mkdirSync(dir, { recursive: true });
const file = path.join(source, path.basename(original)), layout = path.join(source, path.basename(originalLayout));
const beforeQuery = fs.readFileSync(original), beforeLayout = fs.readFileSync(originalLayout);
fs.writeFileSync(file, beforeQuery); fs.writeFileSync(layout, beforeLayout);
const queryTimestamp = fs.statSync(file).mtimeMs;
let desktop, page, step = 'launch';
(async () => {
  const env = { ...process.env, LOCALAPPDATA: data }; delete env.ELECTRON_RUN_AS_NODE;
  desktop = await electron.launch({ executablePath: executable, env });
  page = await desktop.firstWindow(); page.setDefaultTimeout(20000);
  let pageErrors = 0; page.on('pageerror', () => pageErrors++);
  await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
  step = 'loopback connection';
  // Decode XML in memory. Never log, screenshot or persist the connection string/password.
  const connection = await page.evaluate(xml => {
    const doc = new DOMParser().parseFromString(xml, 'application/xml');
    for (const node of doc.querySelectorAll('*')) for (const attr of node.attributes)
      if (attr.name === 'connectionString' || attr.name === 'connection-string') return attr.value;
    return '';
  }, fs.readFileSync(configFile, 'utf8'));
  const parts = new Map();
  for (const m of connection.matchAll(/\s*([^=;]+)\s*=\s*(?:"((?:[^"]|"")*)"|'((?:[^']|'')*)'|([^;]*))\s*(?:;|$)/g))
    parts.set(m[1].trim().toLowerCase(), m[2] !== undefined ? m[2].replace(/""/g, '"') : m[3] !== undefined ? m[3].replace(/''/g, "'") : m[4].trim());
  assert.ok(parts.has('user id') && parts.has('password'));
  const connected = await page.evaluate(async args => (await window.reportDesk.call('testConnection', args)).ok,
    { host: '127.0.0.1', port: 1521, service: 'ORCL', username: parts.get('user id'), password: parts.get('password'), remember: false });
  parts.clear(); assert.equal(connected, true, 'Loopback connection failed');
  step = 'import and edit';
  await desktop.evaluate(({ dialog }, target) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [target] });
    dialog.showMessageBox = async () => ({ response: 0 });
  }, file);
  await page.click('#import-file'); await page.waitForFunction(() => document.querySelector('#notice').open); await page.click('[data-close="notice"]');
  await page.locator('.report-item').first().click();
  if (await page.locator('#query-conditions-view').isVisible()) await page.click('#conditions-dismiss');
  await page.click('#sql');
  await page.waitForFunction(() => document.querySelector('#sql-editor').open && !document.querySelector('#sql-editor-input').disabled && document.querySelector('#sql-editor-input').value.length > 0);
  const oldSql = await page.locator('#sql-editor-input').inputValue();
  const draft = reconcile ? oldSql : 'select q.*, \'layout-check\' AS "新增字段" from (\n' + oldSql + '\n) q where 1=0';
  const expectedCount = (oldSql.includes('a.apply_id 申请id') ? 6 : 5) + (reconcile ? 0 : 1);
  const expectedHeader = reconcile ? '申请ID' : '新增字段';
  if (!reconcile) await page.locator('#sql-editor-input').fill(draft);
  step = 'preview'; await page.click('#sql-editor-sync'); await page.waitForFunction(() => !busy);
  assert.equal(await page.locator('#layout-editor').evaluate(n => n.open), true, 'Preview not opened');
  assert.equal(await page.locator('#layout-editor-table tbody tr').count(), expectedCount);
  assert.equal(await page.locator('#layout-editor-table tbody tr').last().locator('input[type=text]').inputValue(), expectedHeader);
  if (reconcile) {
    assert.match(await page.locator('#layout-editor-title').innerText(), /SQL 已保存/);
    assert.equal(await page.locator('#layout-editor-table tbody tr.layout-added').count(), 1);
    assert.match(await page.locator('#layout-editor-table tbody tr').first().locator('td').last().innerText(), /原第 1 列/);
  }
  assert.deepEqual(fs.readFileSync(file), beforeQuery); assert.deepEqual(fs.readFileSync(layout), beforeLayout);
  await page.locator('#layout-editor-table tbody tr').last().locator('input[type=number]').fill('180');
  step = 'geometry';
  for (const width of [1040, 1440, 1920]) {
    await desktop.evaluate(({ BrowserWindow }, width) => BrowserWindow.getAllWindows()[0].setSize(width, 930), width);
    await page.waitForTimeout(150);
    const ok = await page.evaluate(() => {
      const modal = document.querySelector('#layout-editor').getBoundingClientRect();
      return modal.left >= 0 && modal.right <= innerWidth && modal.top >= 0 && modal.bottom <= innerHeight &&
        [...document.querySelectorAll('#layout-editor input,#layout-editor button')].every(n => { const r = n.getBoundingClientRect(); return r.left >= modal.left && r.right <= modal.right && r.height >= 39; });
    });
    assert.equal(ok, true, 'Preview controls overflow');
    await page.screenshot({ path: path.join(run, 'preview-' + width + '.png') });
  }
  step = 'cancel confirmation';
  await page.click('#layout-editor-save'); await page.waitForFunction(() => !busy);
  assert.equal(await page.locator('#layout-editor').evaluate(n => n.open), true);
  assert.deepEqual(fs.readFileSync(file), beforeQuery); assert.deepEqual(fs.readFileSync(layout), beforeLayout);
  step = 'confirmed save';
  await desktop.evaluate(({ dialog }) => { dialog.showMessageBox = async () => ({ response: 1 }); });
  await page.click('#layout-editor-save'); await page.waitForFunction(() => !busy);
  assert.equal(await page.locator('#layout-editor').evaluate(n => n.open), false);
  assert.match(await page.locator('#sql-editor-status').innerText(), /已保存，并已回读核对/);
  const saved = await page.evaluate(({ query, layout, last }) => {
    const parse = text => new DOMParser().parseFromString(text, 'application/xml'); const q = parse(query), l = parse(layout);
    return { sql: q.querySelector('QueryDataSource > QueryDataSource > Sql').textContent,
      count: l.querySelector('ColumnCount').textContent, header: l.querySelector(`Cell[row="1"][column="${last}"] Data`).textContent,
      width: l.querySelector(`AxisModels > Column > Items > Item[index="${last}"] > Size`).textContent };
  }, { query: fs.readFileSync(file, 'utf8'), layout: fs.readFileSync(layout, 'utf8'), last: expectedCount - 1 });
  assert.deepEqual(saved, { sql: draft, count: String(expectedCount), header: expectedHeader, width: '180' });
  if (reconcile) { assert.deepEqual(fs.readFileSync(file), beforeQuery); assert.equal(fs.statSync(file).mtimeMs, queryTimestamp); }
  assert.equal(fs.readdirSync(source).length, 2);
  await page.click('#sql-editor-close'); await page.click('#sql');
  await page.waitForFunction(() => document.querySelector('#sql-editor').open && !document.querySelector('#sql-editor-input').disabled);
  assert.equal(await page.locator('#sql-editor-input').inputValue(), draft);
  await page.click('#sql-editor-sync'); await page.waitForFunction(() => !busy);
  assert.equal(await page.locator('#layout-editor-table tbody tr').count(), expectedCount);
  assert.equal(await page.locator('#layout-editor-table tbody tr.layout-added').count(), 0);
  await page.click('#layout-editor-cancel');
  assert.deepEqual(fs.readFileSync(original), beforeQuery); assert.deepEqual(fs.readFileSync(originalLayout), beforeLayout);
  assert.equal(pageErrors, 0);
  await page.screenshot({ path: path.join(run, 'saved.png') });
  fs.writeFileSync(path.join(run, 'PASS.txt'), 'PASS packaged Electron/IPC/Host/local Oracle schema-only preview, alias/header/width save, cancellation no writes, 1040/1440/1920 geometry, reopen no duplicate columns, original library unchanged. ' + (reconcile ? 'Saved SQL 6/template 5 recovery; query bytes and timestamp unchanged. ' : '') + 'No patient rows or saved password. HIS client/printing NOT RUN.\n');
  console.log('PASS ' + run);
})().catch(async e => {
  console.error('FAIL stage=' + step + ' category=' + e.name);
  if (page && !['launch', 'loopback connection'].includes(step)) {
    console.log('Editor status: ' + await page.locator('#sql-editor-status').textContent());
    await page.screenshot({ path: path.join(run, 'failed.png') });
  }
  process.exitCode = 1;
}).finally(async () => {
  if (desktop) { await desktop.evaluate(({ dialog }) => { dialog.showMessageBoxSync = () => 1; }); await desktop.close(); }
});
