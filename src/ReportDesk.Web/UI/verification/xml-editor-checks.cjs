'use strict';
// Browser-only contract checks with synthetic XML; no server, SQL, or patient data.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require(path.resolve(__dirname, '../../../ReportDesk.Desktop/node_modules/playwright'));
const executablePath = path.join(process.env.LOCALAPPDATA, 'ms-playwright/chromium-1217/chrome-win64/chrome.exe');
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.setContent('<div><button id="sql">SQL</button></div>');
    await page.addScriptTag({ content: `
      const $ = s => document.querySelector(s);
      let selected='r1', detail={demo:false}, currentReportSession={source:0}, busy=false, dead=false, reports=[];
      window.calls=[]; window.allowConfirm=true; window.cancelSave=false; window.failOpen=false; window.missingDetail=false;
      window.confirm=()=>window.allowConfirm;
      async function task(_label, action) { busy=true; try { await action(); } catch(e) { window.lastError=e.message; } finally { busy=false; } }
      async function select() {}
      async function call(method,args={}) {
        window.calls.push({method,args});
        if(method==='xmlEditorOpen') {
          if(window.failOpen) throw Error('模板读取失败');
          if(args.kind==='detail' && window.missingDetail) throw Error('未找到唯一的明细报表模板');
          const layout=args.kind!=='query';
          return {reportId:'r1',title:'测试报表',kind:args.kind,path:layout?'C:\\\\LIB\\\\layout.xml':'C:\\\\LIB\\\\query.xml',token:layout?'layout-token':'query-token',text:layout?'<Spread/>':'<ReportQueryInfo/>',matchNotice:layout?'同名候选，请核对模板':''};
        }
        if(method==='xmlEditorCheck') return {passed:true,message:'格式正确'};
        if(method==='xmlEditorSave') {
          if(window.cancelSave) return null;
          return {saved:true,reloaded:false,message:'已保存',editor:{reportId:'r1',title:'测试报表',kind:'layout',path:'C:\\\\LIB\\\\layout.xml',token:'next-token',text:args.text,matchNotice:'同名候选，请核对模板'}};
        }
        return {};
      }
    ` });
    await page.addScriptTag({ path: path.resolve(__dirname, '../xml-editor.js') });
    await page.locator('#xml-editor-open').click();
    assert.equal(await page.locator('#xml-editor-input').inputValue(), '<ReportQueryInfo/>');
    await page.locator('#xml-editor-input').fill('<ReportQueryInfo>draft</ReportQueryInfo>');
    await page.evaluate(() => { window.allowConfirm=false; });
    await page.locator('#xml-editor-kind').selectOption('layout');
    assert.equal(await page.locator('#xml-editor-kind').inputValue(), 'query');
    assert.match(await page.locator('#xml-editor-input').inputValue(), /draft/);
    await page.evaluate(() => { window.allowConfirm=true; window.failOpen=true; });
    await page.locator('#xml-editor-kind').selectOption('layout');
    await page.waitForFunction(() => window.lastError === '模板读取失败');
    assert.equal(await page.locator('#xml-editor-kind').inputValue(), 'query');
    assert.match(await page.locator('#xml-editor-input').inputValue(), /draft/);
    await page.evaluate(() => { window.failOpen=false; });
    await page.locator('#xml-editor-kind').selectOption('layout');
    await page.waitForFunction(() => document.querySelector('#xml-editor-input').value === '<Spread/>');
    assert.match(await page.locator('#xml-editor-match').innerText(), /候选/);
    await page.locator('#xml-editor-input').fill('<Spread><Cell>测试</Cell></Spread>');
    await page.evaluate(() => { window.missingDetail=true; });
    await page.locator('#xml-editor-kind').selectOption('detail');
    await page.waitForFunction(() => window.lastError === '未找到唯一的明细报表模板');
    assert.equal(await page.locator('#xml-editor-kind').inputValue(), 'layout');
    assert.match(await page.locator('#xml-editor-input').inputValue(), /测试/);
    assert.match(await page.locator('#xml-editor-status').innerText(), /未找到唯一的明细/);
    await page.evaluate(() => { window.missingDetail=false; });
    await page.locator('#xml-editor-kind').selectOption('detail');
    await page.waitForFunction(() => document.querySelector('#xml-editor-kind').value === 'detail');
    assert.equal(await page.locator('#xml-editor-input').inputValue(), '<Spread/>');
    await page.locator('#xml-editor-kind').selectOption('layout');
    await page.waitForFunction(() => document.querySelector('#xml-editor-kind').value === 'layout');
    await page.locator('#xml-editor-input').fill('<Spread><Cell>测试</Cell></Spread>');
    await page.locator('#xml-editor-check').click();
    assert.match(await page.locator('#xml-editor-status').innerText(), /格式正确/);
    await page.evaluate(() => { window.cancelSave=true; });
    await page.locator('#xml-editor-save').click();
    assert.match(await page.locator('#xml-editor-status').innerText(), /取消保存/);
    assert.match(await page.locator('#xml-editor-input').inputValue(), /测试/);
    await page.evaluate(() => { window.cancelSave=false; });
    await page.locator('#xml-editor-save').click();
    assert.match(await page.locator('#xml-editor-status').innerText(), /已保存/);
    assert.equal(await page.locator('#xml-editor-save').isDisabled(), true);
    const calls = await page.evaluate(() => window.calls);
    const save = calls.filter(c => c.method === 'xmlEditorSave').at(-1);
    assert.deepEqual(Object.keys(save.args).sort(), ['reportId','text','token']);
    assert.equal(save.args.token, 'layout-token');
    assert.ok(!calls.some(c => ['query','layoutPreview','sqlEditorSave'].includes(c.method)));
    assert.deepEqual(errors, []);
    console.log('PASS XML editor: open, draft preservation, switch failure, candidate notice, check, cancel and save.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
