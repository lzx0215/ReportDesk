// Real LIB XML copies only; isolated data/config, no Oracle calls or result fixtures.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { once } = require('node:events'), { Bridge } = require('../../src/ReportDesk.Desktop/bridge.cjs');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const lib = path.resolve(process.argv[2] || process.env.REPORTDESK_LIB || 'E:/his/LIB');
const run = path.join(root, 'artifacts/verification/desktop', 'new-reports-' + Date.now());
const screenshots = path.join(root, 'output/playwright', path.basename(run));
const data = path.join(run, 'data'), config = path.join(run, 'config'), source = path.join(run, 'source');
for (const dir of [data, config, source, screenshots]) fs.mkdirSync(dir, { recursive: true });
const originals = ['普通门诊处方记录查询设置.xml', '住院各科有效收入统计查询设置.xml', '普通门诊处方记录报表设置.xml'].map(n => path.join(lib, 'Config/Xml', n));
const buffers = originals.map(f => fs.readFileSync(f));
const firstFile = path.join(source, path.basename(originals[0]));
fs.writeFileSync(firstFile, buffers[0]);
let bridge, desktop, cancelOnProgress = false, cancelPromise;
const start = () => new Bridge(path.join(root, 'artifacts/host/ReportDesk.Host.exe'), data, config, true, () => {
  if (cancelOnProgress) { cancelOnProgress = false; cancelPromise = bridge.cancel(); }
}, () => {});
async function stop() { const done = once(bridge.process, 'exit'); bridge.close(); await done; bridge = null; }
(async () => {
  bridge = start(); await bridge.ready;
  await assert.rejects(bridge.call('checkNewReports'), /先导入/);
  const imported = await bridge.call('import', { path: source, folder: true });
  assert.equal(imported.imported, 1); const first = imported.reports[0];
  // Remember overlapping sources; the same report must only be added once.
  await bridge.call('import', { path: firstFile });
  const before = await bridge.call('definition', { reportId: first.id });
  const nested = path.join(source, 'nested'); fs.mkdirSync(nested);
  fs.writeFileSync(path.join(nested, path.basename(originals[1])), buffers[1]);
  fs.writeFileSync(path.join(source, path.basename(originals[2])), buffers[2]);
  let checked = await bridge.call('checkNewReports');
  assert.equal(checked.addedCount, 1); assert.equal(checked.visibleAddedCount, 1); assert.equal(checked.reports.length, 2);
  assert.equal((await bridge.call('checkNewReports')).addedCount, 0);
  assert.deepEqual(await bridge.call('definition', { reportId: first.id }), before);
  // Source loss must preserve existing reports and remembered sources.
  const saved = fs.readFileSync(path.join(data, 'import-sources.json'), 'utf8');
  const moved = path.join(run, 'source-unavailable');
  assert.ok(source.startsWith(run + path.sep) && moved.startsWith(run + path.sep));
  fs.renameSync(source, moved);
  try {
    checked = await bridge.call('checkNewReports');
    assert.equal(checked.reports.length, 2); assert.ok(checked.errors.length > 0);
    assert.equal(fs.readFileSync(path.join(data, 'import-sources.json'), 'utf8'), saved);
  } finally { fs.renameSync(moved, source); }
  // Do not refresh an existing definition during an additions-only scan.
  fs.writeFileSync(firstFile, buffers[1]);
  assert.equal((await bridge.call('checkNewReports')).addedCount, 0);
  assert.deepEqual(await bridge.call('definition', { reportId: first.id }), before);
  fs.writeFileSync(firstFile, buffers[0]);
  await stop();
  // A single-file source does not silently expand to all sibling reports.
  fs.writeFileSync(path.join(data, 'import-sources.json'), JSON.stringify([{ Path: firstFile, Folder: false }]));
  bridge = start(); await bridge.call('bootstrap');
  assert.equal((await bridge.call('checkNewReports')).reports.length, 1); await stop();
  // Cancellation against the real corpus must not commit any staged additions.
  fs.writeFileSync(path.join(data, 'import-sources.json'), JSON.stringify([{ Path: lib, Folder: true }]));
  bridge = start(); cancelOnProgress = true;
  await assert.rejects(bridge.call('checkNewReports'), e => e.cancelled === true); await cancelPromise;
  assert.deepEqual(await bridge.call('list'), []); await stop();
  // New hidden reports can be discovered but every visible entry point stays filtered.
  fs.writeFileSync(path.join(data, 'import-sources.json'), JSON.stringify([{ Path: source, Folder: true }]));
  fs.writeFileSync(path.join(config, 'report-visibility.xml'), `<ReportVisibility mode="selected"><Report id="${first.id}" /></ReportVisibility>`);
  bridge = start(); checked = await bridge.call('checkNewReports');
  assert.equal(checked.addedCount, 2); assert.equal(checked.visibleAddedCount, 1); assert.equal(checked.reports.length, 1);
  assert.equal((await bridge.call('checkNewReports')).addedCount, 0); await stop();
  fs.writeFileSync(path.join(config, 'report-visibility.xml'), '<ReportVisibility mode="all" />');
  // Corrupt preferences are rejected and left intact.
  fs.writeFileSync(path.join(data, 'import-sources.json'), 'BROKEN-TEST-CONFIG');
  bridge = start(); await assert.rejects(bridge.call('checkNewReports'));
  assert.equal(fs.readFileSync(path.join(data, 'import-sources.json'), 'utf8'), 'BROKEN-TEST-CONFIG'); await stop();
  // Exercise the UI on the complete real corpus, with networking blocked by offline mode.
  fs.writeFileSync(path.join(data, 'import-sources.json'), JSON.stringify([{ Path: lib, Folder: true }, { Path: source, Folder: true }]));
  const env = { ...process.env, REPORTDESK_TEST: '1', REPORTDESK_TEST_DATA: data, REPORTDESK_TEST_CONFIG: config }; delete env.ELECTRON_RUN_AS_NODE;
  desktop = await electron.launch({ args: [path.join(root, 'src/ReportDesk.Desktop')], env });
  const page = await desktop.firstWindow(); page.setDefaultTimeout(60000);
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
  const initialCount = await page.locator('.report-item').count(); assert.ok(initialCount >= 1284);
  assert.equal(await page.locator('#demo, #recheck').count(), 0);
  assert.deepEqual(await page.locator('.rail button').evaluateAll(ns => ns.map(n => n.id)), ['all', 'import-file', 'import-folder', 'check-new', 'settings-open', 'logs']);
  const gap = await page.evaluate(() => document.querySelector('#import-file').getBoundingClientRect().top - document.querySelector('#all').getBoundingClientRect().bottom);
  assert.ok(gap >= 0 && gap <= 16);
  await page.evaluate(async () => {
    const report = reports.find(r => r.name.includes('中联医保费用明细')) || reports.find(r => r.name === '普通门诊处方记录');
    await task('选择真实报表', () => select(report.id));
  });
  assert.equal(await page.locator('#report-status').count(), 0);
  assert.equal(await page.locator('#issues').innerText(), '');
  assert.equal(await page.locator('#locations').isVisible(), false);
  for (const size of [[1040, 700], [1440, 930]]) {
    await desktop.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), size);
    await page.screenshot({ path: path.join(screenshots, 'main-' + size[0] + '.png') });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth || document.querySelector('.workspace').scrollWidth > document.querySelector('.workspace').clientWidth), false);
  }
  await page.click('#locations-open');
  assert.match(await page.locator('#location-source').innerText(), /\.xml/i);
  assert.ok((await page.locator('#report-locations').innerText()).length > 0);
  await page.screenshot({ path: path.join(screenshots, 'locations.png') });
  await page.keyboard.press('Escape'); assert.equal(await page.locator('#locations').isVisible(), false);
  const state = await page.evaluate(() => { window.originalFieldNodes = [...fields.values()].map(f => f.input); return { selected, values: values() }; });
  fs.writeFileSync(path.join(nested, '新增报表.xml'), buffers[0]);
  await page.click('#check-new'); await page.waitForFunction(() => document.querySelector('#notice').open);
  assert.match(await page.locator('#notice-body').innerText(), /新增 1 张报表/);
  assert.equal(await page.locator('.report-item').count(), initialCount + 1);
  assert.deepEqual(await page.evaluate(() => ({ selected, values: values() })), state);
  assert.equal(await page.evaluate(() => window.originalFieldNodes.every((n, i) => n === [...fields.values()][i].input)), true);
  await page.click('[data-close="notice"]');
  await page.click('#metadata-open'); assert.ok((await page.locator('#meta-guidance').innerText()).length > 0); await page.click('[data-close="metadata"]');
  // Safety blockers must remain visible even though the purpose/adaptation banner is gone.
  await page.evaluate(async () => { const blocked = reports.find(r => r.issues.length); await task('选择待适配报表', () => select(blocked.id)); });
  assert.match(await page.locator('#issues').innerText(), /待适配/);
  assert.deepEqual(errors, []);
  await desktop.close(); desktop = null;
  originals.forEach((f, i) => assert.deepEqual(fs.readFileSync(f), buffers[i]));
  fs.writeFileSync(path.join(run, 'PASS.txt'), 'PASS real LIB discovery, nested/overlapping sources, duplicates, existing definitions, missing sources, single-file scope, cancellation, visibility, corrupt preferences, UI locations/navigation/new button/field preservation and blockers. Oracle NOT RUN.');
  console.log('PASS ' + run + '\nScreenshots: ' + screenshots);
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(async () => { if (bridge) await stop(); if (desktop) await desktop.close(); });
