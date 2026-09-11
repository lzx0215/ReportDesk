// Real Electron + offline .NET host. All catalogs and exports stay in artifacts.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const dir = path.join(root, 'artifacts/verification/desktop', 'light-' + Date.now());
fs.mkdirSync(dir, { recursive: true });
for (const name of ['data', 'config']) fs.mkdirSync(path.join(dir, name));
let desktop;
(async () => {
  const env = { ...process.env, REPORTDESK_TEST: '1', REPORTDESK_TEST_DATA: path.join(dir, 'data'), REPORTDESK_TEST_CONFIG: path.join(dir, 'config') };
  delete env.ELECTRON_RUN_AS_NODE;
  desktop = await electron.launch({ args: [path.join(root, 'src/ReportDesk.Desktop')], env });
  const page = await desktop.firstWindow(), errors = [];
  page.setDefaultTimeout(15000);
  page.on('pageerror', e => errors.push(e.message));
  const ready = () => page.waitForFunction(() => !document.querySelector('#demo').disabled);
  const shot = name => page.screenshot({ path: path.join(dir, name + '.png') });
  const size = async (width, height) => {
    await desktop.evaluate(({ BrowserWindow }, [w, h]) => BrowserWindow.getAllWindows()[0].setContentSize(w, h), [width, height]);
    await page.waitForFunction(([w, h]) => innerWidth === w && innerHeight === h, [width, height]);
  };
  const layout = async () => {
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    for (const selector of ['.rail', '#export', '#next', '#progress', 'footer']) {
      const box = await page.locator(selector).boundingBox();
      assert.ok(box && box.x >= 0 && box.x + box.width <= (await page.evaluate(() => innerWidth)) + 1, selector + ' horizontal bounds');
      assert.ok(box.y + box.height <= (await page.evaluate(() => innerHeight)) + 1, selector + ' vertical bounds');
    }
    assert.equal(await page.locator('#progress').evaluate(e => getComputedStyle(e).height), '10px');
    assert.equal(await page.locator('.app').evaluate(e => getComputedStyle(e).backgroundColor), 'rgb(247, 246, 243)');
  };
  await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
  await ready();
  assert.equal(await page.locator('.rail button svg').count(), 8);
  assert.equal(await page.locator('#query').isDisabled(), true);
  for (const [w, h] of [[1424,891], [1366,768], [1280,720]]) { await size(w,h); await layout(); await shot(`empty-${w}x${h}`); }
  await size(1424,891);
  await page.click('#demo'); await ready();
  await page.fill('[data-parameter=begin]', '2026-01-01');
  await page.fill('[data-parameter=end]', '2026-06-30');
  await page.locator('[data-parameter=department]').focus();
  await page.keyboard.press('Tab');
  assert.equal(await page.locator('#query').evaluate(e => e === document.activeElement), true);
  assert.equal(await page.locator('#query').evaluate(e => getComputedStyle(e).outlineWidth), '2px');
  await shot('conditions-focus');
  await page.hover('#query');
  assert.equal(await page.locator('#query').evaluate(e => getComputedStyle(e).backgroundColor), 'rgb(203, 178, 127)');
  await page.click('#query'); await ready();
  assert.equal(await page.locator('tbody tr').count(), 200);
  await page.click('#next'); await ready(); assert.equal(await page.locator('tbody tr').count(), 162);
  await page.click('#previous'); await ready();
  await page.locator('th button').last().click(); await ready();
  assert.equal(await page.locator('th').last().getAttribute('aria-sort'), 'ascending');
  await page.locator('tbody input').first().check();
  await page.click('#copy'); await ready();
  const copied = await desktop.evaluate(({ clipboard }) => clipboard.readText());
  assert.ok(copied.includes('模拟编号') && copied.includes('000'));
  for (const [w,h] of [[1424,891], [1366,768], [1280,720], [1046,768]]) {
    await size(w,h); await layout(); await page.mouse.move(0,0); await shot(`table-${w}x${h}`);
  }
  await page.locator('#table-wrap').evaluate(e => { e.scrollTop = e.scrollHeight; e.scrollLeft = e.scrollWidth; });
  assert.ok(await page.locator('#table-wrap').evaluate(e => e.scrollTop > 0));
  await size(1424,891);
  await page.fill('#result-filter','演示科室 B'); await page.locator('#filter-form button').click(); await ready();
  assert.equal(await page.locator('tbody tr').count(),181);
  const exported = path.join(dir, 'filtered.xlsx');
  await desktop.evaluate(({ dialog }, file) => { dialog.showSaveDialog = async () => ({ canceled: false, filePath: file }); }, exported);
  await page.click('#export'); await ready(); assert.ok(fs.existsSync(exported));
  await page.fill('#result-filter','no match'); await page.locator('#filter-form button').click(); await ready();
  assert.equal(await page.locator('tbody tr').count(),0); await shot('no-results');
  await page.click('#metadata-open'); await shot('metadata'); await page.click('[data-close=metadata]');
  await page.click('#sql'); await ready(); await shot('sql'); await page.click('[data-close=notice]');
  await page.click('#help'); await shot('help'); await page.click('[data-close=notice]');
  await page.click('#settings-open'); await ready(); await shot('connection');
  await page.selectOption('#conn-mode','1'); await page.locator('#connection summary').click(); await shot('connection-tns');
  await page.click('[data-close=connection]');
  await page.click('#star'); await ready(); assert.ok((await page.locator('#star').textContent()).includes('已收藏'));
  await page.click('#favorites'); await ready(); assert.equal(await page.locator('.report-item').count(),1);
  await page.click('#recent'); await ready(); assert.equal(await page.locator('.report-item').count(),1);
  await page.fill('#report-search','no matching report');
  await page.waitForFunction(() => document.querySelectorAll('.report-item').length === 0); await ready(); await shot('search-empty');
  await page.click('#demo'); await ready();
  await page.fill('[data-parameter=begin]', '2026-06-30'); await page.fill('[data-parameter=end]', '2026-01-01');
  await page.click('#query'); await ready(); assert.equal(await page.locator('#execution').getAttribute('class'),'execution error');
  await shot('error'); await page.click('[data-close=notice]');
  await page.fill('[data-parameter=begin]', '0001-01-01'); await page.fill('[data-parameter=end]', '9999-12-31');
  await page.click('#query');
  try {
    await page.waitForFunction(() => document.querySelector('#execution').classList.contains('running'));
    assert.equal(await page.locator('#progress').getAttribute('aria-valuenow'), null);
    assert.equal(await page.locator('#progress > div').evaluate(e => getComputedStyle(e).animationName), 'execution-flow');
    await shot('running');
  } finally { await page.click('#cancel'); }
  await ready(); assert.ok((await page.locator('#operation-status').textContent()).includes('取消')); await shot('cancelled');
  await page.fill('[data-parameter=begin]', '2026-01-01'); await page.fill('[data-parameter=end]', '2026-01-07');
  await page.click('#query'); await ready(); assert.equal(await page.locator('tbody tr').count(),14);
  await shot('completed');
  const displays = await desktop.evaluate(({ screen }) => screen.getAllDisplays().map(d => ({ size: d.size, scaleFactor: d.scaleFactor })));
  assert.deepEqual(errors, []);
  fs.writeFileSync(path.join(dir,'PASS.json'), JSON.stringify({ result: 'PASS', displays, viewport: 'Electron setContentSize; no CSS zoom or forced device scale', checks: 'Empty, query controls, focus/hover/disabled, real offline 362-row query, sort, segment, selection, copy, scroll, filter, export, dialogs, favorites/recent/search, error, running, cancellation, retry', notRun: 'Live Oracle, Win10 clean machine, native dropdown/date popup inspection' },null,2));
  await desktop.close(); desktop = null; console.log('PASS light theme: ' + dir);
})().catch(async e => { console.error(e); if (desktop) { try { const p = await desktop.firstWindow(); await p.screenshot({ path: path.join(dir,'failure.png') }); await desktop.close(); } catch {} } process.exitCode = 1; });
