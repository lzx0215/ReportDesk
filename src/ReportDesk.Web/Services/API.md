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

Admin whitelist: settings, saveSettings, testConnection, discoverTns, tnsAliases,
import, checkNewReports, recheck, relatedFiles, sqlEditorOpen, sqlEditorCheck,
sqlEditorSave, sqlEditorReveal, reloadReport, layoutPreview, layoutSave. Admin SQL DTOs intentionally
retain SQL/path diagnostic fields. Ordinary methods remain callable in admin mode.
Import paths must stay under reportRoot; TNS paths must stay under dataDirectory/config.
Config files (connection.json/import-sources.json/report-visibility.xml/
report-locations.xml) live under dataDirectory/config (provision this directory before
constructing WebBackend). Web intentionally ignores desktop report-visibility.xml
and presents all standalone reports. Startup restores/imports once;
creating sessions neither scans directories nor writes files.

`backend.FileGate` is the in-process catalog/file coordination lock. The HTTP/sync
layer supplies a common CROSS-PROCESS write lock around admin XML/config writes
and synchronization. SQL/layout saves must run in the protected file transaction:
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
field discovery. Settings/test/save use the Host atomic persistence and DPAPI
implementation. Password fields are never returned. `SetConnectionSettings`
injects a trusted in-memory configuration without persistence for integrations.
`GetConnection()` returns a server-only `WebConnectionSnapshot` with Settings,
Password and Offline; Settings is a defensive copy without DPAPI ciphertext.
`GetConnectionString()` returns the Oracle string for the sync reader, and throws
in offline mode. Neither is an allowed HTTP/Call verb.

`GetEditPaths(WebContext context, Dictionary<string,object> args, bool layout)`
returns absolute paths from the validated current editor snapshot/layout plan.
It does not reopen the editor or rotate tokens. Convert these to relative paths
for `ReportFileTransaction.ExecuteProtectedEdit`; invoke strict reload from its
reload callback so rollback failure propagates and retains the recovery marker.

Disposal: dispose each WebContext after its job is cancelled and finished; dispose
backend at application shutdown. Export returns a complete XLSX in memory; HTTP
sets the download filename/content type, never accepts/returns a filesystem path.

## Offline verification

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
