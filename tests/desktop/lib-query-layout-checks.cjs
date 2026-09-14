// Compatibility entry point: layout now belongs to the independent conditions view.
// Usage retains <baseline-exe> <updated-exe>; old fixed-width assertions are retired.
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const result = spawnSync(process.execPath, [path.join(__dirname, 'lib-query-conditions-checks.cjs'), process.argv[3], process.argv[2]], { stdio: 'inherit' });
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
