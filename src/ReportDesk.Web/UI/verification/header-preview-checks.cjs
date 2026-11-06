'use strict';
// Real XML metadata emitted by the actual legacy-Core binary check. All requests
// are intercepted; the sole explicit-query response is an empty protocol envelope.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const web = path.resolve(__dirname, '..'), desktop = path.resolve(web, '../../ReportDesk.Desktop');
const { chromium } = require(path.join(desktop, 'node_modules/playwright'));
const cached = path.join(process.env.LOCALAPPDATA, 'ms-playwright/chromium-1217/chrome-win64/chrome.exe');
const [metadataFile, evidenceDirectory, siteDirectory] = process.argv.slice(2);
if (!metadataFile || !evidenceDirectory) throw new Error('Usage: node header-preview-checks.cjs details.json evidence-directory [patched-site]');
const details = JSON.parse(fs.readFileSync(metadataFile, 'utf8'));
const evidence = path.resolve(evidenceDirectory); fs.mkdirSync(evidence, { recursive: true });
const origin = 'http://127.0.0.1:49179';
const reports = details.map(d => ({id:d.id, name:d.name, status:d.status}));
let browser, checks = 0;
function check(actual, expected, name) { assert.deepEqual(actual, expected, name); checks++; console.log('PASS ' + name); }
async function scenario(prefix, admin, width) {
  const context = await browser.newContext({viewport:{width,height:960}}), requests = [], errors = [], jobs = new Map();
  let job = 0, unavailable = false, oldServer = false;
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) { errors.push('External request'); return route.abort(); }
    const local = url.pathname.slice(prefix.length);
    const json = data => route.fulfill({contentType:'application/json',body:JSON.stringify(data)});
    if (local === '/api/session') return json({csrf:'offline-test',admin,offline:true,serverReportsEnabled:true,idleMinutes:60,syncReady:false});
    if (local === '/api/tab-lifecycle') return json({ok:true,data:{alive:true,local:false,busy:false}});
    if (local === '/api/catalog-status') return json({version:'same'});
    if (local.startsWith('/api/tasks/')) return json({state:'succeeded',data:jobs.get(local.split('/').at(-1))});
    if (local.startsWith('/api/')) {
      const method = local.split('/').at(-1), args = request.postDataJSON().args; requests.push({method,args});
      let data;
      if (method === 'bootstrap') data = {reports,offline:true,warnings:[]};
      else if (method === 'list') data = reports;
      else if (method === 'select') {
        data = structuredClone(details.find(d=>d.id===args.reportId));
        if (unavailable) data.headerPreview = {title:data.name,columns:[],message:'模板暂不可读或表头结构尚未适配，暂不能预览表头；仍可按原流程查询。'};
        if (oldServer) delete data.headerPreview;
      } else if (method === 'definition') data = {textParameterNames:[]};
      else if (method === 'query') data = {columns:[{name:'数据库实际字段（空结果协议检查）',type:'String'}],rows:[],offset:0,count:0,total:0,resultId:'empty-contract'};
      else throw new Error('Unexpected API method: ' + method);
      const id = String(++job); jobs.set(id,data); return json({ok:true,jobId:id});
    }
    if (local === '/' || local === '/admin/') {
      const html = siteDirectory ? path.join(siteDirectory,'UI/index.html') : path.join(web,'index.html');
      return route.fulfill({contentType:'text/html',body:fs.readFileSync(html,'utf8').replaceAll('"/assets/','"'+prefix+'/assets/')});
    }
    if (local.startsWith('/assets/')) {
      const name = path.basename(local);
      const file = siteDirectory ? path.join(siteDirectory,'UI',name) : ['transport.js','web.js','web.css','xml-editor.js','local-directory.js'].includes(name) ? path.join(web,name) : path.join(desktop,'ui',name);
      if (fs.existsSync(file)) return route.fulfill({contentType:name.endsWith('.css')?'text/css':name.endsWith('.svg')?'image/svg+xml':'text/javascript',body:fs.readFileSync(file)});
    }
    return route.fulfill({status:404,body:''});
  });
  const page = await context.newPage(); page.on('pageerror',e=>errors.push(e.message));
  await page.goto(origin+prefix+(admin?'/admin/':'/'));
  await page.waitForFunction(()=>document.title.startsWith('ReportDesk Web') && !busy);
  const select = async d => { await page.locator('[data-report-id="'+d.id+'"]').click(); await page.waitForFunction(()=>!busy); };
  for (let i=0;i<details.length;i++) {
    const d = details[i]; await select(d);
    check(await page.locator('.header-preview caption').innerText(),d.headerPreview.title,d.name+' immediate title');
    check(await page.locator('.header-preview th').allTextContents(),d.headerPreview.columns.map(c=>c.label),d.name+' exact real template headers');
    check(await page.locator('.header-preview tbody tr').count(),0,d.name+' contains no fabricated rows');
    check(await page.locator('.header-preview th button').count(),0,d.name+' no sorting before query');
    check(await page.locator('#export').isDisabled(),true,d.name+' export remains disabled');
    check(await page.locator('#query').isEnabled(),true,d.name+' original query button available');
    check(requests.some(r=>['query','lookup','lookupAll'].includes(r.method)),false,d.name+' selection executes no SQL or option lookup');
    check(await page.locator('.header-preview-notice').innerText(),d.headerPreview.message,d.name+' candidate notice retained');
    const name = (siteDirectory?'legacy':'source')+'-'+(admin?'admin':'public')+'-'+width+'-'+i+'.png';
    await page.screenshot({path:path.join(evidence,name),fullPage:true});
  }
  // Switching the source must refresh metadata through the same select path.
  await page.locator('#summary-edit').click();
  const before = requests.filter(r=>r.method==='select').length;
  await page.locator('#source').dispatchEvent('change'); await page.waitForFunction(()=>!busy);
  check(requests.filter(r=>r.method==='select').length,before+1,'source switch refreshes selection');
  check(await page.locator('.header-preview caption').innerText(),details[2].name,'source switch does not clear new header');
  await page.locator('#conditions-dismiss').click();
  unavailable = true; await select(details[1]);
  check(await page.locator('.header-preview th').count(),0,'unsupported preview clears previous headers');
  check((await page.locator('.header-preview-notice').innerText()).includes('仍可按原流程查询'),true,'unsupported preview explains fallback');
  check(await page.locator('#query').isEnabled(),true,'unsupported preview leaves querying available');
  oldServer = true; await select(details[0]);
  check(await page.locator('.header-preview').count(),0,'older select response gracefully retains old empty state');
  oldServer = unavailable = false; await select(details[1]);
  await page.locator('#query').click(); await page.waitForFunction(()=>!busy);
  check(requests.filter(r=>r.method==='query').length,1,'only explicit query submits one query request');
  check(await page.locator('.header-preview').count(),0,'completed query uses original result renderer');
  check(await page.locator('#table-wrap th button').allTextContents(),['数据库实际字段（空结果协议检查）'],'actual result schema is not overwritten by template headers');
  check((await page.locator('#table-wrap').innerText()).includes('没有匹配的结果'),true,'original empty-result indication retained');
  await select(details[0]);
  check(await page.locator('.header-preview th').allTextContents(),details[0].headerPreview.columns.map(c=>c.label),'select after result restores selected template');
  check(await page.locator('#export').isDisabled(),true,'select after result clears result/export state');
  check(requests.filter(r=>r.method==='query').length,1,'reselection never automatically reruns SQL');
  check(errors,[],'no browser errors or external requests');
  await context.close();
}
(async()=>{
  browser=await chromium.launch({headless:true,...(fs.existsSync(cached)?{executablePath:cached}:{}),args:['--disable-background-networking']});
  await scenario('',false,1920); await scenario('/desk',true,1280);
  console.log('PASS '+checks+' browser assertions against '+(siteDirectory?'packaged legacy UI':'current source UI')+'; actual template metadata, intercepted HTTP, zero Oracle/result rows.');
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(async()=>{await browser?.close();});
