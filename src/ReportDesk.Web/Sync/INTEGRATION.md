# HIS sync integration (net48, offline implementation)

Scope: `Sync/*.cs` has no System.Web dependency. It uses existing Core and Oracle.ManagedDataAccess **19.32.0**, plus Framework System.IO.Compression/System.Xml.Linq. No new production packages. All tests use isolated copies of real LIB XML bytes; the offline archive source is a file safety fixture, not a simulated Oracle result or evidence of database compatibility.

## Startup order: recover BEFORE constructing a catalog

Use a single configured `DataDirectory` outside the website, `ReportsDirectory = DataDirectory/Reports`, and `StateDirectory = DataDirectory/Sync`. Both directories require the same application identity's write permissions. Application pool: one worker. Configure `offline=true`, `syncEnabled=false` by default.

```csharp
var work = new ReportWorkDirectory(reportsDirectory, Path.Combine(dataDirectory, "Sync"));
var transactions = new ReportFileTransaction(work);
transactions.RecoverFiles(); // BEFORE FromBaseline(), imports, WebBackend or loading query XML
// RecoveryRequired means do not expose affected definitions. Keep marker/backups for retry.

// After recovery, construct backend/catalog and build policy from recovered files.
var policy = ReportFilePolicy.FromBaseline(work);
var options = new SyncOptions {
    WorkingDirectory = reportsDirectory,
    StateDirectory = Path.Combine(dataDirectory, "Sync"),
    Enabled = syncEnabled && !offline
};
var reader = new OracleReleaseReader(connectionProvider, options, validated: false);
var coordinator = new SyncCoordinator(options, reader, policy, reloadSink);
coordinator.InitializeBaseline(); // idempotent; preserves all previously tracked hashes
// schedule coordinator.RunOnce(token) through IIS application lifecycle management
```

`IOracleSyncConnectionProvider.GetConnectionString()` returns the current server-side configuration; never return it to browsers. The reader constructor does not connect. `validated:false` refuses every read before asking for the connection string. Enabling the scheduler alone does not bypass that gate. Only change it after explicitly authorized sample verification; this delivery has **NOT RUN Oracle**.

`IReportReloadSink.Reload(IReadOnlyList<string> relativePaths)` calls `backend.ReloadCatalog()` and must throw on failure. It is called under the shared file lease. Build a new catalog first, then swap it atomically; do not leave partial shared state when throwing. Already executing queries must retain their original in-memory definition/result. The callback must NOT reacquire the same file lease.

## Shared write lease and existing SQL/layout methods

`work.Acquire(CancellationToken)` returns `IDisposable`; a lock file in Reports is opened with FileShare.None and excludes every process using that Reports path, including IIS recycle overlap. No process name/session identity assumptions. Acquisition is cancellable. For simple definition reads, imports/reloads and new query preparation, use the same lease. Release it before database execution or result processing.

```csharp
transactions.ExecuteProtectedEdit(
    new[] { queryRelativePath, layoutRelativePath },
    () => backendExistingSqlOrLayoutSave(),
    () => backend.ReloadCatalog(),
    token);
```

This wrapper acquires the lease itself. Do NOT surround it with a second `Acquire()`. Enumerate **all** files the mutation may create or replace; for SQL-only saves list the query file, for paired saves list both. For imports list destination XML paths in Reports before copying; reading a source outside Reports does not authorize writes there. Import config/catalog side effects are not part of this file transaction and must occur only after the file operation succeeds. It does not transactionally protect arbitrary extra writes, directory moves, `.bak` files or undeclared paths.

After a successful maintenance import **and after releasing the transaction lease**, call `coordinator.RegisterImportedBaseline(token)`. It discovers newly imported complete groups and records hashes only for previously untracked paths. This handles an initially empty Reports directory. Existing tracked hashes/accepted release IDs are preserved, so importing over a locally edited/previously synced report does not silently clear conflicts. Startup `InitializeBaseline()` likewise adds missing paths from the supplied recovered policy without resetting tracked hashes. Do not call baseline registration automatically during normal publication scans, since that would acknowledge arbitrary new external files as imported baseline.

It snapshots old bytes into a temporary transaction directory, durably writes `pending.xml`, runs mutation then reload, durably marks committed, then removes recovery files. Any mutation/reload exception restores old XML and calls reload again. Recovery/reload failure retains the marker and emits `RecoveryRequired`. Recovery restores uncommitted batches and retains committed batches. This provides recoverability while consumers honor the lock; it does not claim simultaneous atomic replacement of multiple files. Current journals/backups contain report XML solely for fault recovery, never database query results. They are not logs or a version archive.

External editors do not honor this file lease. Sync compares the current file hash against the last accepted baseline/HIS hash and rejects local conflicts; do not permit uncoordinated external writes during the short physical file replacement/recovery window. Lock files are retained and are not indicators that a process currently holds a lock.

## Coordinator/status/conflict API

- `RunOnce(CancellationToken)` returns a safe `SyncStatus`; expected and unexpected failures are reduced to fixed codes, with no raw exception/SQL/connection messages.
- `Status` returns a snapshot with `Code`, `LastSuccessUtc`, `AppliedReleases`, `PendingReleases`, `ConflictReleases`, `ReleaseId`, `RequiresRecovery`, `OracleValidated`, `ScanMode`.
- `GetPendingConflicts(token)` returns `ReleaseId`, `Code`, `Fingerprint`, `CanResolve`. Only `LocalConflict` has `CanResolve=true`.
- UI should call `ResolveConflict(releaseId, expectedFingerprint, ConflictResolution.AcceptHis/KeepLocal, token)`, then `RunOnce`. There is also an overload without expected fingerprint for internal callers. The recorded decision is bound to both publication fingerprint and observed local hashes; a subsequent local/publication change requires another decision.
- `AcceptHis` allows the next verified batch to replace local edits. `KeepLocal` acknowledges that exact publication without changing local bytes or resetting their baseline hashes. Later publications still conflict with the local edit.
- `Recover(token)` performs recovery and reload under the lease after the coordinator exists. **Startup uses `ReportFileTransaction.RecoverFiles()` before policy/catalog construction.**

Typical codes: `Disabled`, `OracleNotValidated`, `Running`, `Success`, `Pending`, `Busy`, `Cancelled`, `BaselineRequired`, `SyncStateInvalid`, `RecoveryRequired`, `TransactionRolledBack`, `LocalConflict`, `IncompleteReportBatch`, `FileNotAllowlisted`, `NewReportCompanionUnresolved`, `CompanionRejected`, `UnsafePath`, `ArchiveIntegrityRejected`, `ScanSizeRejected`, `SyncFailed`. Render fixed friendly text in UI; do not serialize internal XML/journal DTOs. `LastSuccessUtc` advances only when the full scan completes with no pending items, not when one file happens to succeed.

## Allowlist, first scan and late commits

`FromBaseline` reuses Core discovery and standalone-query rules. Ambiguous/missing companion associations are excluded. Main template naming is a **candidate**, not a proven menu/HIS equivalence claim. Existing complete groups are validated together before replacement.

New published paths can enter automatically if the same verified batch contains a query XML with report SQL and the corresponding recognized `Spread class="FarPoint.Win.Spread.FpSpread"` template, plus any explicit detail template. Query/main pairing uses Core's `查询设置.xml` / `报表设置.xml` convention; explicit detail references must resolve uniquely inside the batch. New nonconforming/ambiguous groups remain pending rather than guessing. A file with no report SQL does not become a runnable query. Other XML, DLL/EXE, directory archives, duplicate targets, DTDs, links, path traversal, ADS and device names are rejected. No original SQL or report bytes are rewritten.

Every scan reads and validates the full publication snapshot before applying anything, but keeps expanded bytes only for the current latest complete candidate groups. When a newer group supersedes an older one, the older bytes are released immediately; independent groups from older multi-report releases remain candidates. Historical acknowledgements retain only IDs/fingerprints. Initial history is not replayed oldest-first into the catalog. Per-file accepted release IDs and release content fingerprints prevent older late commits from rolling back newer files. Rejected/failed records remain pending and are rechecked after restart. `FILECOUNT` is not trusted.

## Important current throughput limit

**This implementation still reads and verifies all selected historical BLOBs on every scan. It has NOT implemented metadata-only polling, FILE_ID content caching or periodic content verification. Do not describe it as production-ready 30-second polling.** The scheduler default can remain 30 seconds while sync stays disabled; choose the enabled interval only after measuring approved publication volume/latency. Required production optimization for a large history is metadata recheck plus known-file content deduplication and periodic full-content revalidation, while retaining late-commit detection. Full scans avoid maximum-ID/time gaps but are intentionally conservative for this offline delivery.

Archive safety limits (not query/result truncation): 32 MiB compressed/file, 32 MiB expanded/file, 128 files/release, 128 MiB compressed/expanded per batch; 256 MiB expanded for the **current candidate set**, not the accumulated publication history. Replaced historical bytes do not count toward this limit. Limits are configurable in `SyncOptions`; an exceeded limit is a reported failure, never silent skipping. Metadata/state retention follows publication history; no automatic pruning policy is inferred. The Web host's configurable `SyncSeconds` controls polling frequency; this module does not own a timer.

The reader currently targets the static-evidence owner `HIS`, filters the evidenced report publication description prefix with a bound parameter, and uses a read-only Oracle transaction plus fixed SELECTs and bound release IDs. It does not trust FILECOUNT and does not write publication tables. `DIRECTORY_FLAG` accepts only explicit `0`/`false` as a single file; other values fail closed. Database DIRECTORY is required to be a safe relative directory without a leading slash. The actual flag representation, owner, directory convention, ZIP flavor and permissions require known-sample verification. Legacy empty ZIP entry names are supported; ZIP64, encryption, multi-entry archives and directory packages are not.

## Validation from repository root

```powershell
dotnet build tests/ReportDesk.Web.Sync.Checks/ReportDesk.Web.Sync.Checks.csproj --nologo -v:minimal
& tests/ReportDesk.Web.Sync.Checks/bin/Debug/net48/ReportDesk.Web.Sync.Checks.exe
```

Optional first argument supplies a real LIB root (the inner LIB). Tests read real query/layout bytes, work only under a fresh temp directory, verify source hashes unchanged, and leave the isolated evidence path in stdout. There is no Oracle connection, generated query-result data, HIS client execution, IIS modification, source LIB write, commit or deployment. Oracle sample equivalence, actual server DPAPI/permissions, IIS idle/recycle behavior and production latency remain **NOT RUN**.
