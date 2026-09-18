# Sync offline checks

Run from `D:\aiproject\ReportDesk`:

```powershell
dotnet build tests/ReportDesk.Web.Sync.Checks/ReportDesk.Web.Sync.Checks.csproj --nologo -v:minimal
& tests/ReportDesk.Web.Sync.Checks/bin/Debug/net48/ReportDesk.Web.Sync.Checks.exe
```

2026-09-18 result: build **0 warnings, 0 errors**; **17 groups passed**. Source LIB bytes and hashes remain unchanged. No real Oracle connection, fabricated Oracle query results, IIS change or deployment was performed. This test project links only Sync source files and references existing Core/net48 dependencies, so it can run independently of concurrent Web integration.

Checks cover:

- Actual LIB query/template bytes in named-deflate and empty-name stored ZIP envelopes.
- ZIP paths, duplicate entries, CRC corruption, truncated input and expansion/compressed bounds.
- Absolute/traversal/ADS/device-name path rejection.
- Disabled/unvalidated readers never requesting connection configuration.
- Shared directory lease tested by a separate process with cancellation.
- Paired-file rollback after reload failure; process exit mid-batch; failed recovery preserving its marker.
- Idempotence and late historical recheck without reverting current files.
- First full scan never exposing older historical versions to reload.
- New complete groups from genuine report bytes joining the allowlist.
- Older multi-report release retaining an independent latest group while suppressing superseded files.
- Twenty-four historical versions fitting a one-current-candidate memory budget; true candidate overrun still rejected.
- Empty Reports startup then maintenance import recording baseline without resetting tracked edits.
- Local conflicts, stale decisions, explicit accept HIS and keep local.
- Incomplete pairs, unknown XML paths and duplicate destinations staying pending.
- Restart retry following a failed release reload; metadata state excluding SQL/credential messages.

The fixture is an offline archive source carrying real report byte copies, not a simulated Oracle result set. Test metadata/unsafe envelope fixtures exercise file safety only. Test copies and recovery evidence are left under a unique `ReportDeskSyncChecks-*` temp directory whose path is printed; no broad cleanup is run.

Main-agent integration, APIs, startup order, limits and the unresolved full-history BLOB polling cost are documented in `src/ReportDesk.Web/Sync/INTEGRATION.md`.
