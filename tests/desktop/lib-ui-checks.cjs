const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../..');
const {_electron:electron}=require(path.join(root,'src/ReportDesk.Desktop/node_modules/playwright'));
const run=path.join(root,'artifacts/verification/desktop','lib-ui-'+Date.now());fs.mkdirSync(run,{recursive:true});
const source=process.argv[2]||'D:/系统知识库/00_Inbox/yljhis/LIB/LIB';let app;
(async()=>{
 const env={...process.env,LOCALAPPDATA:path.join(run,'local-data'),REPORTDESK_TEST:'1',REPORTDESK_TEST_DATA:path.join(run,'data'),REPORTDESK_TEST_CONFIG:run};delete env.ELECTRON_RUN_AS_NODE;
 app=await electron.launch(process.argv[3]?{executablePath:path.resolve(process.argv[3]),env}:{args:[path.join(root,'src/ReportDesk.Desktop')],env});
 if(process.argv[3])await app.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows().forEach(w=>w.hide()));
 const p=await app.firstWindow();p.setDefaultTimeout(30000);const errors=[];p.on('pageerror',e=>errors.push(e.message));
 await p.waitForFunction(()=>document.querySelector('#operation-status').textContent==='准备就绪。');
 await app.evaluate(({dialog},folder)=>{dialog.showOpenDialog=async()=>({canceled:false,filePaths:[folder]});},source);
 await p.click('#import-folder');await p.waitForFunction(()=>document.querySelectorAll('.report-item').length===1284);
 if(await p.locator('#notice[open]').count()) await p.click('[data-close="notice"]');
 assert.equal(await p.locator('.sidebar [data-mode],.sidebar select').count(),0);
 assert.equal(await p.locator('.sidebar input').count(),1);
 await p.fill('#report-search','门诊处方');await p.waitForTimeout(650);assert.ok(await p.locator('.report-item').count()>1);
 await p.locator('.report-item').filter({hasText:'普通门诊处方记录'}).click();
 await p.waitForFunction(()=>document.querySelector('#report-title').textContent==='普通门诊处方记录');
 assert.equal(await p.locator('#query').isDisabled(),false);
 await p.screenshot({path:path.join(run,'prescription.png')});
 await p.fill('#report-search','(伽马刀、直加)工作量');await p.waitForTimeout(650);await p.locator('.report-item').first().click();
 await p.waitForFunction(()=>!document.querySelector('#query').disabled);
 assert.ok(await p.locator('[data-parameter][readonly]').count()>0);
 await p.screenshot({path:path.join(run,'cross-and-tree.png')});
 await p.fill('#report-search','天河区公费医疗SQL设置');await p.waitForTimeout(650);await p.locator('.report-item').first().click();
 await p.waitForFunction(()=>!document.querySelector('#query').disabled);
 assert.ok(await p.locator('#source option').count()>0);
 assert.doesNotMatch(await p.locator('#source').textContent(),/dtOper/);
 assert.ok(await p.locator('#parameters button').count()>0);
 await p.fill('#report-search','患者费用查询SQL设置');await p.waitForTimeout(650);await p.locator('.report-item').first().click();
 await p.waitForFunction(()=>!document.querySelector('#query').disabled);
 assert.match(await p.locator('#parameters').textContent(),/RegisterID.*非住院号/);
 const progress=await p.locator('.track').evaluate(n=>({height:getComputedStyle(n).height,radius:getComputedStyle(n).borderRadius}));assert.equal(progress.height,'10px');assert.notEqual(progress.radius,'0px');
 await p.fill('#report-search','');await p.waitForTimeout(650);assert.equal(await p.locator('.report-item').count(),1284);
 await p.screenshot({path:path.join(run,'all-reports.png')});assert.deepEqual(errors,[]);
 await app.close();app=null;
 app=await electron.launch(process.argv[3]?{executablePath:path.resolve(process.argv[3]),env}:{args:[path.join(root,'src/ReportDesk.Desktop')],env});
 if(process.argv[3])await app.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows().forEach(w=>w.hide()));
 const reopened=await app.firstWindow();reopened.setDefaultTimeout(30000);
 reopened.on('pageerror',e=>errors.push(e.message));
 await reopened.waitForFunction(()=>document.querySelector('#operation-status').textContent==='准备就绪。'&&document.querySelectorAll('.report-item').length===1284);
 // Isolate machine TNS discovery and inject UI-only login success. Actual Host
 // success/failure persistence is covered by ReportDesk.Host.Checks without Oracle.
 await app.evaluate(({ipcMain})=>{
   const handler=ipcMain._invokeHandlers.get('reportdesk:call');ipcMain.removeHandler('reportdesk:call');
   ipcMain.handle('reportdesk:call',async(event,method,args)=>{
     if(method==='discoverTns')return {ok:true,data:[]};
     if(method==='testConnection'){
       const saved=await handler(event,'saveSettings',args);
       return saved.ok?{ok:true,data:{version:'OFFLINE-INJECTED',settings:saved.data}}:saved;
     }
     return handler(event,method,args);
   });
 });
 await reopened.click('#settings-open');await reopened.waitForFunction(()=>document.querySelector('#connection').open);
 assert.equal(await reopened.locator('#conn-remember').isChecked(),true);
 assert.equal(await reopened.locator('#test-connection').textContent(),'连接并保存');
 await reopened.fill('#conn-name','Offline UI persistence');await reopened.fill('#conn-host','localhost');
 await reopened.fill('#conn-service','offline');await reopened.fill('#conn-user','offline_reader');
 await reopened.fill('#conn-password','SYNTHETIC_UI_PERSISTENCE');await reopened.click('#test-connection');
 await reopened.waitForFunction(()=>document.querySelector('#test-status').textContent.includes('OFFLINE-INJECTED'));
 assert.equal(await reopened.locator('#conn-password').inputValue(),'');
 assert.equal(await reopened.locator('#conn-keep').isChecked(),true);
 assert.equal(await reopened.locator('#conn-password').isDisabled(),true);
 await reopened.click('[data-close="connection"]');
 await reopened.screenshot({path:path.join(run,'restarted-restored-reports.png')});assert.deepEqual(errors,[]);
 await app.close();app=null;
 const dataDir=process.argv[3]?path.join(env.LOCALAPPDATA,'ReportDesk'):env.REPORTDESK_TEST_DATA;
 assert.ok(!fs.readFileSync(path.join(dataDir,'connection.json'),'utf8').includes('SYNTHETIC_UI_PERSISTENCE'));
 app=await electron.launch(process.argv[3]?{executablePath:path.resolve(process.argv[3]),env}:{args:[path.join(root,'src/ReportDesk.Desktop')],env});
 if(process.argv[3])await app.evaluate(({BrowserWindow})=>BrowserWindow.getAllWindows().forEach(w=>w.hide()));
 const third=await app.firstWindow();
 await third.waitForFunction(()=>document.querySelector('#operation-status').textContent==='准备就绪。'&&document.querySelectorAll('.report-item').length===1284);
 const savedSettings=await third.evaluate(()=>window.reportDesk.call('settings'));
 assert.equal(savedSettings.data.hasPassword,true);assert.equal(savedSettings.data.name,'Offline UI persistence');
 await app.close();app=null;
 fs.writeFileSync(path.join(run,'PASS.txt'),'PASS real Electron + actual LIB: full directory import, restart auto-restoration, search-only sidebar, prescription/cross/tree parameters, original progress style; UI-only injected login with atomic save, cleared password input and encrypted restart reuse. No fabricated report/result data. Oracle NOT RUN.');console.log('PASS '+run);
})().catch(async e=>{console.error(e);if(app)await app.close();process.exitCode=1;});
