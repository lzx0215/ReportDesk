'use strict';
// Browser/UI protocol checks only. The server below returns metadata fixtures;
// no Oracle, real credentials, report result rows or production files are used.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const web = path.resolve(__dirname, '..');
const desktop = path.resolve(web, '../../ReportDesk.Desktop');
const root = path.resolve(web, '../../..');
const { chromium } = require(path.join(desktop, 'node_modules/playwright'));
const cachedBrowser = path.join(process.env.LOCALAPPDATA, 'ms-playwright/chromium-1217/chrome-win64/chrome.exe');
const evidence = path.join(root, 'output/playwright/local-default-20261106');
let server, browser, origin, temporaryDirectory;
const fixturePassword = 'UI-CHECK-NOT-A-REAL-PASSWORD';
async function setup(admin, serverReportsEnabled = true) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const state = { active: 'profile-a', localMode:false, alive:true, catalogChecks:0, nextId: 3, fail: '', calls: [], errors: [], profiles: [
    { profileId:'profile-a', name:'HIS 查询库', mode:0, host:'his-ui.invalid', port:1521, service:'HIS_UI', username:'ui_reader', remember:true, hasPassword:true },
    { profileId:'profile-b', name:'报表校验库', mode:0, host:'check-ui.invalid', port:1521, service:'CHECK_UI', username:'ui_reader', remember:false, hasPassword:true }
  ] };
  const dto = id => ({ ...state.profiles.find(item => item.profileId === id), activeProfileId:state.active,
    profiles:state.profiles.map(item => ({ id:item.profileId, name:item.name, isActive:item.profileId === state.active, hasPassword:item.hasPassword })) });
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) { state.errors.push('External request rejected'); return route.abort(); }
    const local = url.pathname, json = data => route.fulfill({ contentType:'application/json', body:JSON.stringify(data) });
    if (local === '/api/session') return json({ csrf:'settings-ui-csrf', admin, offline:true, serverReportsEnabled, idleMinutes:60, reportsDirectory:admin && serverReportsEnabled ? 'C:\\LIB' : null, syncReady:false, syncSeconds:30 });
    if (local === '/api/catalog-status') { state.catalogChecks++; return json({version:'settings-fixture'}); }
    if (local === '/api/tab-lifecycle') return json({ok:true,data:{alive:state.alive,local:state.localMode,busy:false}});
    if (local.startsWith('/api/')) {
      assert.equal(request.method(),'POST'); assert.equal(request.headers()['x-reportdesk-csrf'],'settings-ui-csrf');
      const {args,tabId} = request.postDataJSON(), method = local.split('/').at(-1);
      state.calls.push({method,args,tabId});
      if (state.fail === method) { state.fail = ''; return json({ok:false,message:'离线检查：操作失败，原连接保持不变。'}); }
      let data = {};
      if (method === 'bootstrap') data = {reports:[],offline:true,warnings:[]};
      else if (method === 'list') data = [];
      else if (method === 'settings') data = dto(args.profileId || state.active);
      else if (method === 'discoverTns') data = [];
      else if (method === 'saveSettings' || method === 'testConnection') {
        const id = args.newProfile ? 'profile-' + state.nextId++ : args.profileId || state.active;
        const old = state.profiles.find(item => item.profileId === id), value = {profileId:id};
        for (const key of ['name','mode','host','port','service','username','tnsFile','tnsAlias','remember']) value[key] = args[key];
        value.hasPassword = args.keepPassword ? !!old?.hasPassword : !!args.password;
        const index = state.profiles.findIndex(item => item.profileId === id);
        if (index < 0) state.profiles.push(value); else state.profiles[index] = value;
        data = method === 'testConnection' ? {version:'UI-FIXTURE',settings:dto(id)} : dto(id);
      } else if (method === 'activateConnection') { state.active = args.profileId; data = dto(state.active); }
      else if (method === 'syncStatus') data = {enabled:false,message:'离线界面检查，未访问 HIS。',conflicts:[]};
      else if (method === 'uploadImport') data = {path:args.folder ? 'fixture-folder' : 'fixture.xml'};
      else if (method === 'import') data = {reports:[],imported:0,pending:0,layouts:0,otherXml:0,errors:[],incomplete:[],skipped:0,matched:0,candidates:0,unresolved:0};
      else if (method === 'localBegin' || method === 'localUpload') data = {};
      else if (method === 'localActivate' || method === 'localUseServer') { state.localMode = method === 'localActivate'; state.alive = true; data = {reports:[]}; }
      else if (method === 'localStatus') data = {pending:null,requiresRescan:false};
      else if (method === 'clear') data = {};
      else throw new Error('Unexpected fixture method: ' + method);
      return json({ok:true,data});
    }
    if (local === '/' || local === '/admin') return route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(web,'index.html'))});
    if (local.startsWith('/assets/')) {
      const name = path.basename(local), file = ['transport.js','web.js','web.css','local-directory.js','xml-editor.js'].includes(name) ? path.join(web,name) : path.join(desktop,'ui',name);
      if (fs.existsSync(file)) return route.fulfill({contentType:name.endsWith('.css') ? 'text/css' : name.endsWith('.svg') ? 'image/svg+xml' : 'text/javascript',body:fs.readFileSync(file)});
    }
    return route.fulfill({status:404,body:''});
  });
  const page = await context.newPage(); page.setDefaultTimeout(10000);
  page.on('pageerror',error => state.errors.push(error.message));
  await page.goto(origin + (admin ? '/admin' : '/'));
  await page.waitForFunction(() => document.title.startsWith('ReportDesk Web') && !busy);
  if (admin) await page.waitForFunction(() => !!window.reportDeskSettings && !!window.reportDeskSources);
  return {context,page,state};
}
const idle = page => page.waitForFunction(() => !busy);
async function choose(page, selector, value, accept) {
  if (typeof accept === 'boolean') page.once('dialog', dialog => accept ? dialog.accept() : dialog.dismiss());
  await page.locator(selector).selectOption(value); await idle(page);
}
async function checkAdmin() {
  const {context,page,state} = await setup(true);
  const count = method => state.calls.filter(item => item.method === method).length;
  const last = method => state.calls.filter(item => item.method === method).at(-1).args;
  // Shortcut tabs are independent of database configuration and TNS discovery.
  await page.locator('#web-sync-open').click(); await idle(page);
  assert.equal(await page.locator('#settings-tab-sync').getAttribute('aria-selected'),'true');
  assert.equal(count('settings'),0); assert.equal(count('discoverTns'),0);
  await page.locator('#web-settings-close').click();
  await page.locator('#web-import-open').click(); await idle(page);
  await page.locator('#source-tab-local').click();
  assert.equal(await page.locator('#local-directory-pick').isVisible(),true);
  assert.equal(count('settings'),0);
  await page.locator('#source-close').click();
  await page.locator('#settings-open').click(); await idle(page);
  assert.equal(await page.locator('#settings-tab-local').count(),0);
  assert.equal(await page.locator('#connection [role=tab]').count(),2);
  assert.equal(await page.locator('#conn-host').isEnabled(),true);
  assert.equal(await page.locator('#conn-profile option').count(),2);
  assert.equal(await page.locator('#conn-activate').isEnabled(),false);
  await page.locator('#conn-host').fill('unsaved-ui.invalid');
  // Opening the separate source dialog leaves a database draft untouched.
  await page.evaluate(() => window.reportDeskSources.open('local')); await idle(page);
  assert.equal(await page.locator('#local-directory-pick').isVisible(),true);
  await page.locator('#source-close').click();
  assert.equal(await page.locator('#conn-host').inputValue(),'unsaved-ui.invalid');
  await page.locator('#settings-tab-sync').click(); await idle(page);
  assert.equal(await page.locator('#local-directory-settings').isVisible(),false);
  await page.locator('#settings-tab-database').click();
  assert.equal(await page.locator('#conn-host').inputValue(),'unsaved-ui.invalid');
  assert.equal(count('saveSettings'),0); assert.equal(count('testConnection'),0); assert.equal(count('activateConnection'),0);
  await page.locator('#settings-tab-database').focus(); await page.keyboard.press('End'); await idle(page);
  assert.equal(await page.locator('#settings-tab-sync').getAttribute('aria-selected'),'true');
  await page.keyboard.press('Home'); await idle(page);
  assert.equal(await page.locator('#settings-tab-database').getAttribute('aria-selected'),'true');
  // Rejecting a profile change preserves the original profile and unsaved input.
  await choose(page,'#conn-profile','profile-b',false);
  assert.equal(await page.locator('#conn-profile').inputValue(),'profile-a');
  assert.equal(await page.locator('#conn-host').inputValue(),'unsaved-ui.invalid');
  await choose(page,'#conn-profile','profile-b',true);
  assert.equal(last('settings').profileId,'profile-b'); assert.equal(state.active,'profile-a');
  assert.equal(await page.locator('#conn-activate').isEnabled(),true);
  // Escape cancellation must not erase a password draft before the confirmation.
  await page.locator('#conn-keep').uncheck(); await page.locator('#conn-password').fill(fixturePassword);
  page.once('dialog',dialog => dialog.dismiss()); await page.keyboard.press('Escape');
  assert.equal(await page.locator('#connection').isVisible(),true);
  assert.equal(await page.locator('#conn-password').inputValue(),fixturePassword);
  page.once('dialog',dialog => dialog.accept()); await page.locator('#web-settings-close').click();
  await page.waitForFunction(() => !document.querySelector('#connection').open && document.querySelector('#conn-password').value === '');
  assert.equal(await page.locator('#conn-password').inputValue(),'');
  await page.locator('#settings-open').click(); await idle(page);
  assert.equal(await page.locator('#conn-profile').inputValue(),'profile-a');
  // Creating a connection must neither inherit a saved password nor activate it.
  await page.locator('#conn-new').click();
  assert.equal(await page.locator('#conn-name').inputValue(),'');
  assert.equal(await page.locator('#conn-host').inputValue(),'');
  assert.equal(await page.locator('#conn-keep').isChecked(),false);
  assert.equal(await page.locator('#conn-password').inputValue(),'');
  assert.equal(await page.locator('#conn-activate').isEnabled(),false);
  await page.locator('#conn-name').fill('离线验证连接'); await page.locator('#conn-host').fill('new-ui.invalid');
  await page.locator('#conn-service').fill('UI_SERVICE'); await page.locator('#conn-user').fill('ui_reader');
  await page.locator('#conn-password').fill(fixturePassword);
  await page.locator('#connection-form button[type=submit]').click(); await idle(page);
  const createArgs = last('saveSettings'); assert.equal(createArgs.newProfile,true); assert.equal('profileId' in createArgs,false);
  assert.equal(createArgs.keepPassword,false); assert.equal(createArgs.password,fixturePassword);
  assert.equal(state.active,'profile-a'); assert.equal(await page.locator('#conn-profile option').count(),3);
  assert.equal(await page.locator('#conn-password').inputValue(),'');
  const created = await page.locator('#conn-profile').inputValue();
  assert.equal(await page.locator('#conn-activate').isEnabled(),true);
  page.once('dialog',dialog => dialog.dismiss()); await page.locator('#conn-activate').click();
  assert.equal(count('activateConnection'),0);
  page.once('dialog',dialog => dialog.accept()); await page.locator('#conn-activate').click(); await idle(page);
  assert.deepEqual(last('activateConnection'),{profileId:created}); assert.equal(state.active,created);
  assert.match(await page.locator('#connection-state').innerText(),/离线验证连接/);
  // A failed save leaves the visible draft and persisted profile unchanged.
  state.fail = 'saveSettings'; await page.locator('#conn-host').fill('failed-save-ui.invalid');
  await page.locator('#connection-form button[type=submit]').click(); await idle(page);
  assert.match(await page.locator('#test-status').innerText(),/操作失败/);
  assert.equal(await page.locator('#conn-host').inputValue(),'failed-save-ui.invalid');
  assert.equal(state.profiles.find(item => item.profileId === created).host,'new-ui.invalid');
  await page.locator('[data-close="notice"]').click();
  await choose(page,'#conn-profile','profile-b',true);
  await page.locator('#conn-keep').uncheck(); await page.locator('#conn-password').fill(fixturePassword);
  state.fail = 'testConnection'; await page.locator('#test-connection').click(); await idle(page);
  assert.equal(await page.locator('#conn-password').inputValue(),fixturePassword); assert.equal(state.active,created);
  await page.locator('[data-close="notice"]').click();
  await page.locator('#test-connection').click(); await idle(page);
  assert.equal(last('testConnection').profileId,'profile-b'); assert.equal(state.active,created);
  assert.equal(await page.locator('#conn-password').inputValue(),'');
  assert.match(await page.locator('#test-status').innerText(),/当前使用的连接未改变/);
  assert.deepEqual(await page.evaluate(() => [localStorage.length,sessionStorage.length]),[0,0]);
  await page.locator('#web-settings-close').click();
  // The single rail entry retains both native file-selection paths.
  assert.equal(await page.locator('.rail #web-import-open').count(),1);
  assert.equal(await page.locator('#web-import-open').getAttribute('aria-label'),'报表来源');
  assert.equal(await page.locator('.rail #import-file, .rail #import-folder').count(),0);
  const fixtureDirectory = path.join(temporaryDirectory,'import-fixture'); fs.mkdirSync(fixtureDirectory);
  const filePath = path.join(fixtureDirectory,'ui-protocol.xml'); fs.writeFileSync(filePath,'<ReportQueryInfo />');
  for (const folder of [false,true]) {
    await page.locator('#web-import-open').click();
    await page.locator('#source-tab-server').click();
    assert.equal(await page.locator('#web-report-sources').isVisible(),true);
    const chooserPromise = page.waitForEvent('filechooser');
    await page.locator(folder ? '#import-folder' : '#import-file').click();
    const chooser = await chooserPromise;
    assert.equal(chooser.isMultiple(),folder);
    assert.equal(await chooser.element().getAttribute('webkitdirectory'),folder ? 'true' : null);
    await chooser.setFiles(folder ? fixtureDirectory : filePath); await idle(page);
    await page.locator('#notice[open]').waitFor();
    assert.equal(last('uploadImport').folder,folder); assert.equal(last('import').folder,folder);
    assert.equal(last('uploadImport').files.length,1); assert.equal(last('uploadImport').files[0].text,'<ReportQueryInfo />');
    assert.equal(last('import').path,folder ? 'fixture-folder' : 'fixture.xml');
    await page.locator('[data-close="notice"]').click();
    if (await page.locator('#web-report-sources').isVisible()) await page.locator('#source-close').click();
  }
  await page.locator('#web-import-open').click(); await page.keyboard.press('Escape');
  assert.equal(await page.locator('#web-report-sources').isVisible(),false);
  assert.equal(await page.locator('#web-import-open').evaluate(node => node === document.activeElement),true);
  assert.equal(count('query'),0);
  await checkLocalSource(page,state,count);
  // Capture the complete settings and report-source surfaces at both viewport sizes.
  for (const [name,viewport] of [['desktop',{width:1440,height:1000}],['narrow',{width:390,height:844}]]) {
    await page.setViewportSize(viewport); await page.locator('#settings-open').click(); await idle(page);
    for (const tab of ['database','sync']) {
      await page.locator('#settings-tab-' + tab).click(); await idle(page);
      const bounds = await page.locator('#connection').evaluate(node => ({left:node.getBoundingClientRect().left,right:node.getBoundingClientRect().right,client:node.clientWidth,scroll:node.scrollWidth}));
      assert.ok(bounds.left >= -1 && bounds.right <= viewport.width + 1,'dialog fits ' + name);
      assert.ok(bounds.scroll <= bounds.client + 1,'dialog has no horizontal overflow: ' + name + '/' + tab);
      await page.screenshot({path:path.join(evidence,name + '-' + tab + '.png')});
    }
    await page.locator('#web-settings-close').click(); await page.locator('#web-import-open').click();
    for (const tab of ['local','server']) {
      await page.locator('#source-tab-' + tab).click(); await idle(page);
      const bounds = await page.locator('#web-report-sources').evaluate(node => ({left:node.getBoundingClientRect().left,right:node.getBoundingClientRect().right,client:node.clientWidth,scroll:node.scrollWidth}));
      assert.ok(bounds.left >= -1 && bounds.right <= viewport.width + 1,'source dialog fits ' + name);
      assert.ok(bounds.scroll <= bounds.client + 1,'source dialog has no horizontal overflow: ' + name + '/' + tab);
      await page.screenshot({path:path.join(evidence,name + '-source-' + tab + '.png')});
    }
    await page.locator('#source-close').click();
  }
  assert.deepEqual(state.errors,[]);
  assert.equal(new Set(state.calls.map(call => call.tabId)).size,1);
  fs.writeFileSync(path.join(evidence,'summary.json'),JSON.stringify({browser:browser.version(),checks:'settings tabs/drafts/profiles/save/test/activate/report sources/import/local active and staging/recovery links/responsive/public',apiCounts:Object.fromEntries([...new Set(state.calls.map(c=>c.method))].map(method=>[method,count(method)])),errors:state.errors,localEvidence:'Real OPFS directory handles with a substituted OS picker and backend protocol fixtures; no real report data or database',oracle:'NOT RUN',iis:'NOT RUN'},null,2));
  await context.close();
}
async function checkLocalSource(page,state,count) {
  // Native directory handles and local mode are real; only the OS picker and
  // server responses are substituted. This checks source routing, not XML parsing.
  await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const source = await root.getDirectoryHandle('UI-HIS-COPY',{create:true});
    const backup = await root.getDirectoryHandle('UI-HIS-BACKUP',{create:true});
    const file = await source.getFileHandle('ui-protocol.xml',{create:true});
    const stream = await file.createWritable(); await stream.write('<ReportQueryInfo />'); await stream.close();
    window.showDirectoryPicker = async options => options.id === 'reportdesk-his' ? source : backup;
  });
  await page.locator('#web-import-open').click(); await page.locator('#source-tab-local').click();
  await page.locator('#local-directory-pick').click(); await idle(page);
  await page.locator('#local-directory-backup-pick').click(); await idle(page);
  await page.locator('#local-directory-use').click(); await idle(page);
  assert.equal(await page.evaluate(() => reportDeskLocal.active),true);
  assert.equal(await page.locator('#source-tab-local').getAttribute('aria-selected'),'true');
  await page.locator('#source-close').click();
  assert.equal(await page.locator('#web-import-open').isVisible(),true,'local mode retains the source-management entry');
  await page.locator('#web-import-open').click();
  assert.equal(await page.locator('#source-tab-local').getAttribute('aria-selected'),'true');
  await page.locator('#source-tab-server').click();
  assert.equal(await page.evaluate(() => reportDeskLocal.active),true,'a source tab change is not a mode switch');
  assert.match(await page.locator('#source-current-status').innerText(),/本机/);
  assert.equal(await page.locator('#import-file').isVisible(),false);
  assert.equal(await page.locator('#import-folder').isVisible(),false);
  assert.equal(await page.locator('#source-server-local-hint').isVisible(),true);
  assert.equal(await page.locator('#local-directory-server').isVisible(),true);
  const beforeImport = count('import');
  const rejectedImport = await page.evaluate(() => reportDesk.call('import',{folder:false,path:'ui-protocol.xml'}));
  assert.equal(rejectedImport.ok,false); assert.equal(count('import'),beforeImport);
  const beforeSwitch = count('localUseServer');
  page.once('dialog',dialog => dialog.dismiss()); await page.locator('#local-directory-server').click(); await idle(page);
  assert.equal(count('localUseServer'),beforeSwitch); assert.equal(await page.evaluate(() => reportDeskLocal.active),true);
  await page.locator('#source-close').click();
  for (const editor of ['sql-editor','xml-editor','layout-editor']) {
    await page.evaluate(id => document.getElementById(id).showModal(),editor);
    await page.locator('#' + editor + '-local-settings').click(); await idle(page);
    await page.locator('#web-report-sources').waitFor({state:'visible'});
    assert.equal(await page.locator('#web-report-sources').isVisible(),true);
    assert.equal(await page.locator('#source-tab-local').getAttribute('aria-selected'),'true');
    assert.equal(await page.locator('#local-directory-inspect').isVisible(),true);
    assert.equal(await page.locator('#connection').isVisible(),false,'recovery links open report sources, not database settings');
    await page.locator('#local-directory-inspect').click(); await idle(page);
    assert.match(await page.locator('#local-directory-status').innerText(),/没有待确认保存/);
    await page.locator('#source-close').click(); await page.evaluate(id => document.getElementById(id).close(),editor);
  }
  await page.locator('#web-import-open').click(); await page.locator('#source-tab-server').click();
  page.once('dialog',dialog => dialog.accept()); await page.locator('#local-directory-server').click(); await idle(page);
  assert.equal(count('localUseServer'),beforeSwitch + 1); assert.equal(await page.evaluate(() => reportDeskLocal.active),false);
  assert.match(await page.locator('#source-current-status').innerText(),/服务器/);
  assert.equal(await page.locator('#import-file').isEnabled(),true);
  assert.equal(await page.locator('#import-folder').isEnabled(),true);
  // Failure during activation leaves staging recoverable from the same source entry.
  await page.locator('#source-tab-local').click(); state.fail = 'localActivate';
  const use = await page.locator('#local-directory-use').isVisible() ? '#local-directory-use' : '#local-directory-rescan';
  await page.locator(use).click(); await idle(page); await page.locator('[data-close="notice"]').click();
  assert.equal(await page.evaluate(() => reportDeskLocal.state.staging),true);
  await page.locator('#source-close').click(); assert.equal(await page.locator('#web-import-open').isVisible(),true);
  await page.locator('#web-import-open').click(); await page.locator('#source-tab-server').click();
  assert.equal(await page.locator('#import-file').isVisible(),false);
  page.once('dialog',dialog => dialog.accept()); await page.locator('#local-directory-server').click(); await idle(page);
  assert.equal(await page.evaluate(() => reportDeskLocal.state.staging),false);
  assert.equal(await page.locator('#import-file').isEnabled(),true);
  await page.locator('#source-close').click();
}
async function checkPublic() {
  const {context,page,state} = await setup(false);
  assert.equal(await page.locator('#settings-open').isVisible(),false);
  assert.equal(await page.locator('#import-file').isVisible(),false);
  assert.equal(await page.locator('#import-folder').isVisible(),false);
  assert.equal(await page.locator('#web-import-open').count(),0);
  const result = await page.evaluate(() => window.reportDesk.call('activateConnection',{profileId:'profile-a'}));
  assert.equal(result.ok,false); assert.equal(state.calls.some(call => call.method === 'activateConnection'),false);
  assert.deepEqual(state.errors,[]); await context.close();
}
async function checkLocalDefault() {
  const {context,page,state} = await setup(true,false);
  const count = method => state.calls.filter(item => item.method === method).length;
  assert.equal(state.catalogChecks,0,'local default does not inspect the server catalog');
  assert.equal(await page.locator('#local-start-open').isVisible(),true);
  for (const id of ['check-new','web-sync-open','web-publish-preview-open','import-file','import-folder'])
    assert.equal(await page.locator('#' + id).isVisible(),false,id + ' is absent from local-default flows');
  assert.equal(count('localBegin'),0,'opening the page does not read or upload a directory');
  await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    const source = await root.getDirectoryHandle('LOCAL-DEFAULT-COPY',{create:true});
    const backup = await root.getDirectoryHandle('LOCAL-DEFAULT-BACKUP',{create:true});
    const file = await source.getFileHandle('ui-protocol.xml',{create:true});
    const stream = await file.createWritable(); await stream.write('<ReportQueryInfo />'); await stream.close();
    window.testDefaultLocal = {source,backup,pickers:0};
    window.showDirectoryPicker = async options => { testDefaultLocal.pickers++; return options.id === 'reportdesk-his' ? source : backup; };
  });
  await page.locator('#local-start-open').click(); await page.locator('#web-report-sources').waitFor({state:'visible'});
  assert.equal(await page.locator('#source-tab-server').isVisible(),false);
  assert.equal(await page.locator('#local-directory-pick').isVisible(),true);
  assert.equal(await page.evaluate(() => testDefaultLocal.pickers),0,'opening sources never invokes the OS picker');
  assert.equal(count('settings'),0); assert.equal(count('saveSettings'),0); assert.equal(count('testConnection'),0);
  await page.locator('#local-directory-pick').click(); await idle(page);
  await page.locator('#local-directory-backup-pick').click(); await idle(page);
  assert.equal(await page.evaluate(() => testDefaultLocal.pickers),2);
  await page.locator('#local-directory-use').click(); await idle(page);
  assert.equal(await page.evaluate(() => reportDeskLocal.active),true);
  assert.equal(await page.locator('#local-directory-server').innerText(),'关闭当前目录');
  assert.equal(await page.locator('#source-panel-local #local-directory-server').count(),1);
  assert.equal(await page.locator('#local-start').isVisible(),false);
  // A directory close cannot discard a draft or an open editor.
  const beforeClose = count('localUseServer');
  await page.evaluate(() => reportDesk.call('sqlEditorDirty',{dirty:true}));
  await page.locator('#local-directory-server').click(); await idle(page);
  await page.locator('#notice[open]').waitFor(); assert.equal(count('localUseServer'),beforeClose);
  await page.locator('[data-close="notice"]').click();
  await page.evaluate(() => reportDesk.call('sqlEditorDirty',{dirty:false}));
  page.once('dialog',dialog => dialog.dismiss()); await page.locator('#local-directory-server').click(); await idle(page);
  assert.equal(await page.evaluate(() => reportDeskLocal.active),true);
  page.once('dialog',dialog => dialog.accept()); await page.locator('#local-directory-server').click(); await idle(page);
  assert.equal(count('localUseServer'),beforeClose + 1);
  assert.deepEqual(await page.evaluate(async () => ({active:reportDeskLocal.active,source:reportDeskLocal.state.source,backup:reportDeskLocal.state.backup,text:await (await (await testDefaultLocal.source.getFileHandle('ui-protocol.xml')).getFile()).text()})),
    {active:false,source:'LOCAL-DEFAULT-COPY',backup:'LOCAL-DEFAULT-BACKUP',text:'<ReportQueryInfo />'});
  await page.locator('#source-close').click(); assert.equal(await page.locator('#local-start-open').isVisible(),true);
  // Explicit reload is the only way back after the server workspace is lost.
  await page.locator('#local-start-open').click(); await page.locator('#local-directory-use').click(); await idle(page);
  await page.locator('#source-close').click();
  await page.evaluate(async () => {
    document.getElementById('sql-editor-input').value = '-- RETAINED_UI_DRAFT';
    document.getElementById('sql-editor').showModal();
    await reportDesk.call('sqlEditorDirty',{dirty:true});
  });
  const beforeLoss = state.calls.length;
  await page.evaluate(() => window.dispatchEvent(new Event('reportdesk-workspace-lost')));
  assert.deepEqual(await page.evaluate(() => ({active:reportDeskLocal.active,staging:reportDeskLocal.state.staging,lost:reportDeskLocal.state.workspaceLost,scan:reportDeskLocal.state.needsScan,draft:document.getElementById('sql-editor-input').value})),
    {active:false,staging:false,lost:true,scan:true,draft:'-- RETAINED_UI_DRAFT'});
  assert.equal(state.calls.length,beforeLoss,'workspace loss does not upload files or retry operations');
  assert.equal(await page.locator('#local-workspace-warning').isVisible(),true);
  assert.equal(await page.locator('#sql-editor-local-settings').isVisible(),true,'inactive lost workspace still exposes recovery');
  for (const method of ['query','sqlEditorSave']) {
    const rejected = await page.evaluate(method => reportDesk.call(method,{}),method);
    assert.equal(rejected.ok,false); assert.match(rejected.message,/失效|重新扫描/);
  }
  assert.equal(state.calls.length,beforeLoss);
  await page.locator('#sql-editor-local-settings').click(); await idle(page);
  await page.locator('#local-directory-inspect').click(); await idle(page);
  assert.match(await page.locator('#local-directory-status').innerText(),/失效/);
  await page.locator('#local-directory-rescan').click(); await idle(page);
  await page.locator('#notice[open]').waitFor(); assert.equal(count('localBegin'),2,'open drafts block rescan');
  await page.locator('[data-close="notice"]').click();
  await page.locator('#source-close').click();
  await page.evaluate(async () => { document.getElementById('sql-editor').close(); await reportDesk.call('sqlEditorDirty',{dirty:false}); });
  // Capture the loss state before explicit recovery.
  await page.screenshot({path:path.join(evidence,'desktop-local-lost.png')});
  await page.locator('#local-start-open').click(); await page.locator('#local-directory-rescan').click(); await idle(page);
  assert.equal(await page.evaluate(() => reportDeskLocal.state.workspaceLost),false);
  assert.equal(count('localBegin'),3); assert.equal(count('query'),0);
  assert.equal(count('settings'),0); assert.equal(count('saveSettings'),0); assert.equal(count('testConnection'),0);
  page.once('dialog',dialog => dialog.accept()); await page.locator('#local-directory-server').click(); await idle(page);
  await page.locator('#source-close').click();
  // The single local-source surface and database-only settings remain usable on narrow screens.
  for (const [name,viewport] of [['desktop',{width:1440,height:1000}],['narrow',{width:390,height:844}]]) {
    await page.setViewportSize(viewport);
    await page.screenshot({path:path.join(evidence,name + '-local-empty.png')});
    await page.locator('#local-start-open').click(); await page.locator('#web-report-sources').waitFor({state:'visible'});
    const sourceBounds = await page.locator('#web-report-sources').evaluate(node => ({left:node.getBoundingClientRect().left,right:node.getBoundingClientRect().right,client:node.clientWidth,scroll:node.scrollWidth}));
    assert.ok(sourceBounds.left >= -1 && sourceBounds.right <= viewport.width + 1);
    assert.ok(sourceBounds.scroll <= sourceBounds.client + 1);
    await page.screenshot({path:path.join(evidence,name + '-local-only-source.png')});
    await page.locator('#source-close').click(); await page.locator('#settings-open').click(); await idle(page);
    assert.equal(await page.locator('#settings-tab-sync').isVisible(),false);
    assert.equal(await page.locator('#conn-host').isEnabled(),true);
    await page.screenshot({path:path.join(evidence,name + '-local-only-database.png')});
    await page.locator('#web-settings-close').click();
  }
  assert.deepEqual(state.errors,[]); assert.equal(state.catalogChecks,0);
  fs.writeFileSync(path.join(evidence,'local-default-summary.json'),JSON.stringify({browser:browser.version(),checks:'local-only empty state; explicit picker; no server catalog/sync/import; no connection side effects; close confirmation and retained files/handles; draft guard; lost workspace preserves draft; no replay; explicit rescan; public permission unchanged; desktop/narrow',apiCounts:Object.fromEntries([...new Set(state.calls.map(c=>c.method))].map(method=>[method,count(method)])),errors:state.errors,evidenceTier:'Real OPFS, substituted OS picker and backend protocol fixtures',oracle:'NOT RUN',iis:'NOT RUN'},null,2));
  await context.close();
  const publicPage = await setup(false,false);
  assert.equal(await publicPage.page.locator('#local-start').isVisible(),true);
  assert.equal(await publicPage.page.locator('#local-start-open').isVisible(),false);
  assert.equal(await publicPage.page.locator('#settings-open').isVisible(),false);
  assert.equal(publicPage.state.catalogChecks,0); assert.deepEqual(publicPage.state.errors,[]);
  await publicPage.context.close();
}
(async () => {
  fs.mkdirSync(evidence,{recursive:true}); temporaryDirectory = fs.mkdtempSync(path.join(os.tmpdir(),'reportdesk-settings-ui-'));
  server = http.createServer((request,response) => {response.writeHead(404);response.end();});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve)); origin = 'http://127.0.0.1:' + server.address().port;
  browser = await chromium.launch({headless:true,...(fs.existsSync(cachedBrowser) ? {executablePath:cachedBrowser} : {}),args:['--disable-background-networking']});
  await checkPublic(); await checkAdmin(); await checkLocalDefault();
  console.log('PASS: settings/profiles/activation/drafts/password isolation; server compatibility and native imports; local-default empty state and explicit picker; close preserves files/handles; workspace-loss guard and draft recovery; public denial; desktop/narrow screenshots. OPFS + protocol fixtures only; no Oracle/IIS.');
})().catch(error => {console.error(error);process.exitCode=1;}).finally(async () => {await browser?.close();server?.close();});
