'use strict';
// Exercise the production browser classifier with retained HIS copies and real
// Chromium XML parsing. No source XML is written and no database is contacted.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { chromium } = require(path.resolve(__dirname, '../../../ReportDesk.Desktop/node_modules/playwright'));
const root = path.resolve(__dirname, '../../../..');
const fixture = path.join(root, 'artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml');
const paths = ['Account/门诊账户缴款书报表设置.xml', 'Expense/In/住院按金月报表报表设置.xml', 'Expense/In/住院按金月明细报表报表设置.xml'];
const files = paths.map(name => ({ name, bytes: fs.readFileSync(path.join(fixture, name)) }));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const initial = files.map(file => hash(file.bytes));
const script = fs.readFileSync(path.join(__dirname, '../local-directory.js'), 'utf8');
const start = script.indexOf('  function decodeXml(bytes) {');
const end = script.indexOf('  function reportItem(', start);
assert.ok(start >= 0 && end > start, 'production classifier found');
const source = script.slice(start, end);

(async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  try {
    const page = await browser.newPage();
    const actual = await page.evaluate(({ source, fixtures }) => {
      const { decodeXml, inspectXml } = new Function(source + '\nreturn { decodeXml, inspectXml };')();
      const parse = input => { try { return inspectXml(input).kind; } catch { return 'rejected'; } };
      const layouts = fixtures.map(encoded => {
        const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
        const xml = decodeXml(bytes);
        return { completeXmlRejected: !!new DOMParser().parseFromString(xml, 'application/xml').getElementsByTagName('parsererror').length, kind: parse(xml) };
      });
      const cases = [
        ['query', '<ReportQueryInfo><QueryDataSource><QueryDataSource><Sql>select 1</Sql></QueryDataSource></QueryDataSource></ReportQueryInfo>'],
        ['rejected', '<ReportQueryInfo><QueryDataSource><QueryDataSource><Sql>select &#x0;</Sql></QueryDataSource></QueryDataSource></ReportQueryInfo>'],
        ['layout', '<Spread class="FarPoint.Win.Spread.FpSpread"><x>&#x0;</x></Spread>'],
        ['layout', '<?xml version="1.0"?><!-- test > --><s:Spread xmlns:s="urn:layout" class="FarPoint.Win.Spread.FpSpread" hint="a > b"><x>&#x0;</x></s:Spread>'],
        ['rejected', '<s:Spread class="FarPoint.Win.Spread.FpSpread"><x>&#x0;</x></s:Spread>'],
        ['rejected', '<Spread class="FarPoint.Win.Spread.FpSpread" class="FarPoint.Win.Spread.FpSpread"><x>&#x0;</x></Spread>'],
        ['rejected', '<!DOCTYPE Spread [<!ENTITY x SYSTEM "file:///secret">]><Spread class="FarPoint.Win.Spread.FpSpread">&x;</Spread>'],
        ['rejected', '<!-- <Spread class="FarPoint.Win.Spread.FpSpread"> --><Broken><x>&#x0;</x></Broken>'],
        ['rejected', '<Spread class="Other"><x>&#x0;</x></Spread>'],
        ['rejected', '<?xml broken?><Spread class="FarPoint.Win.Spread.FpSpread"><x>&#x0;</x></Spread>'],
      ].map(([expected, xml]) => ({ expected, actual: parse(xml) }));
      return { layouts, cases };
    }, { source, fixtures: files.map(file => file.bytes.toString('base64')) });
    for (const result of actual.layouts) assert.deepEqual(result, { completeXmlRejected: true, kind: 'layout' });
    for (const result of actual.cases) assert.equal(result.actual, result.expected);
    assert.deepEqual(files.map(file => hash(fs.readFileSync(path.join(fixture, file.name)))), initial, 'source XML unchanged');
    console.log('PASS: three retained FarPoint layouts, complete query parsing, strict root/prolog and DTD checks in Chrome; original XML bytes unchanged.');
    if (process.argv.includes('--full')) {
      const tree = path.join(root, 'artifacts/verification/sql-export-20261030/final-binary-check/reports');
      const walk = directory => fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const name = path.join(directory, entry.name);
        return entry.isDirectory() ? walk(name) : entry.isFile() && /\.xml$/i.test(entry.name) ? [name] : [];
      });
      const all = walk(tree), counts = { layout: 0, query: 0, other: 0, rejected: 0, recoveredLayout: 0 };
      await page.evaluate(source => { window.reportDeskClassifier = new Function(source + '\nreturn { decodeXml, inspectXml };')(); }, source);
      for (let offset = 0; offset < all.length; offset += 25) {
        const batch = all.slice(offset, offset + 25).map(name => fs.readFileSync(name).toString('base64'));
        const result = await page.evaluate(batch => batch.map(encoded => {
          try {
            const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
            const text = window.reportDeskClassifier.decodeXml(bytes);
            const kind = window.reportDeskClassifier.inspectXml(text).kind;
            const malformed = !!new DOMParser().parseFromString(text, 'application/xml').getElementsByTagName('parsererror').length;
            return { kind, recovered: kind === 'layout' && malformed };
          } catch { return { kind: 'rejected', recovered: false }; }
        }), batch);
        for (const item of result) { counts[item.kind]++; if (item.recovered) counts.recoveredLayout++; }
      }
      assert.equal(all.length, 3687, 'retained sample size');
      assert.equal(counts.layout, 1570, 'matches old Core root classification');
      assert.equal(counts.recoveredLayout, 134, 'retained FarPoint invalid-body files classified by validated root');
      assert.equal(counts.rejected, 0, 'no retained XML remains unrecognized');
      console.log('PASS: full retained sample ' + all.length + ' XML; counts ' + JSON.stringify(counts));
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
