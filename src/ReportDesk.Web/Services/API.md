# Web business bridge API

Namespace: `ReportDesk.Web.Services`. Compile Link `Host/Program.cs`, both
`Host/Service.*Editing.cs`, `Host/ConnectionSettingsStore.cs`, and
`Host/ImportSourcesStore.cs`; reference Core and System.Web.Extensions.
Define `REPORTDESK_WEB` to omit the desktop entry point (a Library can also link it).

```csharp
var backend = new WebBackend(dataDirectory, reportRoot, offline: true);
WebContext tab = backend.CreateSession(); // IDisposable; one per browser tab
object dto = backend.Call(tab, method, args, cancellation, progress, admin: false);
byte[] xlsx = backend.Export(tab, args, cancellation);
object adminDto = backend.Call(tab, method, args, cancellation, progress, admin: true);
```

The three-argument constructor retains the existing server-report behavior for
integrations. WebOptions defaults `ReportDesk.ServerReportsEnabled` to false when
absent; BackendAdapter then uses `new WebBackend(dataDirectory, "", offline,
serverReportsEnabled: false)` for connections and an empty catalog. It does not
scan the old report root, initialize server file transactions/synchronization, or
create a replacement Reports directory. Old ReportsDirectory and sync settings
remain on disk unchanged. Explicit true restores server-report validation and
initialization. Local workspace backends load only their own uploaded XML copies.

`admin` is a SERVER authorization decision, never a client argument. HTTP must
enforce session/tab ownership, same-origin/CSRF, admin authorization and job
admission. Cancellation goes directly to the job token, not a second Call.
The handler serializes operations per tab. Backend also protects context state.

Ordinary whitelist: bootstrap, list, select, definition, query, view, page,
lookup, lookupPage, lookupAll, clear, relatedFiles. Export is a separate byte[] method.
Public relatedFiles returns cached `{title,summaryOnly:true,files:[{Role,Status,count}],warnings:[]}`
without file references/paths or another scan. Public controlled errors are
`ReportDesk.Web.WebUserException`; unknown errors remain generic in HTTP.
Select returns `definitionHash`; query/lookup MUST send it back. A missing or
changed hash is rejected before query execution, including source-index reorders.
Definition is `{ title, text: "", textParameterNames: string[] }`.
Bootstrap has reports, offline, demoVisible=false, warnings, and settings containing
only hasPassword and offline (no host/username/password/connection fields).
Ordinary DTOs never include server file paths, query SQL or connection settings.

Admin whitelist: settings, saveSettings, testConnection, activateConnection, discoverTns, tnsAliases,
import, checkNewReports, recheck, relatedFiles, sqlEditorOpen, sqlEditorCheck,
sqlEditorSave, sqlEditorReveal, reloadReport, layoutPreview, layoutSave. Admin SQL DTOs intentionally
retain SQL/path diagnostic fields. Ordinary methods remain callable in admin mode.
The HTTP-only maintenance `exportSql` task returns a UTF-8 BOM text download for
one `reportId`, all visible reports when that ID is empty, or a nonempty `reportIds`
array of distinct visible report IDs (without `reportId`). Invalid or unavailable
selected IDs reject the whole task. One selected report uses its name for the
download; multiple selected reports use the standard filename. It reads saved XML,
includes all nonempty non-ConditionUsing data sources, and labels multiple sources.
Unreadable, unsafe or changed definitions fail the whole task with no partial file.
It never executes SQL, alters the editor session, or exposes SQL to public routes.
Import paths must stay under reportRoot; TNS paths must stay under dataDirectory/config.
Config files (connection.json/import-sources.json/report-visibility.xml/
report-locations.xml) live under dataDirectory/config (provision this directory before
constructing WebBackend). Web intentionally ignores desktop report-visibility.xml
and presents all standalone reports. In server mode, startup scans the entire configured reportRoot once;
remembered sources cannot narrow the scan or include a former root. Out-of-root
records are preserved with a warning. Check-new also scans this root, adding only new IDs.
creating sessions neither scans directories nor writes files.

`CatalogVersion` is an opaque instance/revision token, read without acquiring the
catalog lock. Authenticated `GET /api/catalog-status` returns only this token and
does not renew the idle session. Visible idle pages check every 10 seconds and
refresh list metadata when it changes; current results, selection, conditions and
editor drafts remain untouched. Stale definition hashes still block execution.

`backend.FileGate` is the in-process catalog/file coordination lock. In server
mode, the HTTP/sync layer supplies a common CROSS-PROCESS write lock around
server report writes and synchronization. SQL/layout saves must run in the protected file transaction:
they publish no shared catalog until its reload callback succeeds.
Lock order: cross-process lock, context (handled internally),
FileGate. Do not acquire FileGate then call Call. Query/lookup execution and export
do not hold FileGate. Already completed tab results survive catalog/connection
changes; the next select/query/lookup gets the latest immutable-by-convention
definition snapshot. Existing running calls finish against their captured snapshot.

`ReloadChangedReports(IEnumerable<string> paths, CancellationToken token)` is a
server-only sync hook: call under the common cross-process lock after a validated
batch is installed. It refreshes affected query definitions (all known reports
when companion XML changed). It stages an isolated catalog then atomically
publishes on complete success; failure throws without publishing any definitions.
`ReloadChangedReportsStrict` is the same operation. Empty paths means a full
root refresh, for transaction recovery after restarting the process.
It never disposes other tabs' results. New query XML are imported too.

Default offline=true blocks every Oracle path, including testConnection/layout
field discovery. Web settings/test/save use a Web-only multi-connection extension
of `connection.json`, retaining the legacy active connection at the root and
CurrentUser DPAPI for remembered passwords. Password fields are never returned.
`SetConnectionSettings`
injects a trusted in-memory configuration without persistence for integrations.
`GetConnection()` returns a server-only `WebConnectionSnapshot` with Settings,
Password and Offline; Settings is a defensive copy without DPAPI ciphertext.
`GetConnectionString()` returns the Oracle string for the sync reader, and throws
in offline mode. Neither is an allowed HTTP/Call verb.

Connection profile API (maintenance routes only):

- `settings` returns the active settings; `{profileId}` reads another profile
  without activating it. The legacy fields remain, with `profileId`,
  `activeProfileId`, and `profiles: [{id,name,isActive,hasPassword}]` added.
- `saveSettings`/`testConnection` accept `{profileId,...existingFields}` to edit
  a profile or `{newProfile:true,...existingFields}` to add one. Adding or editing
  an inactive profile does not activate it; editing the active profile updates
  the current connection. Omitted ID retains the legacy active-edit behavior.
  Explicit profile operations require a nonblank name; IDs isolate equal names.
  Test returns `{version,settings}`; save returns the edited settings DTO.
- `activateConnection {profileId}` selects a saved profile without opening an
  Oracle connection. It returns the active settings DTO and rechecks any saved
  TNS file against the current server approval list and controlled directory.
- Each profile owns its remembered or process-memory password. New profiles
  cannot reuse another profile's password; opt-out survives restart. Existing
  queries keep their captured connection; subsequent refreshed contexts use
  the active connection, including the server sync reader when enabled.

The entire profile list and active root projection are replaced atomically in
one file. A path-specific Windows mutex serializes upgraded IIS writers, and a
byte-snapshot fingerprint rejects stale saves instead of dropping another
worker's profiles. Reopening settings reloads changed configuration and retains
process-memory passwords only for unchanged profiles. This protects storage;
it does not add live configuration broadcasting between IIS workers. Failed or
cancelled login tests and failed writes do not publish new settings. Before
rollback, back up `connection.json`: an older build can read the active root,
but saving from that build drops the added profile list.

`GetEditPaths(WebContext context, Dictionary<string,object> args, bool layout)`
returns absolute paths from the validated current editor snapshot/layout plan.
It does not reopen the editor or rotate tokens. Convert these to relative paths
for `ReportFileTransaction.ExecuteProtectedEdit`; invoke strict reload from its
reload callback so rollback failure propagates and retains the recovery marker.

Disposal: dispose each WebContext after its job is cancelled and finished; dispose
backend at application shutdown. Export returns a complete XLSX in memory; HTTP
sets the download filename/content type, never accepts/returns a filesystem path.

## HTTP source and lifetime contract

`GET /api/session` adds `serverReportsEnabled`; missing configuration means false.
`reportsDirectory` is exposed only to maintenance callers in server mode, and
`syncReady` requires that mode plus the existing offline/sync/sample gates.
Default mode keeps connection management and local workspace operations; server
import, browse/upload, report edits, publish and synchronization cannot act on the
old server root. Authentication, maintenance authorization and CSRF are unchanged.

Operation envelopes accept optional `localExpected:true`. The runtime rejects a
missing/nonlocal tab with HTTP 410 before creating a replacement context, preventing
an expired local operation from falling back to the server catalog. Explicit
`localBegin` is the fresh-load path. Local operations require maintenance access
and trusted HTTPS (with the existing real-loopback HTTP debugging exception).
`localActivate` also returns `queryPaths: [{id,path}]` for the imported reports.
Each path is relative to the selected local root and is exposed only through this
maintenance operation; clients use it to map IDs without guessing from names.
`localLocations` is a maintenance-only, trusted-origin operation accepting at
most 100 `{path,hash}` entries. The path is a browser-relative XML identifier,
never a server file path, and hash is the original file's SHA-256. It returns
`{items:[{path,locations}],warnings}` using the existing location file's hash
and basename evidence. Server-derived report IDs are deliberately not inferred.

`POST /api/tab-lifecycle` accepts `{tabId,args:{action:"touch"|"release"}}` with the
session cookie and CSRF. Ownership is scoped to that session. An unknown tab returns
`{ok:true,data:{alive:false,local:false,busy:false,released:false}}` without creating
it. An existing local tab also requires maintenance access and the secure-local
origin rules. Touch renews that tab only. Release disposes an idle tab and returns
`released:true`; an active job or pending local save instead returns `busy:true`
without disposing it. Other responses retain `alive`, `local`, `busy`, `released`.

Each tab expires independently after `IdleMinutes` (default 60); only tabs with no
running job are pruned, regardless of another tab's session activity. Pending
local saves block immediate release but do not retain an idle workspace forever.
Background cleanup runs even with server reports/sync disabled, and request-side
pruning remains. Shutdown cancels jobs but defers context/backend disposal until
active operations have ended. No query timeout, result-row cap or automatic rerun
is introduced.

Local workspace cleanup is limited to marked owned paths under
`DataDirectory/LocalWorkspaces`, with an exclusive worker lease and link checks.
Failed retired-workspace and abandoned-worker deletion is retried; ownership
metadata survives incomplete content deletion. Live workers and unknown paths
are not deleted. Client XML, backups and IndexedDB recovery records are outside
this cleanup. Browser HTTP 410/local-context 401 recovery preserves drafts; 403
remains fatal. See `UI/README.md` for browser touch/release and recovery behavior.

## Offline verification

Department context metadata (2026-09-20): `select` parameters expose optional
`contextDepartment` and `lookupDependencies` fields. An implicit CurrentDeptID
(case-insensitive), without its own lookup SQL/source, is projected as a lookup
ComboBox before dependent controls. Its lookup reuses COM_BD_DEPARTMENT valid
department IDs/names and does not require its own value. It is a query context,
not an authenticated HIS identity. Other report lookups retain their original
SQL and bound context parameters. Clients clear dependent selections/options
when a dependency changes; no selected context is persisted.

From the repository root:

```powershell
dotnet build src/ReportDesk.Web/Services/BridgeChecks.csproj -v:minimal
& src/ReportDesk.Web/bin/BridgeChecks/Debug/net48/BridgeChecks.exe '<read-only LIB root>'
```

The LIB argument contains `LIB/Config/Xml`. The checks copy two existing report
XML files to a unique temporary directory before any edit, and inspect the full
original corpus read-only. The source XML and knowledge base are never modified.
DataTables used to exercise paging/export/isolation contain transport markers,
not Oracle/HIS results. Artifacts stay in the printed temporary directory.

2026-09-18: 17 checks passed, including public metadata for all 1,284 standalone
reports, hash mismatch rejection, tab isolation, in-memory XLSX, deferred save
publication, strict batch failure atomicity, cancellation and path containment.
Host and Web built with zero warnings/errors; existing connection persistence
and SQL/layout editing checks also passed (two optional local fixtures skipped).
Oracle, live HIS equivalence, IIS and Windows 10 acceptance remain NOT RUN by
these bridge checks; use the main integration workflow for HTTP/IIS validation.
