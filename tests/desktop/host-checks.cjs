const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { Bridge } = require('../../src/ReportDesk.Desktop/bridge.cjs');
const root = path.resolve(__dirname, '../..');
const run = path.join(root,'artifacts/verification/desktop', 'host-' + Date.now());
fs.mkdirSync(run,{recursive:true});
const host = path.join(root,'artifacts/host/ReportDesk.Host.exe');
function start(name,config){const dir=path.join(run,name);fs.mkdirSync(dir,{recursive:true});if(config!==undefined)fs.writeFileSync(path.join(dir,'report-visibility.xml'),config);return new Bridge(host,dir,dir,true,()=>{},()=>{});}
function args(extra={}){return {reportId:'built-in-demo',source:0,values:{begin:'2026-01-01T00:00:00',end:'2026-06-30T00:00:00',department:''},...extra};}
(async()=>{
 const b=start('normal'); try {
 await b.ready;assert.deepEqual((await b.call('bootstrap')).reports,[]);await b.call('demo');let detail=await b.call('select',args());assert.equal(detail.parameters.length,3);
 let page=await b.call('query',args());assert.equal(page.total,362);assert.equal(page.rows.length,200);assert.equal(page.rows[0][2],'000000001');assert.equal(typeof page.rows[0][4],'string');
 const oldId=page.resultId;let next=await b.call('page',{resultId:oldId,offset:200});assert.equal(next.rows.length,162);
 page=await b.call('view',{resultId:oldId,filter:'演示科室 B',sort:4,descending:true});assert.equal(page.count,181);assert.equal(page.total,362);assert.equal(page.rows[0][4],'100.00');
 await assert.rejects(b.call('page',{resultId:oldId,revision:page.revision-1}),/视图已变化/);
 await b.call('export',{resultId:oldId,revision:page.revision,path:path.join(run,'filtered.xlsx')});assert.ok(fs.statSync(path.join(run,'filtered.xlsx')).size>100);
 await b.call('select',args());await assert.rejects(b.call('page',{resultId:oldId}),/失效/);
 await assert.rejects(b.call('query',args({values:{begin:'2026-09-11',end:'2026-09-10',department:'NEVER_LOG_THIS_PARAMETER'}})),/开始日期/);
 // Cancellation exercises the reader loop without a database or application timeout.
 const huge=b.call('query',args({values:{begin:'0001-01-01',end:'9999-12-31',department:''}}));
 // Request is ordered after query on the same stream; cancellation must stay serviceable.
 const rejected=assert.rejects(huge,e=>e.cancelled===true);await new Promise(resolve=>setImmediate(resolve));await b.cancel();await rejected;
 page=await b.call('query',args({values:{begin:'2026-09-10',end:'2026-09-10',department:''}}));assert.equal(page.total,2);
 await b.call('saveSettings',{name:'offline check',mode:0,host:'127.0.0.1',port:1521,service:'test',username:'readonly',password:'SYNTHETIC_SECRET',remember:true});
 assert.equal((await b.call('settings')).hasPassword,true);const saved=fs.readFileSync(path.join(run,'normal/connection.json'),'utf8');assert.ok(!saved.includes('SYNTHETIC_SECRET'));assert.ok(!saved.includes('NEVER_LOG_THIS_PARAMETER'));assert.ok(!saved.includes('000000001'));assert.ok(!fs.existsSync(path.join(run,'normal/catalog.json')));
 await assert.rejects(b.call('testConnection',{}),/禁止真实数据库/);
 const xml=path.join(run,'sample.xml');fs.writeFileSync(xml,'<ReportQueryInfo><List><List><Name>when</Name><Text>日期</Text><ControlType Type="FS.Core.UI.Report.Common.ControlType.DateTimeType,FS.Core.UI"><CustomFormat>yyyy-MM-dd</CustomFormat></ControlType></List></List><QueryDataSource><QueryDataSource><Name>main</Name><Sql>select &apos;&amp;when&apos; from dual</Sql><SqlType>MainReportUsing</SqlType></QueryDataSource></QueryDataSource></ReportQueryInfo>');
 const imported=await b.call('import',{path:xml});assert.equal(imported.imported,1);const real=imported.reports.find(x=>!x.demo);await assert.rejects(b.call('query',{reportId:real.id,source:0,values:{when:'2026-09-10'}}),/禁止真实数据库/);
 const logs=fs.readdirSync(path.join(run,'normal/logs')).map(f=>fs.readFileSync(path.join(run,'normal/logs',f),'utf8')).join('');assert.ok(!logs.includes('SYNTHETIC_SECRET'));assert.ok(!logs.includes('NEVER_LOG_THIS_PARAMETER'));
 } finally {b.close();}
 for(const cfg of ['<ReportVisibility mode="selected" />','<ReportVisibility mode="selected"><Report id="hidden" /></ReportVisibility>']){const limited=start('scope-'+Math.random(),cfg);try{await limited.ready;assert.equal((await limited.call('bootstrap')).demoVisible,false);await assert.rejects(limited.call('demo'),/显示清单/);await assert.rejects(limited.call('select',args()),/显示清单/);
   const hiddenImport=await limited.call('import',{path:path.join(run,'sample.xml')});assert.equal(hiddenImport.imported,1);assert.equal(hiddenImport.reports.length,0);
   const hiddenId=require('node:crypto').createHash('sha256').update(path.resolve(run,'sample.xml').toUpperCase()).digest('hex');
   await assert.rejects(limited.call('relatedFiles',{reportId:hiddenId}),/显示清单/);
 }finally{limited.close();}}
 const invalid=start('invalid','<ReportVisibility mode="broken" />');await assert.rejects(invalid.ready,/启动失败|后台/);invalid.close();
 fs.writeFileSync(path.join(run,'PASS.txt'),'PASS: paging/full export, typed values, stale handles, cancellation/retry, DPAPI, offline boundary, XML import, visibility and log privacy.\n');
 console.log('PASS host checks: '+run);
})().catch(e=>{console.error(e);process.exitCode=1;});
