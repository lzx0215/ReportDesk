// Offline update/rollback + external report import. No invented report/result data.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {execFileSync}=require('node:child_process'),{once}=require('node:events');
const {Bridge}=require('../../src/ReportDesk.Desktop/bridge.cjs');
const root=path.resolve(__dirname,'../..'),update=path.resolve(process.argv[2]),baseline=path.resolve(process.argv[3]);
const run=path.join(root,'artifacts/verification/desktop','report-update-'+Date.now()),installed=path.join(run,'installation with spaces');
const manifest=JSON.parse(fs.readFileSync(path.join(update,'manifest.json'),'utf8'));
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex').toUpperCase();
const apply=extra=>execFileSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(update,'Apply-Update.ps1'),'-TargetDirectory',installed,...(extra||[])],{encoding:'utf8',stdio:['ignore','pipe','pipe']});
function verify(key){for(const f of manifest.files)assert.equal(hash(path.join(installed,f.path)),f[key]);}
let bridge;
(async()=>{
 fs.mkdirSync(run,{recursive:true});fs.cpSync(baseline,installed,{recursive:true});
 assert.equal(manifest.files.length,3);assert.match(apply(['-VerifyOnly']),/PASS update verification/);verify('baseSha256');
 const applied=apply();assert.match(applied,/PASS update/);verify('sha256');assert.match(apply(),/already installed/);
 const backup=applied.match(/Backup: ([^\r\n]+)/)[1].trim();
 bridge=new Bridge(path.join(installed,'resources/host/ReportDesk.Host.exe'),path.join(run,'data'),installed,true,()=>{},()=>{});await bridge.ready;
 const source=process.argv[4];if(!source) throw new Error('Usage: node tests/desktop/report-update-checks.cjs <update-dir> <baseline-dir> <report-directory>');
 const imported=await bridge.call('import',{path:source,folder:true});assert.equal(imported.errors.length,0);
 for(const report of imported.reports){const detail=await bridge.call('select',{reportId:report.id});assert.equal(detail.id,report.id);assert.equal(detail.demo,false);}
 const stop=once(bridge.process,'exit');bridge.close();await stop;bridge=null;
 assert.match(apply(['-Rollback','-BackupDirectory',backup,'-VerifyOnly']),/PASS rollback verification/);
 assert.match(apply(['-Rollback','-BackupDirectory',backup]),/PASS rollback/);verify('baseSha256');
 assert.match(apply(),/PASS update/);verify('sha256');
 for(const f of manifest.requires)assert.equal(hash(path.join(installed,f.path)),f.sha256);
 assert.ok(!fs.existsSync(path.join(run,'data/catalog.json')));
 fs.writeFileSync(path.join(run,'PASS.txt'),'PASS exact baseline verification; three-file apply, idempotence, packaged Host external report import, all report details, rollback/reapply and dependency hashes. No Oracle or fabricated report/result data.\n');
 fs.writeFileSync(path.join(run,'result.json'),JSON.stringify({installed,update,baseline,reports:imported.reports.length,pending:imported.reports.filter(r=>r.issues.length>0).length,incomplete:imported.incomplete.length,oracle:'NOT RUN'},null,2));console.log('PASS '+run);
})().catch(e=>{console.error(e);if(bridge)bridge.close();process.exitCode=1;});
