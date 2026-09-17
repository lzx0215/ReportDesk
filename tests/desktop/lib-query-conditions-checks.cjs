// Real LIB metadata; isolated Electron profile. No Oracle execution.
// IPC success/error/lookup envelopes below are UI harness responses, not HIS results.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const root = path.resolve(__dirname, '../..');
const { _electron: electron } = require(path.join(root, 'src/ReportDesk.Desktop/node_modules/playwright'));
const source = path.resolve(process.env.REPORTDESK_LIB || 'E:/his/LIB');
const run = path.join(root, 'artifacts/verification/desktop', 'lib-query-modal-' + Date.now());
const executable = process.argv[2];
const baseline = process.argv[3];
const names = ['(病案)观察室工作日志', '按科室(出院)', '门诊中药处方查询', '住院各科有效收入统计', '科室口服执行单查询', '患者费用查询SQL设置', '(伽马刀、直加)工作量', '住院药品处方汇总表(按科室)', '[中联]医保费用明细'];
const sizes = [[1040, 700], [1280, 720], [1920, 1080]];
let app;
(async () => {
  const local = path.join(run, 'local');
  fs.mkdirSync(path.join(local, 'ReportDesk'), { recursive: true });
  fs.writeFileSync(path.join(local, 'ReportDesk/import-sources.json'), JSON.stringify([{ Folder: true, Path: source }]));
  const env = { ...process.env, LOCALAPPDATA: local }; delete env.ELECTRON_RUN_AS_NODE; delete env.REPORTDESK_TEST;
  app = await electron.launch(executable ? { executablePath: path.resolve(executable), env } : { args: [path.join(root, 'src/ReportDesk.Desktop')], env });
  const p = await app.firstWindow(); p.setDefaultTimeout(20000);
  await app.evaluate(({ ipcMain, BrowserWindow }) => {
    BrowserWindow.getAllWindows()[0].hide();
    global.conditionCalls = []; global.queryReply = 'success'; global.lookupFixture = [];
    const original = ipcMain._invokeHandlers.get('reportdesk:call'); ipcMain.removeHandler('reportdesk:call');
    const empty = { resultId: 'UI-only-empty-envelope', revision: 0, offset: 0, total: 0, count: 0, columns: [], rows: [] };
    ipcMain.handle('reportdesk:call', async (event, method, args) => {
      global.conditionCalls.push({ method, args });
      if (method === 'query') {
        if (global.queryReply === 'parameter') return { ok: false, message: '请填写或选择所有查询条件。\nUI-only validation envelope.' };
        if (global.queryReply === 'connection') return { ok: false, message: 'UI-only connection failure.' };
        if (global.queryReply === 'cancel') return { ok: false, cancelled: true, message: 'UI-only cancellation.' };
        if (global.queryReply === 'delay') await new Promise(resolve => setTimeout(resolve, 200));
        return { ok: true, data: empty };
      }
      if (['view', 'page'].includes(method)) return { ok: true, data: empty };
      if (['lookup', 'lookupPage'].includes(method)) return { ok: true, data: { ...empty, rows: global.lookupFixture.map(o => [o.Value, o.Label]), count: global.lookupFixture.length } };
      if (method === 'lookupAll') return { ok: true, data: global.lookupFixture.map(o => ({ value: o.Value, label: o.Label })) };
      return original(event, method, args);
    });
  });
  const errors = []; p.on('pageerror', e => errors.push(e.message));
  await p.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。', null, { timeout: 120000 });
  const discovered = await p.evaluate(async names => {
    const list = await call('list');
    const cases = names.map(name => { const r = list.find(r => r.name === name || r.path.endsWith(name + '查询设置.xml')); if (!r) throw Error('Missing real report: ' + name); return { id: r.id, name: r.name }; });
    let fixture = [];
    for (const r of cases) {
      const d = await call('select', { reportId: r.id });
      fixture.push(...d.parameters.flatMap(p => p.options || []));
      if (d.sources.filter(s => !s.issues.length).length > 1 && !cases.some(c => c.source !== undefined)) {
        const extra = d.sources.find(s => s.index !== d.source && !s.issues.length);
        cases.push({ ...r, source: extra.index });
      }
    }
    for (const r of list) {
      const d = await call('select', { reportId: r.id });
      if (d.sources.length && !d.selectedIssues.length && !d.parameters.length) { cases.push({ id: r.id, name: r.name }); break; }
    }
    return { cases, fixture: [...new Map(fixture.map(o => [o.Value, o])).values()].slice(0, 2), count: list.length };
  }, names);
  assert.equal(discovered.count, 1284); assert.equal(discovered.fixture.length, 2);
  await app.evaluate((_, fixture) => { global.lookupFixture = fixture; }, discovered.fixture);
  const counts = () => app.evaluate(() => ({ select: global.conditionCalls.filter(c => c.method === 'select').length, query: global.conditionCalls.filter(c => c.method === 'query').length }));
  const idle = () => p.waitForFunction(() => !busy);
  const coverage = new Set(), measurements = [], contracts = [], editedPayloads = [];
  for (const [index, r] of discovered.cases.entries()) {
    console.log('Checking ' + r.name + ' / ' + (r.source || 0));
    await p.evaluate(async r => { await task('Conditions verification', () => select(r.id, r.source || 0)); }, r);
    const defs = await p.evaluate(() => detail.parameters);
    for (const d of defs) { coverage.add(d.kind); for (const k of ['multiple', 'treeSelect', 'implicitValue']) if (d[k]) coverage.add(k); }
    if (r.name === '科室口服执行单查询') assert.equal(defs.length, 18);
    assert.equal(await p.locator('#report-view input[data-parameter], #report-view #source').count(), 0);
    assert.equal(await p.locator('#report-view').isVisible(), true);
    assert.equal(await p.locator('#conditions-query').isDisabled(), false);
    assert.equal(await p.locator('#query-conditions-view [data-parameter]').count(), defs.length);
    const initial = await p.evaluate(() => buildQueryPayload());
    const domValues = await p.evaluate(() => {
      const output = {};
      for (const [name, field] of fields) {
        if (field.definition.multiple) { const choices = JSON.parse(field.input.dataset.selections || '[]'); output[name] = choices.map(c => c.value); output[name + '.Text'] = choices.map(c => c.label).join(','); }
        else { output[name] = field.input.type === 'checkbox' ? (field.input.checked ? 'True' : 'False') : field.input.tagName === 'SELECT' && !field.input.selectedOptions[0]?.dataset.choice ? null : field.input.value; output[name + '.Text'] = field.input.selectedOptions?.[0]?.dataset.label ?? field.input.selectedOptions?.[0]?.textContent ?? field.input.value; }
      }
      return output;
    });
    assert.deepEqual(initial.values, domValues, 'Existing values() contract changed');
    const before = await counts();
    await p.click('#summary-edit');
    await p.evaluate(() => {
      window.retainedSession = currentReportSession;
      window.retainedNodes = [...document.querySelectorAll('#query-conditions-view input, #query-conditions-view select')];
      for (const { input, definition } of fields.values()) {
        if (definition.multiple) continue;
        if (input.type === 'checkbox') input.checked = !input.checked;
        else if (input.tagName === 'SELECT') { if (input.options.length > 1) input.selectedIndex = 1; }
        else if (input.type === 'date') input.value = '2026-09-13';
        else if (input.type === 'datetime-local') input.value = '2026-09-13T08:30:45';
        else input.value = '0';
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
    });
    const edited = await p.evaluate(() => buildQueryPayload());
    editedPayloads.push(edited);
    await p.click('#conditions-apply'); await p.click('#summary-edit');
    await p.keyboard.press('Escape'); assert.equal(await p.locator('#query-conditions-view').isVisible(), false); await p.click('#summary-edit');
    await p.click('#conditions-dismiss'); await p.click('#summary-edit');
    assert.deepEqual(await counts(), before, 'View navigation called select/query');
    assert.deepEqual(await p.evaluate(() => buildQueryPayload()), edited);
    assert.equal(await p.evaluate(() => retainedSession === currentReportSession && retainedNodes.every((n, i) => n === [...document.querySelectorAll('#query-conditions-view input, #query-conditions-view select')][i])), true);
    assert.deepEqual(await p.locator('#parameters [data-parameter]').evaluateAll(ns => ns.map(n => n.dataset.parameter)), defs.map(d => d.name));
    for (const [width, height] of sizes) {
      await app.evaluate(({ BrowserWindow }, size) => BrowserWindow.getAllWindows()[0].setSize(...size), [width, height]);
      await p.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const geometry = await p.evaluate(() => {
        const box = el => { const r = el.getBoundingClientRect(); return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, height: r.height, width: r.width }; };
        const scroll = document.querySelector('#conditions-scroll');
        const items = [...document.querySelectorAll('.query-field')].map(box);
        return { viewport: [innerWidth, innerHeight], items, action: box(document.querySelector('#conditions-query')), back: box(document.querySelector('#conditions-back')), scroll: box(scroll), scrollHeight: scroll.scrollHeight, gridColumns: getComputedStyle(document.querySelector('.query-form-grid')).gridTemplateColumns, overflow: scroll.scrollWidth > scroll.clientWidth + 1, selected: document.querySelector('.report-item.active')?.dataset.reportId };
      });
      assert.equal(geometry.overflow, false); assert.equal(geometry.selected, r.id);
      assert.ok(geometry.action.bottom <= geometry.viewport[1] && geometry.back.top >= 0);
      assert.equal(geometry.gridColumns.split(' ').length, 1);
      assert.equal(await p.evaluate(() => {
        const controls = [...document.querySelectorAll('.query-field input, .query-field select')];
        const lefts = controls.map(n => n.getBoundingClientRect().left);
        return Math.max(...lefts) - Math.min(...lefts) < 1;
      }), true, 'All control columns must align');
      for (let n = 0; n < geometry.items.length; n++) {
        const a = geometry.items[n]; assert.ok(a.width >= 250 && a.right <= geometry.viewport[0]);
        if (n > 0) assert.ok(a.top >= geometry.items[n - 1].bottom, 'One condition per row');
        for (const b of geometry.items.slice(n + 1)) assert.ok(a.right <= b.left + 1 || b.right <= a.left + 1 || a.bottom <= b.top + 1 || b.bottom <= a.top + 1, 'Field overlap');
      }
      await p.evaluate(() => { const s = document.querySelector('#conditions-scroll'); s.scrollTop = s.scrollHeight; });
      assert.equal(await p.locator('#parameters .query-field').last().count() ? await p.locator('#parameters .query-field').last().evaluate(n => n.getBoundingClientRect().bottom <= document.querySelector('#conditions-scroll').getBoundingClientRect().bottom + 1) : true, true);
      await p.evaluate(() => { document.querySelector('#conditions-scroll').scrollTop = 0; });
      await p.screenshot({ path: path.join(run, `${index}-${width}-conditions.png`) });
      await p.click('#conditions-back');
      const result = await p.locator('#table-wrap').boundingBox();
      assert.equal(await p.locator('#query-conditions-view').isVisible(), false);
      assert.equal(await p.evaluate(() => {
        const bar = document.querySelector('.query-summary-bar');
        const items = [...bar.children];
        const centers = items.map(n => { const r = n.getBoundingClientRect(); return r.top + r.height / 2; });
        const heading = document.querySelector('#result-count');
        const summary = document.querySelector('#query-summary');
        const bounds = bar.getBoundingClientRect(), summaryBounds = summary.getBoundingClientRect();
        return Math.max(...centers) - Math.min(...centers) < 1 && bar.scrollWidth <= bar.clientWidth
          && heading.parentElement === bar && heading.clientWidth >= 60
          && summaryBounds.top >= bounds.bottom && Math.abs(summaryBounds.left - bounds.left) < 1
          && Math.abs(summaryBounds.width - bounds.width) < 1 && !document.querySelector('#report-status');
      }), true, 'Result heading and actions must share one row; summary must sit below; title metadata must be absent');
      assert.ok(result.height >= 200, 'Report result area too small');
      assert.equal(await p.locator('#query-summary').evaluate(n => n.getBoundingClientRect().height <= 36), true);
      await p.screenshot({ path: path.join(run, `${index}-${width}-report.png`) });
      measurements.push({ name: r.name, source: r.source || 0, parameters: defs.length, width, height, resultHeight: result.height, conditionScroll: geometry.scrollHeight > geometry.scroll.height + 1 });
      await p.click('#summary-edit');
    }
    assert.deepEqual(await p.evaluate(() => buildQueryPayload()), edited, 'Resize changed values');
    // Real static-option labels/codes are reused solely to drive lookup UI callbacks.
    for (const d of defs.filter(d => (d.kind === 'ComboBoxType' || d.treeSelect) && d.lookup)) {
      await p.locator(`[data-parameter="${d.name}"]`).locator('..').locator('button').click(); await idle();
      await p.locator('#lookup-list button').first().click();
      if (d.multiple) {
        await p.locator('#lookup-list button').nth(1).click();
        assert.equal(await p.evaluate(name => currentReportSession.parameterValues.get(name).value.length, d.name), 2);
        await p.click('#lookup-clear'); assert.equal(await p.evaluate(name => buildQueryPayload().values[name].length, d.name), 0);
        if (d.hasAll) { await p.click('#lookup-all'); await idle(); assert.equal(await p.evaluate(name => buildQueryPayload().values[name].length, d.name), 2); }
        else await p.locator('#lookup-list button').first().click();
        await p.click('[data-close=lookup]');
      }
      assert.equal(await p.evaluate(name => { const f = fields.get(name); return JSON.stringify(QueryState.capture(f.input, f.definition)) === JSON.stringify(currentReportSession.parameterValues.get(name)); }, d.name), true);
    }
    const payload = await p.evaluate(() => buildQueryPayload());
    const queryCount = (await counts()).query;
    await p.click('#conditions-query'); await idle();
    assert.equal((await counts()).query, queryCount + 1);
    assert.equal(await p.locator('#report-view').isVisible(), true);
    assert.deepEqual(await p.evaluate(() => currentReportSession.lastExecutedQuerySnapshot), payload);
    assert.equal(await p.locator('#query-dirty').isVisible(), false);
    assert.deepEqual(await app.evaluate(() => global.conditionCalls.filter(c => c.method === 'query').at(-1).args), payload);
    contracts.push({ name: r.name, source: r.source || 0, parameterNames: defs.map(d => d.name), initialContract: 'PASS', editedContract: 'PASS' });
    if (defs.length) {
      await p.click('#summary-edit');
      await p.evaluate(() => {
        window.oldResultPage = page;
        const p = detail.parameters.find(p => !p.multiple && !['ComboBoxType'].includes(p.kind) && !p.treeSelect);
        if (p) { const input = fields.get(p.name).input; window.dirtyFieldName = p.name; window.dirtyOriginal = QueryState.capture(input, p); if (input.type === 'checkbox') input.checked = !input.checked; else if (input.type === 'date') input.value = '2026-09-12'; else if (input.type === 'datetime-local') input.value = '2026-09-12T08:30:45'; else input.value = '1'; input.dispatchEvent(new Event('input')); }
      });
      await p.click('#conditions-back');
      assert.equal(await p.evaluate(() => page === oldResultPage), true);
      assert.equal(await p.locator('#query-dirty').isVisible(), true);
      await p.click('#summary-edit');
      await p.evaluate(() => {
        const input = fields.get(dirtyFieldName).input;
        window.changedValue = QueryState.capture(input, fields.get(dirtyFieldName).definition);
        if (input.type === 'checkbox') input.checked = dirtyOriginal.value === 'True'; else input.value = dirtyOriginal.value;
        input.dispatchEvent(new Event('input'));
      });
      await p.click('#conditions-back');
      assert.equal(await p.locator('#query-dirty').isVisible(), false, 'Returning to executed values must clear dirty');
      await p.click('#summary-edit');
      await p.evaluate(() => { const input = fields.get(dirtyFieldName).input; if (input.type === 'checkbox') input.checked = changedValue.value === 'True'; else input.value = changedValue.value; input.dispatchEvent(new Event('input')); });
      await p.click('#conditions-back');
      const unchangedSnapshot = await p.evaluate(() => currentReportSession.lastExecutedQuerySnapshot);
      await app.evaluate(() => { global.queryReply = 'parameter'; });
      await p.click('#query'); await idle();
      assert.equal(await p.locator('#query-conditions-view').isVisible(), true);
      assert.equal(await p.locator('#conditions-error').isVisible(), true);
      assert.deepEqual(await p.evaluate(() => currentReportSession.lastExecutedQuerySnapshot), unchangedSnapshot, 'Failure replaced snapshot');
      await p.click('[data-close=notice]'); await p.click('#conditions-back');
      for (const reply of ['cancel', 'connection']) {
        await app.evaluate((_, reply) => { global.queryReply = reply; }, reply);
        await p.click('#query'); await idle();
        assert.equal(await p.locator('#report-view').isVisible(), true);
        assert.deepEqual(await p.evaluate(() => currentReportSession.lastExecutedQuerySnapshot), unchangedSnapshot);
        if (reply === 'connection') await p.click('[data-close=notice]');
      }
      await app.evaluate(() => { global.queryReply = 'success'; });
      await p.click('#query'); await idle();
      assert.equal(await p.locator('#query-dirty').isVisible(), false);
    }
    // Display-only metadata/labels never dirty the snapshot; real SQL .Text aliases do.
    assert.equal(await p.evaluate(() => {
      const snapshot = buildQueryPayload();
      const displayOnly = structuredClone(snapshot);
      for (const name of Object.keys(displayOnly.values).filter(n => n.endsWith('.Text') && !currentReportSession.textParameters.includes(n.slice(0, -5).toLowerCase()))) displayOnly.values[name] = 'UI-only alternate label';
      return QueryState.key(snapshot, currentReportSession) === QueryState.key(displayOnly, currentReportSession);
    }), true);
    // A full editor remount must hydrate from the session, not from initial metadata.
    assert.equal(await p.evaluate(() => {
      const before = JSON.stringify(buildQueryPayload()); queryFields.mount(currentReportSession);
      return before === JSON.stringify(buildQueryPayload()) && [...fields].every(([name, f]) => JSON.stringify(QueryState.capture(f.input, f.definition)) === JSON.stringify(currentReportSession.parameterValues.get(name)));
    }), true);
    await p.click('#summary-edit');
    const other = await p.evaluate(() => detail.sources.find(s => s.index !== detail.source && !s.issues.length)?.index);
    if (other !== undefined) {
      await p.selectOption('#source', String(other)); await idle();
      assert.equal(await p.locator('#query-conditions-view').isVisible(), true);
      assert.equal(await p.evaluate(() => page === null && currentReportSession.source === detail.source && currentReportSession.lastExecutedQuerySnapshot === null), true);
    }
    const next = discovered.cases[(index + 1) % discovered.cases.length];
    await p.click('#conditions-back');
    await p.locator(`[data-report-id="${next.id}"]`).click(); await idle();
    assert.equal(await p.locator('#report-view').isVisible(), true);
    assert.equal(await p.evaluate(id => currentReportSession.reportId === id && currentReportSession.details === detail && currentReportSession.lastExecutedQuerySnapshot === null, next.id), true);
  }
  for (const type of ['TextBoxType', 'DateTimeType', 'ComboBoxType', 'CheckBoxType', 'RegisterIdType', 'multiple', 'treeSelect', 'implicitValue']) assert.ok(coverage.has(type), type);
  assert.equal(await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length), 1);
  assert.deepEqual(errors, []);
  // Delayed execution verifies both action paths cannot submit twice while busy.
  await app.evaluate(() => { global.queryReply = 'delay'; });
  const beforeDouble = (await counts()).query;
  await p.evaluate(() => { executeQuery(); executeQuery(); }); await idle();
  assert.equal((await counts()).query, beforeDouble + 1);
  if (baseline) {
    await app.close(); app = null;
    const baselineLocal = path.join(run, 'baseline-local'); fs.mkdirSync(path.join(baselineLocal, 'ReportDesk'), { recursive: true });
    fs.writeFileSync(path.join(baselineLocal, 'ReportDesk/import-sources.json'), JSON.stringify([{ Folder: true, Path: source }]));
    app = await electron.launch({ executablePath: path.resolve(baseline), env: { ...env, LOCALAPPDATA: baselineLocal } });
    const bp = await app.firstWindow();
    await bp.waitForFunction(() => document.querySelector('#operation-status').textContent === '准备就绪。', null, { timeout: 120000 });
    for (const [i, r] of discovered.cases.entries()) {
      const payload = await bp.evaluate(async r => {
        await task('Baseline contract verification', () => select(r.id, r.source || 0));
        for (const { input, definition } of fields.values()) {
          if (definition.multiple) continue;
          if (input.type === 'checkbox') input.checked = !input.checked;
          else if (input.tagName === 'SELECT') { if (input.options.length > 1) input.selectedIndex = 1; }
          else if (input.type === 'date') input.value = '2026-09-13';
          else if (input.type === 'datetime-local') input.value = '2026-09-13T08:30:45';
          else input.value = '0';
          input.dispatchEvent(new Event('input', { bubbles: true }));
        }
        return { reportId: selected, source: Number(document.querySelector('#source').value), values: values() };
      }, r);
      assert.deepEqual(editedPayloads[i], payload, 'Baseline IPC values changed: ' + r.name);
    }
  }
  fs.writeFileSync(path.join(run, 'results.json'), JSON.stringify({ reports: discovered.cases, measurements, contracts, coverage: [...coverage], baselineContracts: baseline ? 'PASS actual baseline client parameter values match' : 'NOT RUN', checks: 'PASS session/navigation/summary/query snapshot/lookup/state/resize/error contracts', oracle: 'NOT RUN', queryResults: 'UI-only empty success envelope; no fabricated HIS result rows', lookup: 'UI-only envelopes reusing real LIB static options; no live dictionary query' }, null, 2));
  console.log('PASS ' + run);
})().catch(async e => { console.error(e); if (app) { const p = await app.firstWindow(); console.error(await p.evaluate(() => ({ status: document.querySelector('#operation-status')?.textContent, notice: document.querySelector('#notice-body')?.textContent, scripts: [...document.scripts].map(s => s.src) }))); } process.exitCode = 1; }).finally(async () => { if (app) await app.close(); });
