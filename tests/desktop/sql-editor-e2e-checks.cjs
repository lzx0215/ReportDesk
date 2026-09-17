'use strict';
// Exercise the real Electron -> IPC -> Host -> file path on a copy of a HIS XML.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const original = process.argv[2] || 'E:/his/LIB/Config/Xml/普通门诊处方记录查询设置.xml';
const run = path.join(root, 'artifacts/verification/desktop', 'sql-editor-e2e-' + Date.now());
const data = path.join(run, 'data'), config = path.join(run, 'config');
const source = process.argv[3] ? path.join(path.resolve(process.argv[3]), path.basename(run)) : path.join(run, 'source');
for (const dir of [data, config, source]) fs.mkdirSync(dir, { recursive: true });
const file = path.join(source, path.basename(original));
const before = fs.readFileSync(original);
fs.writeFileSync(file, before);
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
let desktop;
(async () => {
  const env = { ...process.env, REPORTDESK_TEST: '1', REPORTDESK_TEST_DATA: data, REPORTDESK_TEST_CONFIG: config };
  delete env.ELECTRON_RUN_AS_NODE;
  const executablePath = process.argv[4];
  if (executablePath) { delete env.REPORTDESK_TEST; env.LOCALAPPDATA = data; }
  desktop = await electron.launch(executablePath ? { executablePath: path.resolve(executablePath), env } : { args: [path.join(root, 'src/ReportDesk.Desktop')], env });
  const page = await desktop.firstWindow(); page.setDefaultTimeout(15000);
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
  await desktop.evaluate(({ dialog }, file) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [file] });
    dialog.showMessageBox = async () => ({ response: 1 });
  }, file);
  await page.click('#import-file');
  await page.waitForFunction(() => document.querySelector('#notice').open);
  await page.click('[data-close="notice"]');
  await page.locator('.report-item').first().click();
  if (await page.locator('#query-conditions-view').isVisible()) await page.click('#conditions-dismiss');
  await page.click('#sql');
  await page.waitForFunction(() => document.querySelector('#sql-editor').open && !document.querySelector('#sql-editor-input').disabled);
  const oldSql = await page.locator('#sql-editor-input').inputValue();
  const orderBy = /^(\s*)(--\s*)?(order\s+by\s+SORT_ID)\s*$/im;
  const draft = orderBy.test(oldSql)
    ? oldSql.replace(orderBy, (_all, space, comment, clause) => space + (comment ? '' : '--') + clause)
    : oldSql + '\n-- REPORTDESK_REAL_FILE_SAVE_CHECK';
  const readSources = bytes => page.evaluate(xml => {
    const doc = new DOMParser().parseFromString(xml, 'application/xml');
    if (doc.querySelector('parsererror')) throw new Error('Test could not parse saved XML');
    return [...doc.documentElement.querySelectorAll(':scope > QueryDataSource > QueryDataSource')].map(n => ({
      name: n.querySelector('Name')?.textContent, sql: n.querySelector('Sql')?.textContent
    }));
  }, bytes.toString('utf8'));
  const sourcesBefore = await readSources(before);
  const selectedSource = Number(await page.locator('#sql-editor-source').inputValue());
  await page.locator('#sql-editor-input').fill(draft);
  await page.click('#sql-editor-save');
  await page.waitForFunction(() => !busy);
  const status = await page.locator('#sql-editor-status').innerText();
  console.log('Save status:', status);
  assert.match(status, /回读核对/);
  assert.ok(status.includes(file), 'Status must identify the file actually saved');
  assert.notEqual(hash(fs.readFileSync(file)), hash(before), 'Save must change the imported file on disk');
  assert.equal(fs.readdirSync(source).filter(n => n.endsWith('.bak') || n.endsWith('.tmp')).length, 0);
  const sourcesAfter = await readSources(fs.readFileSync(file));
  assert.equal(sourcesAfter[selectedSource].sql, draft);
  sourcesBefore.forEach((s, i) => { if (i !== selectedSource) assert.deepEqual(sourcesAfter[i], s, 'Other source SQL must remain unchanged'); });
  await page.click('#sql-editor-close'); await page.click('#sql');
  assert.equal(await page.locator('#sql-editor-input').inputValue(), draft);
  // Save a second edit back to the original SQL; tests both commenting and uncommenting ORDER BY.
  await page.locator('#sql-editor-input').fill(oldSql);
  await page.click('#sql-editor-save'); await page.waitForFunction(() => !busy);
  assert.equal((await readSources(fs.readFileSync(file)))[selectedSource].sql, oldSql);
  assert.equal(fs.readdirSync(source).filter(n => n.endsWith('.bak') || n.endsWith('.tmp')).length, 0);
  assert.deepEqual(fs.readFileSync(original), before, 'Original test repository must remain unchanged');
  assert.deepEqual(errors, []);
  await page.screenshot({ path: path.join(run, 'saved.png') });
  fs.writeFileSync(path.join(run, 'PASS.txt'), 'PASS real Electron IPC, two overwrites, no backups or leftover temps, disk SQL verification, other data sources unchanged, reopen persisted SQL, original repository unchanged.\n');
  console.log('PASS ' + run);
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(async () => { if (desktop) await desktop.close(); });
