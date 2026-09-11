// Reuses the installed ASAR utility. Does not repackage or download Electron.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { pathToFileURL } = require('node:url');
const root = path.resolve(__dirname, '..');
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
(async () => {
  const baseline = path.resolve(process.argv[2] || path.join(root,'artifacts/desktop/ReportDesk-win32-x64'));
  const output = path.resolve(process.argv[3] || path.join(root,'artifacts/updates','ReportDesk-0.2.0-xml-import-'+Date.now()));
  if (fs.existsSync(output)) throw new Error('Output must be a new directory.');
  const source = path.join(root,'src/ReportDesk.Desktop');
  const packageInfo = JSON.parse(fs.readFileSync(path.join(source,'package.json'),'utf8'));
  const asar = await import(pathToFileURL(require.resolve('@electron/asar', { paths: [source] })).href);
  const baseInfo = JSON.parse(asar.extractFile(path.join(baseline,'resources/app.asar'),'package.json').toString());
  const runtimeVersion = fs.readFileSync(path.join(baseline,'version'),'utf8').trim();
  if (baseInfo.version !== packageInfo.version || runtimeVersion !== packageInfo.devDependencies.electron) throw new Error('Runtime/version changes need a full package.');
  const files = ['resources/app.asar','resources/host/ReportDesk.Host.exe','resources/host/ReportDesk.Core.dll'];
  for (const relative of files) if (!fs.existsSync(path.join(baseline,relative))) throw new Error('Baseline file missing: '+relative);
  // Only these fixed files are executable app sources; no node_modules, secrets or build tooling.
  const staged = path.join(output,'build-source'); fs.mkdirSync(staged,{recursive:true});
  for (const relative of ['package.json','main.cjs','preload.cjs','bridge.cjs','ui/index.html','ui/styles.css','ui/execution.css','ui/renderer.js']) {
    fs.mkdirSync(path.dirname(path.join(staged,relative)),{recursive:true}); fs.copyFileSync(path.join(source,relative),path.join(staged,relative));
  }
  const payload = path.join(output,'payload'); fs.mkdirSync(path.join(payload,'resources/host'),{recursive:true});
  await asar.createPackage(staged,path.join(payload,'resources/app.asar'));
  for (const name of ['ReportDesk.Host.exe','ReportDesk.Core.dll']) fs.copyFileSync(path.join(root,'artifacts/host',name),path.join(payload,'resources/host',name));
  // Existing runtime and host dependencies must match. No user-owned configuration is included.
  const dependencies = directory => fs.readdirSync(directory).filter(n => /\.(dll|config)$/i.test(n) && n !== 'ReportDesk.Core.dll').sort();
  const baseDependencies = dependencies(path.join(baseline,'resources/host'));
  if (JSON.stringify(baseDependencies) !== JSON.stringify(dependencies(path.join(root,'artifacts/host')))) throw new Error('Host dependency set changed; use a full package.');
  const required = ['ReportDesk.exe','version', ...baseDependencies.map(n=>'resources/host/'+n)];
  for (const relative of required.filter(p=>p.startsWith('resources/host/'))) {
    if (hash(path.join(baseline,relative)) !== hash(path.join(root,'artifacts/host',path.basename(relative)))) throw new Error('Host dependency/config changed; use a full package: '+relative);
  }
  const manifest = { schema:1, updateId:path.basename(output), version:packageInfo.version, electron:packageInfo.devDependencies.electron, architecture:'x64',
    files:files.map(relative=>({path:relative,baseSha256:hash(path.join(baseline,relative)),sha256:hash(path.join(payload,relative))})),
    requires:required.map(relative=>({path:relative,sha256:hash(path.join(baseline,relative))})) };
  fs.writeFileSync(path.join(output,'manifest.json'),JSON.stringify(manifest,null,2));
  fs.copyFileSync(path.join(root,'scripts/Apply-Update.ps1'),path.join(output,'Apply-Update.ps1'));
  fs.copyFileSync(path.join(root,'docs/OfflineUpdate.md'),path.join(output,'README-Update.md'));
  // Staging is deliberately outside the distributable ZIP (build-update.ps1 only lists delivery files).
  console.log(output);
})().catch(e=>{console.error(e.message);process.exitCode=1;});
