const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { once } = require('node:events');
const { Bridge } = require('../../src/ReportDesk.Desktop/bridge.cjs');
const root = path.resolve(__dirname,'../..');
const { _electron: electron } = require(path.join(root,'src/ReportDesk.Desktop/node_modules/playwright'));
const source = path.resolve(process.argv[2]);
const packed = process.argv[3] ? path.resolve(process.argv[3]) : null;
if (packed) assert.ok(packed.startsWith(path.join(root,'artifacts/verification/desktop')+path.sep));
const run = path.join(root,'artifacts/verification/desktop','his-'+Date.now());
const data = path.join(run,'local','ReportDesk'), config=path.join(run,'config');
fs.mkdirSync(data,{recursive:true}); fs.mkdirSync(config,{recursive:true});
const baseline=path.join(root,'artifacts/verification/desktop/update-1789111565036/installation with spaces/resources/host/ReportDesk.Host.exe');
const host=path.join(root,'artifacts/host/ReportDesk.Host.exe');
let b,desktop;
async function stop(){if(b){const done=once(b.process,'exit');b.close();await done;b=null;}}
(async()=>{
 const ordinary=path.join(source,'LIB','Config','Xml','普通门诊处方记录查询设置.xml');
 b=new Bridge(baseline,data,config,true,()=>{},()=>{});
 const old=await b.call('import',{path:ordinary});const oldReport=old.reports[0];assert.equal(oldReport.status,'待适配');
 await b.call('metadata',{reportId:oldReport.id,category:'未分类',notes:'preserve note',aliases:'legacy alias',verified:false});await b.call('favorite',{reportId:oldReport.id,favorite:true});await stop();
 b=new Bridge(host,data,config,true,()=>{},()=>{});
 // Old catalog fields are absent: listing and selecting must still work before reimport.
 assert.equal((await b.call('list')).length,1); await b.call('select',{reportId:oldReport.id});
 const imported=await b.call('import',{folder:true,path:source});
 assert.equal(imported.imported,1284);assert.equal(imported.pending,340);assert.equal(imported.incomplete.length,68);
 const record=imported.reports.find(r=>r.id===oldReport.id);assert.equal(record.category,'药剂科');assert.equal(record.notes,'preserve note');assert.equal(record.favorite,true);assert.equal(record.issues.length,0);
 const patient=imported.reports.find(r=>r.name==='门诊处方患者明细');assert.ok(patient);assert.equal(patient.issues.length,0);
 const details=await b.call('select',{reportId:patient.id,source:1});assert.deepEqual(details.parameters.map(p=>p.name).sort(),['唯一号','处方号'].sort());
 assert.ok(details.locations.some(l=>l.Candidate && l.Active===false));
 await assert.rejects(b.call('query',{reportId:record.id,source:0,values:{dtBeginTime:'2026-09-10',dtEndTime:'2026-09-11'}}),/禁止真实数据库/);
 const custom=`<ReportQueryInfo><List><List><Name>kind</Name><ControlType Type="FS.Core.UI.Report.Common.ControlType.ComboBoxType,FS.Core.UI"><QueryDataSource>Custom</QueryDataSource><IsAddAll>true</IsAddAll><AllValue><ID>1</ID><Name>入库</Name></AllValue><DefaultDataSource><DefaultDataSource><ID>002</ID><Name>出库</Name></DefaultDataSource></DefaultDataSource></ControlType></List><List><Name>flag</Name><ControlType Type="FS.Core.UI.Report.Common.ControlType.CheckBoxType,FS.Core.UI"><DefaultDataSource>false</DefaultDataSource></ControlType></List></List><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select '&amp;kind','&amp;flag' from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>`;
 const fixture=path.join(run,'参数适配验证查询设置.xml');fs.writeFileSync(fixture,custom);const sample=(await b.call('import',{path:fixture})).reports.find(r=>r.name==='参数适配验证');
 const params=await b.call('select',{reportId:sample.id});assert.equal(params.parameters[0].options[0].Value,'002');
 await assert.rejects(b.call('query',{reportId:sample.id,values:{kind:'出库',flag:'False'}}),/配置中的选项/);
 await assert.rejects(b.call('query',{reportId:sample.id,values:{kind:'002',flag:'False'}}),/禁止真实数据库/);
 const categoryCandidate=imported.reports.find(r=>r.category.startsWith('候选 · '));assert.ok(categoryCandidate);
 fs.writeFileSync(path.join(run,'counts.json'),JSON.stringify({reports:imported.imported,pending:imported.pending,ready:imported.imported-imported.pending,candidateReports:imported.reports.filter(r=>r.locations.some(l=>l.Candidate)).length,classified:imported.reports.filter(r=>r.category!=='未分类').length},null,2));
 await stop();
 const env={...process.env,REPORTDESK_TEST:'1',REPORTDESK_TEST_DATA:data,REPORTDESK_TEST_CONFIG:config};delete env.ELECTRON_RUN_AS_NODE;
 if(packed){delete env.REPORTDESK_TEST;delete env.REPORTDESK_TEST_DATA;delete env.REPORTDESK_TEST_CONFIG;env.LOCALAPPDATA=path.join(run,'local');}
 desktop=await electron.launch(packed?{executablePath:packed,env}:{args:[path.join(root,'src/ReportDesk.Desktop')],env});
 const page=await desktop.firstWindow();page.setDefaultTimeout(30000);await page.waitForFunction(()=>document.querySelector('#operation-status').textContent==='准备就绪。');
 await page.fill('#report-search','普通门诊处方记录');await page.waitForFunction(()=>document.querySelector('#report-title').textContent==='普通门诊处方记录');assert.equal(await page.locator('#query').isDisabled(),false);assert.match(await page.locator('#report-locations').innerText(),/药剂科/);assert.equal(await page.locator('#parameters input').count(),2);
 await page.screenshot({path:path.join(run,'ordinary-ready.png')});
 await page.fill('#report-search','门诊处方患者明细');await page.waitForFunction(()=>document.querySelector('#report-title').textContent==='门诊处方患者明细');assert.equal(await page.locator('#query').isDisabled(),false);assert.match(await page.locator('#report-locations').innerText(),/菜单已停用/);await page.selectOption('#source','1');await page.waitForFunction(()=>document.querySelectorAll('#parameters input').length===2);await page.screenshot({path:path.join(run,'patient-detail-ready.png')});
 await page.fill('#report-search','参数适配验证');await page.waitForFunction(()=>document.querySelector('#report-title').textContent==='参数适配验证');assert.equal(await page.locator('[data-parameter="kind"] option').allTextContents().then(v=>v.join('|')),'请选择|入库|出库');await page.selectOption('[data-parameter="kind"]','002');assert.equal(await page.locator('[data-parameter="flag"]').isChecked(),false);await page.check('[data-parameter="flag"]');
 if(!packed){await page.click('#query');await page.waitForFunction(()=>document.querySelector('#notice-body').textContent.includes('禁止真实数据库'));}
 assert.equal(await page.locator('#progress').evaluate(el=>getComputedStyle(el).height),'10px');
 await desktop.close();desktop=null;fs.writeFileSync(path.join(run,'PASS.txt'),'PASS old catalog migration/reimport, 1284 definitions, 944 ready, 340 pending, authentic two prescription UIs, static options, checkbox, inactive menu candidates, binding rejection, original progress bar; Oracle NOT RUN.');console.log('PASS HIS materials: '+run);
})().catch(e=>{console.error(e);process.exitCode=1;}).finally(async()=>{await stop();if(desktop)await desktop.close();});
