const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const directory = path.join(root, 'artifacts/verification/desktop', 'package-' + Date.now());
fs.mkdirSync(directory, { recursive: true });
let desktop;
(async () => {
  const executable = path.join(root, 'artifacts/desktop/ReportDesk-win32-x64/ReportDesk.exe');
  assert.ok(fs.existsSync(executable), 'Packaged EXE is missing');
  const env = { ...process.env, LOCALAPPDATA: directory };
  delete env.ELECTRON_RUN_AS_NODE; delete env.REPORTDESK_TEST;
  desktop = await electron.launch({ executablePath: executable, env });
  const page = await desktop.firstWindow(); page.setDefaultTimeout(20000);
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
  assert.equal(await desktop.evaluate(({ app }) => app.isPackaged), true);
  assert.equal(await desktop.evaluate(() => process.arch), 'x64');
  assert.equal(await page.title(), 'ReportDesk · 报表查询');
  assert.equal(await page.locator('#demo').count(), 0, 'Removed demo entry must not return');
  assert.equal(await page.locator('.report-item').count(), 0);
  assert.match(await page.locator('footer').textContent(), /ReportDesk 0\.3\.0 · x64/);
  for (const [width, height] of [[1040, 700], [1440, 930], [1920, 1080]]) {
    await desktop.evaluate(({ BrowserWindow }, [w, h]) => BrowserWindow.getAllWindows()[0].setContentSize(w, h), [width, height]);
    await page.waitForFunction(([w, h]) => innerWidth === w && innerHeight === h, [width, height]);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  }
  assert.deepEqual(errors, []);
  await page.screenshot({ path: path.join(directory, 'packaged-desktop.png') });
  fs.writeFileSync(path.join(directory, 'PASS.txt'), 'PASS: packaged Windows x64 EXE, isolated user data, current shell, no removed demo entry and 1040/1440/1920 geometry. No real Oracle connection.\n');
  console.log('PASS packaged EXE: ' + directory);
  await desktop.close(); desktop = null;
})().catch(async e => { console.error(e); if (desktop) await desktop.close().catch(() => {}); process.exitCode = 1; });
