'use strict';
// Production WebRuntime preview must be running offline at localhost:18765.
// Real retained XML bytes are copied into browser OPFS. Only the native picker
// is substituted; directory handles, IndexedDB, locks, hash and writes are real.
// This does NOT certify native OS picker/permission prompts or 360 compatibility.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { chromium } = require(path.resolve(__dirname, '../../../ReportDesk.Desktop/node_modules/playwright'));
const root = path.resolve(__dirname, '../../../..');
const fixtureRoot = path.join(root, 'artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml');
const evidence = path.join(root, 'output/playwright/local-default-20261106/runtime');
const runtimeData = path.resolve(process.argv[2] || path.join(evidence,'data'));
function temporaryXml() {
  const base = path.join(runtimeData,'LocalWorkspaces');
  const walk = directory => fs.existsSync(directory) ? fs.readdirSync(directory,{withFileTypes:true}).flatMap(entry => entry.isDirectory() ? walk(path.join(directory,entry.name)) : entry.name.toLowerCase().endsWith('.xml') ? [path.join(directory,entry.name)] : []) : [];
  return walk(base).sort();
}
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const names = ['抗菌药物消耗情况查询设置.xml', '抗菌药物消耗情况报表设置.xml'];
const fixtures = names.map(name => { const bytes = fs.readFileSync(path.join(fixtureRoot, name)); return {name, content:bytes.toString('base64'), hash:hash(bytes)}; });
const results = [];
async function run(channel) {
  const browser = await chromium.launch({ channel, headless:true });
  const peerChannel = channel === 'chrome' ? 'msedge' : 'chrome';
  const peerBrowser = await chromium.launch({channel:peerChannel,headless:true});
  let page,peer,lifecycleAuth;
  try {
    page = await browser.newPage({viewport:{width:1440,height:1100}});
    page.setDefaultTimeout(20000);
    page.on('request', request => {
      if (request.url().endsWith('/api/admin/localActivate')) lifecycleAuth = {tabId:request.postDataJSON().tabId,csrf:request.headers()['x-reportdesk-csrf']};
    });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await page.goto('http://localhost:18765/admin');
    await page.waitForFunction(() => window.reportDeskLocal && document.querySelector('#local-directory-pick') && !document.querySelector('#settings-open').disabled);
    const native = await page.evaluate(() => ({ secure:isSecureContext, picker:typeof window.showDirectoryPicker, locks:!!navigator.locks }));
    assert.deepEqual(native, {secure:true,picker:'function',locks:true});
    assert.equal(await page.evaluate(async () => (await reportDesk.ready).serverReportsEnabled),false);
    assert.equal(await page.locator('#local-start-open').isVisible(),true);
    assert.equal(temporaryXml().length,0,'initial local mode has no uploaded XML');
    await page.evaluate(async fixtures => {
      const root = await navigator.storage.getDirectory();
      const source = await root.getDirectoryHandle('HIS-COPY',{create:true});
      const backup = await root.getDirectoryHandle('BACKUPS',{create:true});
      const config = await source.getDirectoryHandle('Config',{create:true});
      const xml = await config.getDirectoryHandle('Xml',{create:true});
      for (const item of fixtures) {
        const target = await xml.getFileHandle(item.name,{create:true}), stream = await target.createWritable();
        await stream.write(Uint8Array.from(atob(item.content), c=>c.charCodeAt(0))); await stream.close();
      }
      window.testLocal = {source,backup,xml,fixtures};
      window.showDirectoryPicker = async options => options.id === 'reportdesk-his' ? source : backup;
    },fixtures);
    await page.locator('#settings-open').click();
    await page.locator('#conn-host').fill('unsaved-connection-draft.invalid');
    await page.evaluate(() => window.reportDeskSources.open('local'));
    await page.locator('#local-directory-pick').click();
    await page.locator('#local-directory-backup-pick').click();
    await page.locator('#local-directory-use').click();
    await page.waitForFunction(() => reportDeskLocal.active && !reportDeskLocal.working && !document.querySelector('#local-directory-use').disabled);
    console.log(channel + ': directory loaded');
    assert.equal(await page.locator('#conn-host').inputValue(),'unsaved-connection-draft.invalid');
    assert.equal(await page.locator('#library-count').innerText(),'1 张可显示报表');
    assert.equal(await page.locator('#web-sync').isVisible(),false);
    await page.locator('#web-report-sources').evaluate(node=>{node.scrollTop=0;});
    await page.screenshot({path:path.join(evidence,'connection-'+channel+'.png')});
    await page.locator('#source-close').click();
    await page.locator('#web-settings-close').click();
    // A separate browser owns a separate temporary workspace and original files.
    peer = await peerBrowser.newPage({viewport:{width:1440,height:1000}}); peer.setDefaultTimeout(20000);
    peer.on('pageerror', error => errors.push('peer: ' + error.message)); peer.on('dialog',dialog => dialog.accept());
    await peer.goto('http://localhost:18765/admin');
    await peer.waitForFunction(() => !!window.reportDeskSources && !busy);
    assert.equal(await peer.locator('#library-count').innerText(),'0 张可显示报表');
    assert.equal(await peer.evaluate(() => reportDeskLocal.active),false);
    await peer.evaluate(async fixtures => {
      const root = await navigator.storage.getDirectory();
      const source = await root.getDirectoryHandle('PEER-HIS-COPY',{create:true}), backup = await root.getDirectoryHandle('PEER-BACKUP',{create:true});
      for (const item of fixtures) { const file = await source.getFileHandle(item.name,{create:true}), stream = await file.createWritable(); await stream.write(Uint8Array.from(atob(item.content), c=>c.charCodeAt(0))); await stream.close(); }
      window.testPeer = {source,backup}; window.showDirectoryPicker = async options => options.id === 'reportdesk-his' ? source : backup;
    },fixtures);
    await peer.locator('#local-start-open').click();
    await peer.locator('#local-directory-pick').click(); await peer.waitForFunction(() => !busy);
    await peer.locator('#local-directory-backup-pick').click(); await peer.waitForFunction(() => !busy);
    await peer.locator('#local-directory-use').click(); await peer.waitForFunction(() => reportDeskLocal.active && !busy);
    await peer.locator('#source-close').click();
    assert.equal(await peer.locator('#library-count').innerText(),'1 张可显示报表');
    assert.equal(temporaryXml().length,4,'two browsers hold separate query/template copies');
    const offlineQuery = await page.evaluate(() => reportDesk.call('query',buildQueryPayload()));
    assert.equal(offlineQuery.ok,false); assert.match(offlineQuery.message,/离线/);
    await page.locator('#sql').click();
    await page.waitForFunction(() => document.querySelector('#sql-editor-input')?.value && !document.querySelector('#sql-editor-input').disabled);
    const sql = await page.locator('#sql-editor-input').inputValue();
    await page.locator('#sql-editor-input').fill(sql+'\n-- LOCAL_COPY_BROWSER_CHECK');
    await page.locator('#sql-editor-save').click();
    await page.waitForFunction(() => document.querySelector('#sql-editor-status').textContent.includes('本机文件已回读确认') && !reportDeskLocal.pending && !document.querySelector('#sql-editor-close').disabled);
    console.log(channel + ': SQL saved');
    await page.locator('#sql-editor-close').click();
    await page.locator('#xml-editor-open').click();
    await page.waitForFunction(() => document.querySelector('#xml-editor-input')?.value && !document.querySelector('#xml-editor-input').disabled);
    const xml = await page.locator('#xml-editor-input').inputValue();
    assert.ok(xml.includes('LOCAL_COPY_BROWSER_CHECK'));
    await page.locator('#xml-editor-input').fill(xml.replace('</ReportQueryInfo>','<!-- LOCAL_COPY_XML_CHECK --></ReportQueryInfo>'));
    await page.locator('#xml-editor-save').click();
    await page.waitForFunction(() => document.querySelector('#xml-editor-status').textContent.includes('本机文件已回读确认') && !reportDeskLocal.pending && !document.querySelector('#xml-editor-input').disabled);
    console.log(channel + ': XML saved');
    const verified = await page.evaluate(async () => {
      const digest = async bytes=>Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)), b=>b.toString(16).padStart(2,'0')).join('');
      const t=testLocal, query=t.fixtures[0].name;
      const bytes=await (await (await t.xml.getFileHandle(query)).getFile()).arrayBuffer(), text=new TextDecoder().decode(bytes);
      let batches=0,originalBackup=false;
      for await (const [,batch] of t.backup.entries()) {
        const config=await batch.getDirectoryHandle('Config'), xml=await config.getDirectoryHandle('Xml');
        const old=await (await (await xml.getFileHandle(query)).getFile()).arrayBuffer();
        batches++; if(await digest(old)===t.fixtures[0].hash) originalBackup=true;
      }
      const layout=await (await (await t.xml.getFileHandle(t.fixtures[1].name)).getFile()).arrayBuffer();
      return {sqlSaved:text.includes('LOCAL_COPY_BROWSER_CHECK'),xmlSaved:text.includes('LOCAL_COPY_XML_CHECK'),batches,originalBackup,layoutUnchanged:await digest(layout)===t.fixtures[1].hash,pending:reportDeskLocal.pending};
    });
    assert.deepEqual(verified,{sqlSaved:true,xmlSaved:true,batches:2,originalBackup:true,layoutUnchanged:true,pending:false});
    // External modification while editor is open must block a subsequent save.
    const latest=await page.locator('#xml-editor-input').inputValue();
    await page.locator('#xml-editor-input').fill(latest.replace('</ReportQueryInfo>','<!-- SHOULD_NOT_SAVE --></ReportQueryInfo>'));
    await page.evaluate(async () => {
      const target=await testLocal.xml.getFileHandle(testLocal.fixtures[0].name);
      const bytes=new Uint8Array(await (await target.getFile()).arrayBuffer());
      const extra=new TextEncoder().encode('<!-- EXTERNAL_COPY_CHANGE -->');
      const merged=new Uint8Array(bytes.length+extra.length);merged.set(bytes);merged.set(extra,bytes.length);
      const stream=await target.createWritable();await stream.write(merged);await stream.close();
    });
    await page.locator('#xml-editor-save').click();
    await page.waitForFunction(()=>reportDeskLocal.state.needsScan && document.querySelector('#notice[open]'));
    const conflict=await page.evaluate(async()=>{
      const bytes=await (await (await testLocal.xml.getFileHandle(testLocal.fixtures[0].name)).getFile()).arrayBuffer();
      const text=new TextDecoder().decode(bytes);
      return {externalPreserved:text.includes('EXTERNAL_COPY_CHANGE'),draftNotWritten:!text.includes('SHOULD_NOT_SAVE'),draftPreserved:document.querySelector('#xml-editor-input').value.includes('SHOULD_NOT_SAVE')};
    });
    assert.deepEqual(conflict,{externalPreserved:true,draftNotWritten:true,draftPreserved:true});
    await page.locator('[data-close="notice"]').click();
    await page.locator('#xml-editor-close').click(); await page.waitForFunction(() => !busy && !document.getElementById('xml-editor').open);
    const beforeCloseFile = await page.evaluate(async () => await (await (await testLocal.xml.getFileHandle(testLocal.fixtures[0].name)).getFile()).text());
    await page.locator('#web-import-open').click(); await page.locator('#local-directory-server').click(); await page.waitForFunction(() => !reportDeskLocal.active && !busy);
    assert.equal(temporaryXml().length,2,'closing the first browser directory releases only its temporary XML');
    assert.equal(await page.evaluate(async () => await (await (await testLocal.xml.getFileHandle(testLocal.fixtures[0].name)).getFile()).text()),beforeCloseFile);
    assert.deepEqual(await page.evaluate(() => [reportDeskLocal.state.source,reportDeskLocal.state.backup]),['HIS-COPY','BACKUPS']);
    assert.equal(await peer.evaluate(() => reportDeskLocal.active),true);
    const peerText = await peer.evaluate(async name => await (await (await testPeer.source.getFileHandle(name)).getFile()).text(),fixtures[0].name);
    assert.equal(hash(Buffer.from(peerText)),fixtures[0].hash,'the other browser original is untouched');
    await peer.locator('#web-import-open').click(); await peer.locator('#local-directory-inspect').click(); await peer.waitForFunction(() => !busy);
    assert.match(await peer.locator('#local-directory-status').innerText(),/没有待确认保存/);
    await peer.locator('#local-directory-server').click(); await peer.waitForFunction(() => !reportDeskLocal.active && !busy);
    assert.equal(temporaryXml().length,0,'closing both directories releases all uploaded XML');
    await page.locator('#source-close').click(); await page.screenshot({path:path.join(evidence,'closed-'+channel+'.png')});
    // Drop the actual server workspace while each editor contains an unsaved draft.
    // The next request must return 410 and preserve the browser's local recovery path.
    for (const editor of ['sql','xml']) {
      await page.locator('#local-start-open').click(); await page.locator('#local-directory-use').click();
      await page.waitForFunction(() => reportDeskLocal.active && !busy); await page.locator('#source-close').click();
      await page.locator(editor === 'sql' ? '#sql' : '#xml-editor-open').click();
      const input = page.locator('#' + editor + '-editor-input');
      await page.waitForFunction(id => !!document.getElementById(id).value && !busy,editor + '-editor-input');
      const draft = (await input.inputValue()) + (editor === 'sql' ? '\n-- RETAINED_AFTER_EXPIRY' : '\n<!-- RETAINED_AFTER_EXPIRY -->');
      await input.fill(draft);
      const released = await page.evaluate(async auth => {
        const response = await fetch('/api/tab-lifecycle',{method:'POST',headers:{'Content-Type':'application/json','X-ReportDesk-CSRF':auth.csrf},body:JSON.stringify({tabId:auth.tabId,args:{action:'release'}})});
        return response.json();
      },lifecycleAuth);
      assert.equal(released.ok,true); assert.equal(released.data.released,true);
      const lostStatus = await page.evaluate(() => reportDesk.call('localStatus'));
      assert.equal(lostStatus.ok,false);
      await page.waitForFunction(() => reportDeskLocal.state.workspaceLost && !reportDeskLocal.active);
      assert.equal(await input.inputValue(),draft);
      assert.equal(await page.locator('#' + editor + '-editor-save').isEnabled(),false);
      assert.equal(await page.locator('#' + editor + '-editor-save').innerText(),'需重新扫描后保存');
      assert.equal(await page.locator('#' + editor + '-editor-copy').isEnabled(),true);
      assert.equal(await page.locator('#' + editor + '-editor-close').isEnabled(),true);
      assert.equal(await page.locator('#' + editor + '-editor-local-settings').isVisible(),true);
      await input.fill(draft + '\n');
      assert.equal(await page.locator('#' + editor + '-editor-save').isEnabled(),false,'editing the retained draft cannot re-enable remote save');
      await page.screenshot({path:path.join(evidence,editor + '-expired-' + channel + '.png')});
      await page.locator('#' + editor + '-editor-close').click(); await page.waitForFunction(() => !busy);
      await page.locator('#local-start-open').click(); await page.locator('#local-directory-server').click();
      await page.waitForFunction(() => !reportDeskLocal.state.workspaceLost && !busy); await page.locator('#source-close').click();
      assert.equal(temporaryXml().length,0);
      assert.equal(await page.evaluate(async () => await (await (await testLocal.xml.getFileHandle(testLocal.fixtures[0].name)).getFile()).text()),beforeCloseFile);
    }
    assert.deepEqual(errors,[]);
    const result={channel,version:browser.version(),peerChannel,peerVersion:peerBrowser.version(),native,serverReportsEnabled:false,offlineQueryRejected:true,verified,conflict,isolatedBrowsers:true,serverRelease410PreservesSqlAndXmlDrafts:true,expiredEditorsDisableSaveKeepCopyClose:true,temporaryXmlAfterClose:temporaryXml().length,originalsAndHandlesPreserved:true,pageErrors:errors,evidenceTier:'Real offline WebRuntime and OPFS handles; native OS directory picker and prompts not tested'};
    results.push(result);console.log('PASS '+channel+' + '+peerChannel+': isolated browsers; offline query rejection; directory/DB draft preserved; SQL+XML readback/backups; external conflict; close releases temporary XML and preserves local files/handles.');
  } catch (error) {
    if(page) {
      console.error(await page.evaluate(()=>({open:[...document.querySelectorAll('dialog[open]')].map(n=>n.id),local:window.reportDeskLocal?.state,sql:document.querySelector('#sql-editor-status')?.textContent,xml:document.querySelector('#xml-editor-status')?.textContent,notice:document.querySelector('#notice-body')?.textContent})));
      await page.screenshot({path:path.join(evidence,'failed-'+channel+'.png')});
    }
    throw error;
  } finally {await peerBrowser.close();await browser.close();}
}
(async()=>{
  fs.mkdirSync(evidence,{recursive:true});
  for(const channel of ['chrome','msedge']) await run(channel);
  for(const fixture of fixtures) assert.equal(hash(fs.readFileSync(path.join(fixtureRoot,fixture.name))),fixture.hash);
  fs.writeFileSync(path.join(evidence,'browser-results.json'),JSON.stringify({results,fixtureOriginalsUnchanged:true,oracle:'NOT RUN',iis:'NOT RUN',browser360:'NOT RUN'},null,2));
})().catch(error=>{console.error(error);process.exitCode=1;});
