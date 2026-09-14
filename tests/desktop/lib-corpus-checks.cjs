// Real LIB definitions only. No demo reports, generated SQL or fabricated result rows.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {once}=require('node:events');
const {Bridge}=require('../../src/ReportDesk.Desktop/bridge.cjs');
const root=path.resolve(__dirname,'../..'),source=process.argv[2]||'D:/系统知识库/00_Inbox/yljhis/LIB/LIB';
const run=path.join(root,'artifacts/verification/desktop','lib-corpus-'+Date.now());fs.mkdirSync(run,{recursive:true});
const before=JSON.parse(fs.readFileSync(path.join(root,'artifacts/audit/session-20260911/audit.json'),'utf8').replace(/^\uFEFF/,''));
const hash=file=>crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const currentPath=r=>path.join(source,path.relative(before.source,r.path));
let bridge;
(async()=>{
 for(const r of before.rows) assert.equal(hash(currentPath(r)),r.hash,'Copied XML differs from baseline: '+r.name);
 bridge=new Bridge(path.join(root,'artifacts/host/ReportDesk.Host.exe'),run,run,true,()=>{},()=>{});await bridge.ready;
 const imported=await bridge.call('import',{path:source,folder:true});assert.equal(imported.errors.length,0);assert.equal(imported.reports.length,before.reports);assert.equal(imported.incomplete.length,before.incomplete);
 let sources=0,ready=0,blocked=0,trees=0,registers=0;const rows=[];
 for(const report of imported.reports){
   const first=await bridge.call('select',{reportId:report.id});
   assert.ok(!first.demo);assert.ok(first.sources.every(s=>!s.name.includes('ConditionUsing')));
   const statuses=[];
   for(const source of first.sources){
     const detail=await bridge.call('select',{reportId:report.id,source:source.index});sources++;
     assert.deepEqual(detail.selectedIssues,source.issues);
     const parameterNames=detail.parameters.map(p=>p.name);assert.equal(new Set(parameterNames).size,parameterNames.length);
     if(detail.selectedIssues.length) blocked++;else ready++;
     trees+=detail.parameters.filter(p=>p.treeSelect).length;registers+=detail.parameters.filter(p=>p.kind==='RegisterIdType').length;
     statuses.push({source:source.index,blocked:detail.selectedIssues.length>0});
   }
   rows.push({name:report.name,status:report.status,sources:statuses});
   if(rows.length%200===0) console.log('Checked real LIB reports: '+rows.length);
 }
 assert.ok(trees>0&&registers>0);assert.ok(!fs.existsSync(path.join(run,'catalog.json')));
 for(const r of before.rows) assert.equal(hash(currentPath(r)),r.hash);
 const stop=once(bridge.process,'exit');bridge.close();await stop;bridge=null;
 bridge=new Bridge(path.join(root,'artifacts/host/ReportDesk.Host.exe'),run,run,true,()=>{},()=>{});await bridge.ready;
 const restored=await bridge.call('bootstrap');assert.deepEqual(restored.reports,imported.reports);assert.deepEqual(restored.warnings,[]);
 assert.deepEqual(JSON.parse(fs.readFileSync(path.join(run,'import-sources.json'),'utf8')),[{Folder:true,Path:path.resolve(source)}]);
 const restartedStop=once(bridge.process,'exit');bridge.close();await restartedStop;bridge=null;
 const result={source,reports:rows.length,sources,readySources:ready,blockedSources:blocked,treeParameterOccurrences:trees,registerParameterOccurrences:registers,originalXmlHashesVerified:before.rows.length,oracle:'NOT RUN',computedResultComparison:'NOT RUN - LIB has definitions, no query result tables',rows};
 fs.writeFileSync(path.join(run,'results.json'),JSON.stringify(result,null,2));fs.writeFileSync(path.join(run,'PASS.txt'),'PASS real LIB import/classification, every report/source detail, parameter metadata, original source hashes and no catalog persistence. Oracle/results NOT RUN.');
 console.log(JSON.stringify({...result,rows:undefined,run}));
})().catch(async e=>{console.error(e);if(bridge){bridge.close();}process.exitCode=1;});
