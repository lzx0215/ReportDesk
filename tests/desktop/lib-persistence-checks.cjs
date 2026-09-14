// Source recovery checks using copies of an actual LIB XML; no database/results.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {once}=require('node:events'),{Bridge}=require('../../src/ReportDesk.Desktop/bridge.cjs');
const root=path.resolve(__dirname,'../..'),run=path.join(root,'artifacts/verification/desktop','lib-persistence-'+Date.now());
const original=process.argv[2]||'D:/系统知识库/00_Inbox/yljhis/LIB/LIB/Config/Xml/普通门诊处方记录查询设置.xml';
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
fs.mkdirSync(run,{recursive:true});const originalHash=hash(original);
const data=path.join(run,'data'),folder=path.join(run,'source'),other=path.join(run,'second-source');
fs.mkdirSync(folder);fs.mkdirSync(other);const xml=path.join(folder,path.basename(original)),second=path.join(other,path.basename(original));
fs.copyFileSync(original,xml);fs.copyFileSync(original,second);
const start=()=>new Bridge(path.join(root,'artifacts/host/ReportDesk.Host.exe'),data,run,true,()=>{},()=>{});
let b;async function stop(){const done=once(b.process,'exit');b.close();await done;b=null;}
(async()=>{
 b=start();await b.ready;assert.equal((await b.call('bootstrap')).reports.length,0);
 const first=(await b.call('import',{path:folder,folder:true})).reports[0];
 await b.call('import',{path:folder,folder:true});await b.call('import',{path:second});
 const settingsPath=path.join(data,'import-sources.json'),saved=fs.readFileSync(settingsPath,'utf8');
 assert.equal(JSON.parse(saved).length,2);assert.doesNotMatch(saved,/Sql|Queries|password|values|rows/);
 await stop();b=start();await b.ready;let boot=await b.call('bootstrap');assert.equal(boot.reports.length,2);assert.deepEqual(boot.warnings,[]);
 assert.equal((await b.call('bootstrap')).reports.length,2);
 await assert.rejects(b.call('page',{resultId:'old-result'}),/失效/);await stop();
 // Only move the isolated test copy, and restore it before verifying original hashes.
 const missing=path.join(run,'temporarily-unavailable');fs.renameSync(folder,missing);
 b=start();await b.ready;boot=await b.call('bootstrap');assert.equal(boot.reports.length,1);assert.ok(boot.warnings.some(w=>w.includes(folder)));
 assert.equal(fs.readFileSync(settingsPath,'utf8'),saved);await stop();fs.renameSync(missing,folder);
 fs.writeFileSync(path.join(run,'report-visibility.xml'),`<ReportVisibility mode="selected"><Report id="${first.id}" /></ReportVisibility>`);
 b=start();await b.ready;boot=await b.call('bootstrap');assert.equal(boot.reports.length,1);assert.equal(boot.reports[0].id,first.id);await stop();
 // Corrupt preferences must remain untouched and be reported, not silently reset.
 fs.writeFileSync(settingsPath,'BROKEN-TEST-CONFIG');b=start();await b.ready;boot=await b.call('bootstrap');assert.equal(boot.reports.length,0);assert.ok(boot.warnings.length>0);
 await assert.rejects(b.call('import',{path:folder,folder:true}));assert.equal(fs.readFileSync(settingsPath,'utf8'),'BROKEN-TEST-CONFIG');await stop();
 assert.equal(hash(original),originalHash);assert.equal(hash(xml),originalHash);
 fs.writeFileSync(path.join(run,'PASS.txt'),'PASS actual LIB source path persistence, duplicate import, multiple sources, restart, missing source recovery, visibility filtering, corrupt preferences preserved, original XML hashes. Oracle NOT RUN.');
 console.log('PASS '+run);
})().catch(async e=>{console.error(e);if(b)await stop();process.exitCode=1;});
