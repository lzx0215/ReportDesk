'use strict';
// Local page assembly and protocol fixtures only. Download fixtures use a local HTTP server;
// no Oracle, external network, production settings, or result-row fixture is used.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const web = path.resolve(__dirname, '..');
const desktop = path.resolve(web, '../../ReportDesk.Desktop');
const { chromium } = require(path.join(desktop, 'node_modules/playwright'));
const cachedBrowser = path.join(process.env.LOCALAPPDATA, 'ms-playwright/chromium-1217/chrome-win64/chrome.exe');
let origin, browser, server;
async function scenario(route, admin, prefix = '', contextDetail = null) {
  const context = await browser.newContext();
  const calls = [], errors = [], jobs = new Map(); let conflict = true, jobIndex = 0, catalogVersion = 'initial', listFixture = [];
  await context.route('**/*', async routeHandler => {
    const request = routeHandler.request(), url = new URL(request.url());
    if (url.origin !== origin) { errors.push('External request rejected'); return routeHandler.abort(); }
    const local = url.pathname.slice(prefix.length);
    const json = data => routeHandler.fulfill({ contentType: 'application/json', body: JSON.stringify(data) });
    if (local === '/api/session') return json({ csrf: 'local-test-csrf', admin, offline: true, serverReportsEnabled: true, idleMinutes: 17, reportsDirectory: admin ? 'C:\\LIB' : null, syncReady: false, syncSeconds: 30 });
    if (local === '/api/tab-lifecycle') return json({ ok:true, data:{alive:true,local:false,busy:false} });
    if (local === '/api/catalog-status') return json({ version: catalogVersion });
    if (local.startsWith('/api/tasks/')) return json({ state: 'succeeded', data: jobs.get(local.split('/').at(-1)) });
    if (local.startsWith('/api/downloads/sql/')) return routeHandler.continue();
    if (local.startsWith('/api/')) {
      assert.equal(request.method(), 'POST');
      assert.equal(request.headers()['x-reportdesk-csrf'], 'local-test-csrf');
      const body = request.postDataJSON(), method = local.split('/').at(-1); calls.push({ method, body });
      let data = {};
      if (method === 'bootstrap') data = { reports: [{ id: 'ui-metadata', name: 'UI 元数据协议检查', status: '协议检查' }], offline: true, warnings: [] };
      if (method === 'select') data = { id: 'ui-metadata', name: 'UI 元数据协议检查', status: '协议检查', source: 0, sources: [], parameters: [], issues: [], selectedIssues: [], definitionHash: 'SELECT-HASH' };
      if (method === 'definition') data = { title: 'UI 元数据协议检查', textParameterNames: [] };
      if (contextDetail && method === 'bootstrap') data = { reports: [{ id: contextDetail.id, name: contextDetail.name, status: contextDetail.status }], offline: true, warnings: [] };
      if (contextDetail && method === 'select') data = contextDetail;
      if (method === 'lookup') data = { resultId: 'empty-ui-options', offset: 0, total: 0, count: 0, rows: [], columns: [] };
      if (method === 'relatedFiles') {
        if (local === '/api/admin/relatedFiles') data = { root: 'approved-root', query: 'query.xml', files: [{ Role: '主表', Status: 'Matched', Reference: 'layout.xml', Paths: ['layout.xml'] }], warnings: [] };
        else data = { title: 'UI 元数据协议检查', summaryOnly: true, files: [{ Role: '主表', Status: 'Matched', count: 2 }], warnings: [] };
      }
      if (method === 'list') data = listFixture;
      if (method === 'discoverTns') data = [];
      if (method === 'exportSql') {
        assert.equal(local, '/api/admin/exportSql');
        if (body.args.reportId === 'failure') return json({ ok: false, message: '导出失败，请重试。' });
        const selected = body.args.reportIds;
        data = { downloadUrl: prefix + '/api/downloads/sql/' + (selected ? (selected.length === 1 ? selected[0] : 'all') : (body.args.reportId || 'all')) };
      }
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
      const name = path.basename(local), file = ['transport.js', 'web.js', 'web.css', 'xml-editor.js', 'local-directory.js'].includes(name) ? path.join(web, name) : path.join(desktop, 'ui', name);
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
    if (contextDetail) {
      await checkContextPicker(page, calls, contextDetail);
      assert.deepEqual(errors, []); await context.close(); return;
    }
    assert.match(await page.locator('footer').innerText(), /空闲 17 分钟后释放.*执行中不清理.*IIS/);
    assert.equal(await page.locator('link[rel="icon"]').getAttribute('href'), prefix + '/assets/close-emblem.svg');
    await page.locator('#help').click(); assert.match(await page.locator('#notice-body').innerText(), /IIS 回收/); await page.locator('[data-close="notice"]').click();
    await page.locator('#metadata-open').click(); await page.locator('#related-xml').click(); await page.waitForFunction(() => !busy);
    const relatedText = await page.locator('#notice-body').innerText();
    if (admin) { assert.match(relatedText, /layout\.xml/); assert.match(relatedText, /approved-root/); }
    else { assert.match(relatedText, /关联文件数量：2/); assert.doesNotMatch(relatedText, /undefined|layout\.xml|approved-root/); }
    await page.locator('[data-close="notice"]').click(); await page.locator('[data-close="metadata"]').click();
    // Pauses between keystrokes must not disable/blur search or switch the active report.
    const search = page.locator('#report-search'), beforeSearch = calls.length;
    const selectedBeforeSearch = await page.evaluate(() => selected);
    await search.click();
    await page.keyboard.type('slow-search', { delay: 400 });
    await page.waitForTimeout(400);
    assert.equal(await search.inputValue(), 'slow-search');
    assert.equal(await search.evaluate(node => node === document.activeElement && !node.disabled), true);
    assert.equal(await page.evaluate(() => selected), selectedBeforeSearch);
    assert.equal(calls.length, beforeSearch, 'search is local and does not select/clear/query');
    assert.match(await page.locator('#report-list').innerText(), /没有匹配的报表/);
    await search.press('Home'); await search.press('ArrowRight'); await page.keyboard.type('X');
    await page.waitForTimeout(1200); await page.keyboard.type('Y'); await page.waitForTimeout(350);
    assert.equal(await search.inputValue(), 'sXYlow-search');
    assert.deepEqual(await search.evaluate(node => [node === document.activeElement, node.selectionStart, node.selectionEnd]), [true, 3, 3]);
    await search.fill(''); await page.waitForTimeout(350);
    assert.equal(await page.locator('#report-list .report-item').count(), 1);
    // IME composition lasts longer than the debounce period and only filters on commit.
    await search.dispatchEvent('compositionstart');
    await search.evaluate(node => { node.value = 'zhongwen'; node.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true })); });
    await page.waitForTimeout(500);
    assert.equal(await page.locator('#report-list .report-item').count(), 1);
    assert.equal(await search.evaluate(node => node === document.activeElement && !node.disabled), true);
    await search.evaluate(node => { node.value = '中文'; node.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true, data: '中文' })); });
    await page.waitForTimeout(350);
    assert.match(await page.locator('#report-list').innerText(), /没有匹配的报表/);
    assert.equal(calls.length, beforeSearch);
    await search.fill('UI'); await page.waitForTimeout(350);
    assert.equal(await page.locator('#report-list .report-item').count(), 1);
    // Directory polling must defer while search owns focus, including during IME input.
    catalogVersion = 'search-update';
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await page.waitForTimeout(350);
    assert.equal(calls.length, beforeSearch);
    assert.equal(await search.evaluate(node => node === document.activeElement && !node.disabled), true);
    await search.fill(''); await page.waitForTimeout(350);
    // A changed server catalog refreshes only the list; dialogs/drafts defer the refresh.
    if (admin) assert.match(await page.locator('#web-report-source').innerText(), /C:\\LIB.*自动下载尚未就绪/);
    const beforeUpdate = calls.length;
    await page.locator('#metadata-open').click();
    catalogVersion = 'updated';
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await page.waitForTimeout(100);
    assert.equal(calls.slice(beforeUpdate).some(item => item.method === 'list'), false);
    await page.locator('[data-close="metadata"]').click();
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await page.waitForFunction(() => document.querySelector('#web-catalog-update')?.textContent.includes('当前条件和查询结果已保留') && !busy);
    assert.equal(calls.slice(beforeUpdate).filter(item => item.method === 'list').length, 1);
    assert.equal(calls.slice(beforeUpdate).some(item => ['select', 'query', 'lookup', 'clear'].includes(item.method)), false);
    if (!admin) {
      assert.equal(await page.locator('#settings-open').isVisible(), false);
      assert.equal(await page.locator('#web-sql-export-open').count(), 0);
      assert.equal(await page.locator('#sql-editor').count(), 0);
      const before = calls.length;
      assert.equal(await page.evaluate(async () => (await window.reportDesk.call('sqlEditorOpen')).ok), false);
      assert.equal(await page.evaluate(async () => (await window.reportDesk.call('exportSql', { reportId: '' })).ok), false);
      assert.equal(calls.length, before);
      assert.equal(await page.evaluate(() => { try { document.querySelector('#connection').showModal(); return true; } catch { return false; } }), false);
    } else {
      listFixture = [{ id: 'one', name: '同名报表', status: '协议检查' }, { id: 'two', name: '同名报表', status: '协议检查' }, { id: 'special', name: "特殊报表 'A' # 100%", status: '协议检查' }, { id: 'failure', name: '失败检查', status: '协议检查' }, { id: 'demo', name: '同名演示', demo: true }];
      await page.locator('#web-sql-export-open').click();
      await page.locator('#web-sql-export[open]').waitFor(); await page.waitForFunction(() => !busy);
      const exportSearch = page.locator('#web-sql-export-search');
      assert.equal(await exportSearch.evaluate(node => node === document.activeElement), true);
      assert.equal(await page.locator('#web-sql-export-selected').isDisabled(), true);
      await exportSearch.dispatchEvent('compositionstart');
      await exportSearch.evaluate(node => { node.value = 'tongming'; node.dispatchEvent(new InputEvent('input', { bubbles: true, isComposing: true })); });
      assert.equal(await page.locator('.web-sql-export-row').count(), 4);
      await exportSearch.evaluate(node => { node.value = '同名'; node.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true, data: '同名' })); });
      assert.equal(await page.locator('.web-sql-export-row').count(), 2);
      await exportSearch.fill('同名');
      assert.equal(await page.locator('.web-sql-export-row').count(), 2);
      assert.match(await page.locator('.web-sql-export-row').nth(0).innerText(), /报表 ID：one/);
      assert.match(await page.locator('.web-sql-export-row').nth(1).innerText(), /报表 ID：two/);
      await page.getByRole('checkbox', { name: '选择 同名报表，报表 ID two' }).focus();
      await page.keyboard.press('Space');
      assert.equal(await page.getByRole('checkbox', { name: '选择 同名报表，报表 ID two' }).isChecked(), true);
      assert.match(await page.locator('#web-sql-export-selection').innerText(), /已选 1 张/);
      await exportSearch.fill('特殊');
      await page.getByRole('checkbox', { name: "选择 特殊报表 'A' # 100%，报表 ID special" }).check();
      assert.match(await page.locator('#web-sql-export-selection').innerText(), /已选 2 张/);
      await exportSearch.fill('同名');
      assert.equal(await page.getByRole('checkbox', { name: '选择 同名报表，报表 ID two' }).isChecked(), true);
      const selectedDownload = page.waitForEvent('download');
      await page.locator('#web-sql-export-selected').click();
      assert.equal((await selectedDownload).suggestedFilename(), 'ReportDesk-SQL.txt'); await page.waitForFunction(() => !busy);
      assert.deepEqual(calls.filter(item => item.method === 'exportSql').at(-1).body.args, { reportIds: ['two', 'special'] });
      await page.locator('#web-sql-export-select-visible').click();
      assert.match(await page.locator('#web-sql-export-selection').innerText(), /已选 3 张/);
      await page.locator('#web-sql-export-clear-selection').click();
      assert.equal(await page.locator('#web-sql-export-selected').isDisabled(), true);
      await page.getByRole('checkbox', { name: '选择 同名报表，报表 ID two' }).check();
      const oneSelectedDownload = page.waitForEvent('download');
      await page.locator('#web-sql-export-selected').click();
      assert.equal((await oneSelectedDownload).suggestedFilename(), '同名报表.txt'); await page.waitForFunction(() => !busy);
      assert.deepEqual(calls.filter(item => item.method === 'exportSql').at(-1).body.args, { reportIds: ['two'] });
      await page.locator('#web-sql-export-clear-selection').click();
      const singleDownload = page.waitForEvent('download');
      await page.getByRole('button', { name: '导出 同名报表，报表 ID two', exact: true }).click();
      assert.equal((await singleDownload).suggestedFilename(), '同名报表.txt'); await page.waitForFunction(() => !busy);
      assert.equal(calls.filter(item => item.method === 'exportSql').at(-1).body.args.reportId, 'two');
      await exportSearch.fill('特殊');
      const specialDownload = page.waitForEvent('download');
      await page.getByRole('button', { name: "导出 特殊报表 'A' # 100%，报表 ID special", exact: true }).click();
      assert.equal((await specialDownload).suggestedFilename(), "特殊报表 'A' # 100%.txt"); await page.waitForFunction(() => !busy);
      assert.equal(calls.filter(item => item.method === 'exportSql').at(-1).body.args.reportId, 'special');
      await exportSearch.fill('不存在'); assert.match(await page.locator('#web-sql-export-status').innerText(), /没有匹配/);
      const allDownload = page.waitForEvent('download'); await page.locator('#web-sql-export-all').click();
      assert.equal((await allDownload).suggestedFilename(), 'ReportDesk-SQL.txt'); await page.waitForFunction(() => !busy);
      assert.equal(calls.filter(item => item.method === 'exportSql').at(-1).body.args.reportId, '');
      await exportSearch.fill('失败'); await page.locator('.web-sql-export-row button').click();
      await page.waitForFunction(() => !busy); assert.match(await page.locator('#notice-body').innerText(), /导出失败/);
      await page.locator('[data-close="notice"]').click();
      assert.match(await page.locator('#web-sql-export-status').innerText(), /导出失败/);
      await page.getByRole('button', { name: '关闭', exact: true }).last().click();
      await page.locator('#web-sql-export-open').click(); await page.locator('#web-sql-export[open]').waitFor(); await page.waitForFunction(() => !busy);
      assert.match(await page.locator('#web-sql-export-selection').innerText(), /已选 0 张/);
      assert.equal(await page.locator('#web-sql-export-selected').isDisabled(), true);
      await page.getByRole('button', { name: '关闭', exact: true }).last().click();
      listFixture = [];
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
      await page.locator('#web-settings-close').click();
      await page.locator('#web-import-open').click(); await page.locator('#source-tab-local').click();
      assert.equal(await page.evaluate(() => window.reportDeskLocal.supported()), true);
      assert.equal(await page.locator('#local-directory-pick').isEnabled(), true, 'local controls created during bootstrap must become enabled when report sources opens');
      assert.equal(await page.locator('#local-directory-backup-pick').isEnabled(), true);
      assert.equal(await page.locator('#local-directory-use').isEnabled(), false, 'a directory must be selected before loading');
      await page.locator('#source-close').click();
      await page.locator('#settings-open').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#tns-pick').isVisible(), false);
      assert.equal(await page.locator('#conn-remember').isChecked(), false);
      await page.locator('#conn-password').fill('UI-ONLY-NOT-A-REAL-PASSWORD');
      await page.locator('#connection-form button[type=submit]').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#conn-password').inputValue(), '');
      assert.deepEqual(await page.evaluate(() => [localStorage.length, sessionStorage.length]), [0, 0]);
      await page.locator('#web-settings-close').click();
      await page.locator('#logs').click(); await page.waitForFunction(() => !busy);
      assert.equal(await page.locator('#notice-body').innerText(), '仅页面协议检查');
    }
    const tabs = new Set(calls.map(item => item.body.tabId)); assert.equal(tabs.size, 1);
  }
  assert.deepEqual(errors, []); await context.close();
}
async function checkContextPicker(page, calls, detail) {
  const current = detail.parameters.find(p => p.contextDepartment);
  assert.ok(current); assert.equal(current.kind, 'ComboBoxType');
  await page.locator('#summary-edit').click();
  assert.equal(await page.locator('#parameters select').first().getAttribute('data-parameter'), current.name);
  const lookupCount = () => calls.filter(c => c.method === 'lookup').length;
  const beforeMissing = lookupCount();
  await page.getByRole('button', { name: '加载选项：科室', exact: true }).click();
  await page.waitForFunction(() => !busy);
  assert.match(await page.locator('#notice-body').innerText(), /请先选择或填写.*CurrentDeptID/);
  assert.equal(lookupCount(), beforeMissing);
  await page.locator('[data-close="notice"]').click();
  await page.getByRole('button', { name: '加载选项：' + current.label, exact: true }).click();
  await page.locator('#lookup').waitFor({ state: 'visible' });
  assert.equal(calls.filter(c => c.method === 'lookup').at(-1).body.args.parameter, current.name);
  await page.locator('[data-close="lookup"]').click();
  // Exercise user selection state only: no department dictionary rows or Oracle results are fabricated.
  const choose = async (name, value, label) => page.evaluate(({ name, value, label }) => {
    const input = fields.get(name).input, option = new Option(label, value);
    option.dataset.choice = 'true'; option.dataset.label = label; input.add(option); input.selectedIndex = input.options.length - 1;
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }, { name, value, label });
  await choose(current.name, 'UI-USER-A', '界面选择甲');
  assert.match(await page.locator('#query-summary').innerText(), /当前科室.*界面选择甲/);
  await page.getByRole('button', { name: '加载选项：科室', exact: true }).click();
  await page.locator('#lookup').waitFor({ state: 'visible' });
  const request = calls.filter(c => c.method === 'lookup').at(-1).body.args;
  assert.equal(request.parameter, 'dtDeptId'); assert.equal(request.values[current.name], 'UI-USER-A');
  await page.locator('[data-close="lookup"]').click();
  await choose('dtDeptId', 'UI-TARGET-A', '界面科室选项甲');
  await choose('deptid', 'UI-ACCOUNT-A', '界面核算选项甲');
  await choose(current.name, 'UI-USER-B', '界面选择乙');
  const state = await page.evaluate(() => ({ values: values(), options: ['dtDeptId', 'deptid'].flatMap(n => [...fields.get(n).input.options].map(o => o.value)) }));
  assert.equal(state.values[current.name], 'UI-USER-B');
  assert.equal(state.values.dtDeptId, null); assert.equal(state.values.deptid, null);
  assert.equal(state.options.some(v => v === 'UI-TARGET-A' || v === 'UI-ACCOUNT-A'), false);
  assert.equal(calls.some(c => c.method === 'query'), false);
  console.log('PASS: real report context metadata; missing dependency guidance; independent department picker; selected code forwarding; dependent values/options invalidated; no query executed.');
}
(async () => {
  server = http.createServer((request, response) => {
    const filenames = { two: '同名报表.txt', special: "特殊报表 'A' # 100%.txt", all: 'ReportDesk-SQL.txt' };
    const match = request.url.match(/^\/(?:desk\/)?api\/downloads\/sql\/(two|special|all)$/);
    if (!match) { response.writeHead(404); response.end(); return; }
    const encoded = encodeURIComponent(filenames[match[1]]).replace(/[!'()*]/g, char => '%' + char.charCodeAt(0).toString(16).toUpperCase());
    response.writeHead(200, { 'content-type': 'text/plain', 'content-disposition': `attachment; filename="ReportDesk-SQL.txt"; filename*=UTF-8''${encoded}` });
    response.end('protocol fixture');
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch({ headless: true, ...(fs.existsSync(cachedBrowser) ? { executablePath: cachedBrowser } : {}), args: ['--disable-background-networking'] });
  await scenario('/', false); await scenario('/', true); await scenario('/admin', false); await scenario('/admin', true); await scenario('/admin/', true, '/desk');
  if (process.env.REPORTDESK_CONTEXT_METADATA) {
    const detail = JSON.parse(fs.readFileSync(process.env.REPORTDESK_CONTEXT_METADATA, 'utf8'));
    await scenario('/', false, '', detail); await scenario('/admin/', true, '/desk', detail);
  }
  console.log('PASS: local Chromium public/admin/denied/subdirectory pages; SQL download names with Chinese and special characters; slow search/pause/caret/IME/clear/local-only filtering; polling defers during search; jobs for all POSTs; hidden/rejected maintenance; no public editor; idle help; sync conflict confirm/cancel; registered TNS; password clearing/storage; logs. No external network or Oracle.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => { await browser?.close(); server?.close(); });
