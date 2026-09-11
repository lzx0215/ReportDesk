const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const update = path.resolve(process.argv[2]);
const run = path.join(root, 'artifacts/verification/desktop', 'update-' + Date.now());
const installed = path.join(run, 'installation with spaces');
const manifest = JSON.parse(fs.readFileSync(path.join(update, 'manifest.json'), 'utf8'));
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
function apply(extra = []) {
  return execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(update, 'Apply-Update.ps1'), '-TargetDirectory', installed, ...extra], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}
function checkFiles(property) { for (const f of manifest.files) assert.equal(hash(path.join(installed, f.path)), f[property]); }
let desktop;
(async () => {
  fs.mkdirSync(run, { recursive: true });
  fs.cpSync(path.resolve(process.argv[3] || path.join(root, 'artifacts/desktop/ReportDesk-win32-x64')), installed, { recursive: true });
  const visibility = '<ReportVisibility mode="all" />';
  fs.writeFileSync(path.join(installed, 'report-visibility.xml'), visibility);
  const sentinel = path.join(installed, 'user-owned-notes.txt'); fs.writeFileSync(sentinel, 'must survive');
  assert.match(apply(['-VerifyOnly']), /PASS update verification/); checkFiles('baseSha256');
  // Reject wrong baseline before changing any target file.
  const target = path.join(installed, manifest.files[0].path); const original = fs.readFileSync(target);
  fs.appendFileSync(target, 'wrong baseline');
  assert.throws(() => apply(), /File verification failed/);
  for (const f of manifest.files.slice(1)) assert.equal(hash(path.join(installed, f.path)), f.baseSha256);
  fs.writeFileSync(target, original);
  // Reject corrupt delivery bytes before touching the installation.
  const payload = path.join(update, 'payload', manifest.files[0].path); const delivered = fs.readFileSync(payload);
  try { fs.appendFileSync(payload, 'bad delivery'); assert.throws(() => apply(), /File verification failed/); checkFiles('baseSha256'); }
  finally { fs.writeFileSync(payload, delivered); }
  const applied = apply(); assert.match(applied, /PASS update/); checkFiles('sha256');
  assert.match(apply(), /already installed/);
  // A previous installation may already contain older backups. Use this update's reported backup.
  const backup = applied.match(/Backup: ([^\r\n]+)/)[1].trim();
  const his = path.join(run, 'HIS root'); const xmlDir = path.join(his, 'Config', 'Xml'); fs.mkdirSync(xmlDir, { recursive: true });
  const name = '关联验收';
  const query = `<ReportQueryInfo><QueryFilePath>\\Config\\Xml\\${name}查询设置.xml</QueryFilePath><ReportInfo><IsDetail>true</IsDetail><DetailDirectory>\\Config\\Xml\\明细.xml</DetailDirectory></ReportInfo><QueryDataSource><QueryDataSource><Name>dtMain</Name><Sql>select 1 from dual</Sql><SqlType>MainReportUsing</SqlType><AddMapColumn>true</AddMapColumn><IsCross>true</IsCross></QueryDataSource></QueryDataSource></ReportQueryInfo>`;
  fs.writeFileSync(path.join(xmlDir, name + '查询设置.xml'), query);
  for (const file of [name + '报表设置.xml', '明细.xml']) fs.writeFileSync(path.join(xmlDir, file), '<Spread class="FarPoint.Win.Spread.FpSpread" />');
  fs.writeFileSync(path.join(his, 'other.xml'), '<configuration />');
  const env = { ...process.env, LOCALAPPDATA: path.join(run, 'local-data') }; delete env.ELECTRON_RUN_AS_NODE;
  for (let pass = 0; pass < 2; pass++) {
    desktop = await electron.launch({ executablePath: path.join(installed, 'ReportDesk.exe'), env });
    const page = await desktop.firstWindow(); const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
    assert.throws(() => apply(), /Close ReportDesk/);
    assert.equal(await page.locator('.report-item').count(), 0);
    assert.equal(await page.locator('#favorites,#recent,#category,#star').count(), 0);
    {
      await desktop.evaluate(({ dialog }, folder) => { dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [folder] }); }, his);
      await page.click('#import-folder');
      await page.waitForFunction(() => document.querySelector('#notice').open && !document.querySelector('#import-folder').disabled);
      const text = await page.locator('#notice-body').textContent();
      assert.match(text, /导入 1 张/); assert.match(text, /发现版式 XML 2 份/); assert.match(text, /显式路径匹配 1/);
      await page.screenshot({ path: path.join(run, 'import-summary.png') });
      await page.click('[data-close=notice]');
    }
    await page.locator('.report-item').filter({ hasText: name }).click();
    await page.waitForFunction(() => !document.querySelector('#metadata-open').disabled);
    assert.equal(await page.locator('#query').isDisabled(), true);
    assert.match(await page.locator('#issues').textContent(), /IsCross/);
    await page.click('#metadata-open'); await page.click('#related-xml');
    await page.waitForFunction(() => document.querySelector('#notice').open && !document.querySelector('#import-folder').disabled);
    const related = await page.locator('#notice-body').textContent();
    assert.ok(related.includes(his)); assert.match(related, /已按显式路径匹配/); assert.match(related, /名称候选/);
    await page.screenshot({ path: path.join(run, 'related-' + pass + '.png') });
    await page.click('[data-close=notice]'); await page.click('[data-close=metadata]');
    await page.click('#demo'); await page.waitForFunction(() => !document.querySelector('#query').disabled);
    await page.click('#query'); await page.waitForFunction(() => !document.querySelector('#query').disabled);
    assert.equal(await page.locator('tbody tr').count(), 14); assert.deepEqual(errors, []);
    await page.screenshot({ path: path.join(run, 'updated-desktop-' + pass + '.png') });
    await desktop.close(); desktop = null;
  }
  const catalog = path.join(env.LOCALAPPDATA, 'ReportDesk', 'catalog.json'); assert.ok(!fs.existsSync(catalog));
  // Simulate a user-owned old catalog for rollback preservation, without loading it.
  fs.writeFileSync(catalog, '{"Reports":[],"Connection":{}}'); const catalogHash = hash(catalog);
  assert.match(apply(['-Rollback', '-BackupDirectory', backup, '-VerifyOnly']), /PASS rollback verification/);
  assert.match(apply(['-Rollback', '-BackupDirectory', backup]), /PASS rollback/); checkFiles('baseSha256');
  assert.equal(hash(catalog), catalogHash);
  assert.match(apply(), /PASS update/); checkFiles('sha256');
  assert.equal(hash(catalog), catalogHash);
  assert.equal(fs.readFileSync(path.join(installed, 'report-visibility.xml'), 'utf8'), visibility);
  assert.equal(fs.readFileSync(sentinel, 'utf8'), 'must survive');
  for (const f of manifest.requires) assert.equal(hash(path.join(installed, f.path)), f.sha256);
  assert.equal(fs.readFileSync(path.join(xmlDir, name + '查询设置.xml'), 'utf8'), query);
  fs.writeFileSync(path.join(run, 'PASS.txt'), 'PASS: verify-only, wrong baseline/corrupt payload rejection, running process rejection, three-file update, idempotence, actual packaged Electron root import/related XML and restart, unsupported mapping guard, demo query, rollback/reinstall, user files and dependencies preserved. No Oracle connection.\n');
  console.log('PASS update checks: ' + run);
})().catch(async e => { console.error(e); if (desktop) { try { const page = await desktop.firstWindow(); await page.screenshot({ path: path.join(run, 'failure.png') }); await desktop.close(); } catch {} } process.exitCode = 1; });
