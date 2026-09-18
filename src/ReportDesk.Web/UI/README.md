# Web UI integration

The Web UI reuses the Desktop HTML structure, CSS, renderer, condition view and
SQL editor. No runtime package or external web resource is required.

## Routes and packaging

Serve `index.html` at `/` and `/admin` (also `/admin/`). Before sending the HTML,
replace `"/assets/` with `"{BasePath}assets/`, where BasePath is the validated IIS
application prefix ending in `/`. The document contains no inline script/style.

Copy these files to the published `UI/` directory and expose only these assets:

| Source | Destination / asset name |
| --- | --- |
| This directory | `index.html`, `transport.js`, `web.js`, `web.css` |
| `src/ReportDesk.Desktop/ui/` | `styles.css`, `execution.css`, `sql-editor.css`, `query-form.js`, `renderer.js`, `sql-editor.js`, `close-emblem.svg` |

Serve scripts/styles/images at `{BasePath}assets/{name}`. Transport derives the
API root from the parent of its `/assets/` URL. Do not serve README or verification
scripts. The desktop `index.html`, preload and editor files are unchanged.

## Transport contract

- `GET api/session` returns `{csrf,admin,offline,idleMinutes}` directly. A missing
  idleMinutes uses 60 in the UI. All responses must use `Cache-Control: no-store`.
- Every POST is `{args,tabId}`, with the session cookie and `X-ReportDesk-CSRF`.
  tabId is a fresh cryptographic ID per document; no passwords, drafts, results,
  parameter values or tokens are written to localStorage/sessionStorage.
- Every whitelisted POST can return `{ok:true,data}` or `{ok:true,jobId}`.
- `GET api/tasks/{id}?tabId=...` returns
  `{state,message,data,error}`; states are running/succeeded/failed/cancelled.
  `error` may be a string or `{message}`. A completed job's `data` is the same DTO
  as a synchronous call. Polling does not set a query deadline.
- `DELETE api/tasks/{id}?tabId=...` sends CSRF too. Cancellation requested before
  the POST returns is sent as soon as the job ID arrives. Cancellation waits for
  a terminal state; deleting a task is not interpreted as immediate completion.
- The adapter returns the preload-compatible `{ok,data,message,cancelled}` and
  exposes `onProgress/onFatal`. It never retries POSTs. Network uncertainty is
  displayed, not reported as successful or cancelled. HTTP 401/403 closes UI
  operations; a task 404 explicitly asks for a fresh query.
- Export returns `{downloadUrl,count?}`. The URL must be same-origin under the
  application's `api/` path; the browser downloads using GET and its cookie.
  The Web success message confirms a download request for the complete filtered
  and sorted result; it does not derive an export row count from the current page.

Public methods: bootstrap, list, select, definition, relatedFiles, query, view,
page, lookup, lookupPage, lookupAll, clear, export.

Admin methods: settings, saveSettings, testConnection, import, checkNewReports,
recheck, reloadReport, sqlEditorOpen, sqlEditorCheck, sqlEditorSave,
sqlEditorReveal, layoutPreview, layoutSave, discoverTns, tnsAliases, syncStatus,
syncNow, resolveSyncConflict, openLogs.

`session.admin === true` enables maintenance on `/` and `/admin`. An empty
server maintenance list grants admin to every client. Server authorization,
same-origin validation, method allowlists, session/task ownership, path controls
and SQL/path/password projection must still be enforced by WebRuntime/backend.

`definition` must contain `{title,textParameterNames:string[]}`. Names are bare
parameter names used with `.Text`; the UI normalizes case. It does not fetch SQL.
Public details may omit `path`; the renderer shows a controlled-directory label.
`select` must return matching `id` and a nonempty `definitionHash`. The adapter
stores only that hash per reportId in memory and injects it unchanged into every
`query` and `lookup`, overriding caller-supplied hashes without changing source or
values. Reselect invalidates the old hash before loading; missing/failed selection
cannot reuse it. `clear` removes cached hashes. Query/lookup without a selected
hash fails locally. Server rejection of a changed hash is displayed without
fetching a newer hash or replaying the operation. No SQL is fetched for this check.
Public relatedFiles accepts the safe summary DTO
`{title,summaryOnly:true,files:[{Role,Status,count}],warnings}`; the Web renderer
shows status/count without requiring or inventing paths. The existing detailed
DTO `{root,query,files:[{Role,Status,Reference,Paths}],warnings}` remains supported.
When `session.admin` is true, `relatedFiles` routes to `api/admin/relatedFiles`
and retains full diagnostics; otherwise it routes to `api/relatedFiles` and
receives summaryOnly.
The Web favicon reuses `/assets/close-emblem.svg`, including the BasePath rewrite.

Import uses `{folder:boolean,path:string}` where path is relative to the approved
server root; empty folder path means the whole root. No browser file picker is
used. Server validation must independently reject traversal and links.
`discoverTns` must return only registered server files; `tnsAliases` receives
`{path}` from that list. The file picker is hidden and the method is denied.

`sqlEditorDirty` and `sqlEditorDiscard` are local adapters with beforeunload
protection. SQL/column saves retain explicit target confirmation. A successful
save with `{saved:true,editor:...}` clears local dirty state. Editor DTOs and
preview tokens otherwise retain the Desktop contract. `sqlEditorReveal` and
`openLogs` return `{title,text}` displayed as text only. Reveal receives reportId
and the locally retained editor token when available.

## Sync conflict and result lifetime UI

`syncStatus` accepts `{enabled,running?,lastSuccess,message,error?,text?,conflicts}`.
`conflicts` is an array of `{releaseId:number,code,message,fingerprint,canResolve}`;
`message` is a safe server-provided explanation. Only `canResolve === true`, a
valid releaseId and a nonempty string fingerprint enable “接受 HIS 版本” and
“暂保留本地” buttons. Both require an explicit native confirmation, then POST
`api/admin/resolveSyncConflict` with exactly
`{releaseId,acceptHis:boolean,fingerprint}`. The required fingerprint is sent
unchanged from the displayed conflict; missing/blank fingerprints are rejected
locally. The server strictly revalidates the fingerprint and owns the write lock.
After success the UI reloads status and the catalog;
it does not claim files were replaced merely because a decision was recorded.
Unresolvable or incomplete entries only show an explanation. String-only conflicts
or a conflict count can be displayed but cannot be resolved. A rejected stale
fingerprint is shown as a failure, without automatic resubmission.

Footer/help state: results remain in server memory, configurable idle release
(default 60 minutes), no cleanup during execution, and re-query after IIS recycle
or session loss. No automatic polling runs while idle just to keep a session
alive. Each page has a separate tabId; refreshing creates a new context and does
not automatically restore or rerun a previous query. This is a known limitation
relative to the plan's best-effort refresh recovery. Existing server contexts are
released by the backend's idle policy; the UI does not implement server cleanup.

## Offline verification

From repository root:

```powershell
node src/ReportDesk.Web/UI/verification/transport-checks.cjs
node src/ReportDesk.Web/UI/verification/browser-checks.cjs
```

The first verifies exact definitionHash injection for query/lookup, per-report
isolation, reselection/invalidation, missing hashes and no replay on stale hashes;
public/admin relatedFiles routing, export wording, contract envelopes,
cancellation race, CSRF, job errors/404,
admin gating, draft state, relative imports, safe downloads, independent tab IDs
and unchanged Desktop `.Text` metadata extraction. The second uses the installed
Desktop Playwright runtime with local Chromium, intercepts all network requests,
and checks public/admin/denied/virtual-directory pages, public related-file summaries
and full admin diagnostics, the shared SVG favicon, all-POST job responses,
idle help, strict canResolve button gating, both sync decisions with mandatory
fingerprints, confirmation/cancellation, registered TNS, password
clearing/no browser storage, and logs. Protocol checks also reject missing/blank
fingerprints, exclude legacy fields, and verify stale-fingerprint failures are not
automatically resubmitted. These are protocol/UI fixtures, not Oracle
or HIS result-equivalence validation. CLI fallback was necessary due to a Windows
libuv handle assertion. No IIS deployment, real Oracle, real file save or real
release conflict resolution is performed by these checks.
