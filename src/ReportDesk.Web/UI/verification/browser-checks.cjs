'use strict';
// Local page assembly and protocol fixtures only. Every request is intercepted;
// no Oracle, external network, production settings, or result-row fixture is used.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const web = path.resolve(__dirname, '..');
const desktop = path.resolve(web, '../../ReportDesk.Desktop');
const { chromium } = require(path.join(desktop, 'node_modules/playwright'));
const cachedBrowser = path.join(process.env.LOCALAPPDATA, 'ms-playwright/chromium-1217/chrome-win64/chrome.exe');
const origin = 'http://127.0.0.1:49179';
let browser;
async function scenario(route, admin, prefix = '') {
  const context = await browser.newContext();
  const calls = [], errors = [], jobs = new Map(); let conflict = true, jobIndex = 0;
  await context.route('**/*', async routeHandler => {
    const request = routeHandler.request(), url = new URL(request.url());
    if (url.origin !== origin) { errors.push('External request rejected'); return routeHandler.abort(); }
    const local = url.pathname.slice(prefix.length);
    const json = data => routeHandler.fulfill({ contentType: 'application/json', body: JSON.stringify(data) });
    if (local === '/api/session') return json({ csrf: 'local-test-csrf', admin, offline: true, idleMinutes: 17 });
    if (local.startsWith('/api/tasks/')) return json({ state: 'succeeded', data: jobs.get(local.split('/').at(-1)) });
    if (local.startsWith('/api/')) {
      assert.equal(request.method(), 'POST');
      assert.equal(request.headers()['x-reportdesk-csrf'], 'local-test-csrf');
      const body = request.postDataJSON(), method = local.split('/').at(-1); calls.push({ method, body });
      let data = {};
      if (method === 'bootstrap') data = { reports: [{ id: 'ui-metadata', name: 'UI 元数据协议检查', status: '协议检查' }], offline: true, warnings: [] };
      if (method === 'select') data = { id: 'ui-metadata', name: 'UI 元数据协议检查', status: '协议检查', source: 0, sources: [], parameters: [], issues: [], selectedIssues: [], definitionHash: 'SELECT-HASH' };
      if (method === 'definition') data = { title: 'UI 元数据协议检查', textParameterNames: [] };
      if (method === 'relatedFiles') {
        if (local === '/api/admin/relatedFiles') data = { root: 'approved-root', query: 'query.xml', files: [{ Role: '主表', Status: 'Matched', Reference: 'layout.xml', Paths: ['layout.xml'] }], warnings: [] };
        else data = { title: 'UI 元数据协议检查', summaryOnly: true, files: [{ Role: '主表', Status: 'Matched', count: 2 }], warnings: [] };
      }
      if (method === 'list' || method === 'discoverTns') data = [];
      if (method === 'browseReports') data = { path: '', parent: '', folders: [], files: [] };
      if (method === 'uploadImport') data = { path: 'picked.xml' };
      if (method === 'checkNewReports') data = { reports: [], sourceCount: 2, addedCount: 3, visibleAddedCount: 3, incomplete: 1, errors: ['协议检查警告'] };
      if (method === 'settings' || method === 'saveSettings') data = { name: 'Offline UI', host: '', port: 1521, service: '', username: '', mode: 0, remember: false, hasPassword: false };
      if (method === 'syncStatus') data = { enabled: false, lastSuccess: null, message: '本地契约检查，未访问 HIS', conflicts: conflict ? [
        { releaseId: 42, code: 'LocalConflict', message: '测试冲突标识', fingerprint: 'revision-one', canResolve: true },
        { releaseId: 43, code: 'RecoveryRequired', message: '需先恢复', fingerprint: 'revision-two', canResolve: false },
        { releaseId: 44, code: 'LocalConflict', message: '未给处理许可', fingerprint: 'revision-three' },
        { releaseId: 45, code: 'LocalConflict', message: '无效处理许可', fingerprint: 'revision-four', canResolve: 'true' },
        { releaseId: 46, code: 'LocalConflict', message: '缺少指纹', canResolve: true }
      ] : [] };
      if (method === 'resolveSyncConflict') conflict = false;
      if (method === 'openLogs' || method === 'sqlEditorReveal') data = { title: '本地说明', text: '仅页面协议检查' };
      const id = String(++jobIndex); jobs.set(id, data); return json({ ok: true, jobId: id });
    }
    if (local === '/' || local === '/admin' || local === '/admin/') return routeHandler.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(web, 'index.html'), 'utf8').replaceAll('"/assets/', '"' + prefix + '/assets/') });
    if (local.startsWith('/assets/')) {
      const name = path.basename(local), file = ['transport.js', 'web.js', 'web.css'].includes(name) ? path.join(web, name) : path.join(desktop, 'ui', name);
      if (fs.existsSync(file)) return routeHandler.fulfill({ contentType: name.endsWith('.css') ? 'text/css' : name.endsWith('.svg') ? 'image/svg+xml' : 'text/javascript', body: fs.readFileSync(file) });
    }
    return routeHandler.fulfill({ status: 404, body: '' });
  });
  const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin + prefix + route);
  if (route.includes('admin') && !admin) {
    await page.locator('.web-access-error').waitFor();
    assert.equal(calls.length, 0); assert.match(await page.locator('.web-access-error').innerText(), /未获授权/);
  } else {
    await page.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。');
    await page.waitForFunction(() => document.title.startsWith('ReportDesk Web'));
    assert.match(await page.locator('footer').innerText(), /空闲 17 分钟后释放.*执行中不清理.*IIS/);
    assert.equal(await page.locator('link[rel="icon"]').getAttribute('href'), prefix + '/assets/close-emblem.svg');
    await page.locator('#help').click(); assert.match(await page.locator('#notice-body').innerText(), /IIS 回收/); await page.locator('[data-close="notice"]').click();
    await page.locator('#metadata-open').click(); await page.locator('#related-xml').click(); await page.waitForFunction(() => !busy);
    const relatedText = await page.locator('#notice-body').innerText();
    if (admin) { assert.match(relatedText, /layout\.xml/); assert.match(relatedText, /approved-root/); }
    else { assert.match(relatedText, /关联文件数量：2/); assert.doesNotMatch(relatedText, /undefined|layout\.xml|approved-root/); }
    await page.locator('[data-close="notice"]').click(); await page.locator('[data-close="metadata"]').click();
    if (!admin) {
      assert.equal(await page.locator('#settings-open').isVisible(), false);
      assert.equal(await page.locator('#sql-editor').count(), 0);
      const before = calls.length;
      assert.equal(await page.evaluate(async () => (await window.reportDesk.call('sqlEditorOpen')).ok), false);
      assert.equal(calls.length, before);
      assert.equal(await page.evaluate(() => { try { document.querySelector('#connection').showModal(); return true; } catch { return false; } }), false);
    } else {
      await page.locator('#check-new').click(); await page.waitForFunction(() => !busy);
      assert.match(await page.locator('#notice-body').innerText(), /已检查 2 个导入来源，新增 3 张报表/);
      assert.match(await page.locator('#notice-body').innerText(), /不完整定义 1 份.*警告\/失败 1 项/);
      assert.match(await page.locator('#notice-body').innerText(), /协议检查警告/);
      await page.locator('[data-close="notice"]').click();
      await page.locator('#web-sync-open').click(); await page.locator('.web-conflict').first().waitFor();
      assert.equal(await page.locator('.web-conflict').count(), 5);
      assert.equal(await page.locator('.web-conflict button').count(), 2);
      for (let index = 1; index < 5; index++) assert.equal(await page.locator('.web-conflict').nth(index).locator('button').count(), 0);
      page.once('dialog', dialog => dialog.dismiss()); await page.getByRole('button', { name: '接受 HIS 版本', exact: true }).click();
      await page.waitForFunction(() => !busy); assert.equal(calls.some(item => item.method === 'resolveSyncConflict'), false);
      const acceptHis = prefix !== '';
      page.once('dialog', dialog => dialog.accept()); await page.getByRole('button', { name: acceptHis ? '接受 HIS 版本' : '暂保留本地', exact: true }).click();
      await page.waitForFunction(() => !busy); assert.equal(await page.locator('.web-conflict').count(), 0);
      const decision = calls.find(item => item.method === 'resolveSyncConflict').body.args;
      assert.deepEqual(decision, { releaseId: 42, acceptHis, fingerprint: 'revision-one' });
      await page.locator('#web-sync-dialog > button').click();
      await page.locator('#settings-open').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#tns-pick').isVisible(), false);
      assert.equal(await page.locator('#conn-remember').isChecked(), false);
      await page.locator('#conn-password').fill('UI-ONLY-NOT-A-REAL-PASSWORD');
      await page.locator('#connection-form button[type=submit]').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#conn-password').inputValue(), '');
      assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]);
      await page.locator('#logs').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#notice-body').innerText(), '仅页面协议检查');
    }
    const tabs = new Set(calls.map(item => item.body.tabId)); assert.equal(tabs.size, 1);
  }
  assert.deepEqual(errors, []); await context.close();
}
(async () => {
  browser = await chromium.launch({ headless: true, ...(fs.existsSync(cachedBrowser) ? { executablePath: cachedBrowser } : {}), args: ['--disable-background-networking'] });
  await scenario('/', false); await scenario('/', true); await scenario('/admin', false); await scenario('/admin', true); await scenario('/admin/', true, '/desk');
  console.log('PASS: local Chromium public/admin/denied/subdirectory pages; jobs for all POSTs; hidden/rejected maintenance; no public editor; idle help; sync conflict confirm/cancel; registered TNS; password clearing/storage; logs. No external network or Oracle.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => { await browser?.close(); });
