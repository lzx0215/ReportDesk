const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {pathToFileURL}=require('node:url');
const root=path.resolve(__dirname,'../..');
const {chromium}=require(path.join(root,'src/ReportDesk.Desktop/node_modules/playwright'));
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1400,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(pathToFileURL(path.join(root,'artifacts/audit/his-adaptation-20260911/ReportDesk-HIS适配清单.html')).href);
 assert.match(await page.locator('#summary').innerText(),/363 → 944/);assert.match(await page.locator('#summary').innerText(),/新识别拦截 21/);
 await page.selectOption('#mode','cleared');assert.equal(await page.locator('#count').textContent(),'602 项');
 await page.fill('#search','普通门诊处方记录');assert.equal(await page.locator('#count').textContent(),'1 项');assert.match(await page.locator('#rows').innerText(),/药剂科/);
 await page.screenshot({path:path.join(root,'artifacts/audit/his-adaptation-20260911/audit-preview.png')});
 await page.fill('#search','');await page.selectOption('#mode','pending');assert.equal(await page.locator('#count').textContent(),'340 项');
 await page.selectOption('#mode','excluded');assert.equal(await page.locator('#count').textContent(),'2403 项');assert.equal(errors.length,0);
 fs.writeFileSync(path.join(root,'artifacts/audit/his-adaptation-20260911/PASS.txt'),'PASS summary, before/after states, search, pending and excluded filters, no JavaScript errors.');console.log('PASS HIS audit UI');
}finally{await browser.close();}})().catch(e=>{console.error(e.message);process.exitCode=1;});
