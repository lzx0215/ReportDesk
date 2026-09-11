const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {pathToFileURL}=require('node:url');
const root=path.resolve(__dirname,'../..'),{chromium}=require(path.join(root,'src/ReportDesk.Desktop/node_modules/playwright'));
const run=path.join(root,'artifacts/verification/desktop','lib-audit-'+Date.now());fs.mkdirSync(run,{recursive:true});
(async()=>{const browser=await chromium.launch({channel:'msedge',headless:true});try{
 const page=await browser.newPage({viewport:{width:1440,height:1000}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(pathToFileURL(path.resolve(process.argv[2]||path.join(root,'artifacts/audit/lib-current-path-20260911/review.html'))).href);
 assert.equal(await page.locator('#rows details').count(),22);assert.match(await page.locator('#totals').textContent(),/1262/);
 await page.locator('#rows summary').first().click();await page.screenshot({path:path.join(run,'remaining-reports.png')});
 await page.selectOption('#state','ready');assert.equal(await page.locator('#rows details').count(),1262);
 await page.selectOption('#state','all');await page.fill('#search','危重抢救');assert.ok(await page.locator('#rows details').count()>0);
 assert.deepEqual(errors,[]);fs.writeFileSync(path.join(run,'PASS.txt'),'PASS actual generated audit: 22 pending/1262 static-ready rows, expandable reasons and search; no page errors.');console.log('PASS '+run);
 }finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
