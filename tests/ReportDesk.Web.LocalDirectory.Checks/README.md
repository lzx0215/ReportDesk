# Local HIS directory checks

Run from the repository root:

```powershell
dotnet build tests/ReportDesk.Web.LocalDirectory.Checks/ReportDesk.Web.LocalDirectory.Checks.csproj -p:ReportDeskWebTargetFramework=net462
& tests/ReportDesk.Web.LocalDirectory.Checks/bin/Debug/net462/ReportDesk.Web.LocalDirectory.Checks.exe
node src/ReportDesk.Web/UI/verification/local-directory-checks.cjs
```

This is an offline integration suite through `WebRuntime`, its HTTP request boundary, and the real backend. No IIS configuration or Oracle connection is used. It never supplies fabricated Oracle query results.

The suite copies the real `抗菌药物消耗情况查询设置.xml` and its report template. It first looks under the configured read-only HIS LIB path; if unavailable, it uses the real retained fixture already used by the full-XML checks under `artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml`. An alternative fixture directory can be passed as the executable's first argument. Original file hashes are checked after the run. XML variations are made only from copied real reports (comments and an encoding variant), without altering the original query semantics.

Coverage includes HTTPS/loopback, maintenance, CSRF and origin checks; separate sessions using the same tab ID; sibling tabs; raw byte upload and UTF-16 preservation; rejected unsafe/reordered/incomplete uploads; candidate versus confirmed snapshots; SQL, complete query XML and related layout XML; wrong/cross-context acknowledgements; acknowledgement replay and consecutive saves; relative path metadata; aborted-save rescan; preserving the shared report root and database settings; live worker leases, bounded abandoned workspace cleanup, configured-root overlap protection; and cleanup on runtime disposal.

The Node checks use the same retained real XML bytes with in-memory File System Access, IndexedDB and Web Locks test doubles. They cover backup/storage failures before any original-file write; multi-file partial saves and verified restoration; acknowledgement loss and retry; refresh recovery; third-party edits and corrupted backups blocking all rollback writes; independent tab write locks and durable recovery ownership; related-file conflicts; and revoked permission. No source fixture is written. These are client logic checks, not native browser or filesystem API compatibility evidence.

Browser write acknowledgements are simulated here. Passing these checks is not evidence that native browser directory selection, write permissions or browser recovery flows pass. Those require the separate browser verification. The suite retains its isolated run directory under `artifacts/verification/local-directory-*`, including `PASS.txt` only after all checks succeed.
