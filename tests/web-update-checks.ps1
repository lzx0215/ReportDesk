[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$OutputDirectory
)

# Run with Windows PowerShell 5.1. Every mutation is confined to a fresh test
# directory. IIS discovery and HTTP are mocked; this is not a server acceptance test.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repo 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalHis-20261106'
}
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ('artifacts\verification\web-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$allowedOutput = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\verification')) + '\'
if (-not $OutputDirectory.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test output must be a new child of artifacts\verification.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Test output directory already exists.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$oldBinaryDirectory = Join-Path $repo 'artifacts\verification\xml-editor-20261104-legacy-final'
$oldUiArchive = Join-Path $repo 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-XmlEditor-20261104-v2.zip'
$script:ExpectedCore = '6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7'
$script:Results = New-Object 'System.Collections.Generic.List[object]'

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw ('ASSERT: ' + $Message) }
}
function Get-TestHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Write-TestText([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}
function Copy-TestTree([string]$Source, [string]$Destination) {
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}
function Get-TestSnapshot([string]$Directory) {
    $state = @{}
    if (Test-Path -LiteralPath $Directory) {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
            $state[$file.FullName.Substring($Directory.TrimEnd('\').Length + 1)] = Get-TestHash $file.FullName
        }
    }
    return ,$state
}
function Assert-TestSnapshot($Expected, [string]$Directory, [string]$Message) {
    $actual = Get-TestSnapshot $Directory
    Assert-True ($Expected.Count -eq $actual.Count) ($Message + ': file count changed')
    foreach ($key in $Expected.Keys) {
        Assert-True ($actual.ContainsKey($key) -and $actual[$key] -eq $Expected[$key]) ($Message + ': ' + $key)
    }
}
function Assert-TestFailure([scriptblock]$Action, [string]$ReasonPattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'Expected operation to reject the input.'
    if ($ReasonPattern) {
        Assert-True ($caught.Exception.Message -match $ReasonPattern) ('Unexpected failure category: ' + $caught.Exception.Message)
    }
}
function New-TestFixture([string]$Name) {
    $root = Join-Path $OutputDirectory $Name
    $site = Join-Path $root 'ReportDesk'
    $data = Join-Path $root 'Data'
    $reports = Join-Path $root 'Reports'
    $other = Join-Path $root 'OtherProject'
    $backups = Join-Path $root 'Backups'
    foreach ($directory in @($site, $data, $reports, $other)) { [void][IO.Directory]::CreateDirectory($directory) }
    Copy-TestTree $oldBinaryDirectory (Join-Path $site 'bin')
    Assert-True ((Get-TestHash (Join-Path $site 'bin\ReportDesk.Core.dll')) -eq $script:ExpectedCore) 'Genuine prior Core fixture baseline must match.'
    Copy-TestTree (Join-Path $PackageDirectory 'payload\UI') (Join-Path $site 'UI')
    # Only this new feature file is absent from the old installation. This exact
    # file must be removed by rollback rather than left as a partial update.
    [IO.File]::Delete((Join-Path $site 'UI\local-directory.js'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($oldUiArchive)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '^RuntimePatch/UI/[^/]+$') {
                $target = Join-Path $site ($entry.FullName.Substring('RuntimePatch/'.Length))
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
    } finally { $archive.Dispose() }
    $xmlData = [Security.SecurityElement]::Escape($data)
    $xmlReports = [Security.SecurityElement]::Escape($reports)
    Write-TestText (Join-Path $site 'Web.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><appSettings>
<add key="ReportDesk.DataDirectory" value="$xmlData" />
<add key="ReportDesk.ReportsDirectory" value="$xmlReports" />
<add key="ReportDesk.Offline" value="true" />
</appSettings><system.web><compilation targetFramework="4.6.2" /></system.web></configuration>
"@
    Write-TestText (Join-Path $data 'config\connection.json') '{"TestOnlyMarker":"preserve-db-settings","SavePassword":false}'
    Write-TestText (Join-Path $data 'config\catalog.json') '{"Connection":{"TestOnlyMarker":"preserve-legacy-connection"},"Reports":[]}'
    Write-TestText (Join-Path $data 'config\unrelated.json') '{"TestOnlyMarker":"preserve-all-data"}'
    Write-TestText (Join-Path $reports 'sample.xml') '<ReportQueryInfo><TestOnlyMarker>do-not-modify-his</TestOnlyMarker></ReportQueryInfo>'
    Write-TestText (Join-Path $other 'Web.config') '<configuration><!-- Other project sentinel --></configuration>'
    Write-TestText (Join-Path $other 'bin\Other.dll') 'OTHER PROJECT TEST SENTINEL'
    Write-TestText (Join-Path $site 'keep-existing.txt') 'SITE FILE NOT IN PAYLOAD'
    Copy-Item -LiteralPath (Join-Path $repo 'src\ReportDesk.Web\Global.asax') -Destination (Join-Path $site 'Global.asax')
    return [pscustomobject]@{
        Root = $root; Site = $site; Data = $data; Reports = $reports; Other = $other; Backups = $backups
        SiteBefore = (Get-TestSnapshot $site); DataBefore = (Get-TestSnapshot $data)
        ReportsBefore = (Get-TestSnapshot $reports); OtherBefore = (Get-TestSnapshot $other)
    }
}
function Assert-TestPreserved($Fixture) {
    Assert-TestSnapshot $Fixture.DataBefore $Fixture.Data 'Database settings/data must remain byte-for-byte unchanged'
    Assert-TestSnapshot $Fixture.ReportsBefore $Fixture.Reports 'HIS files must remain byte-for-byte unchanged'
    Assert-TestSnapshot $Fixture.OtherBefore $Fixture.Other 'Other project must remain byte-for-byte unchanged'
    foreach ($path in @('Web.config', 'bin\ReportDesk.Core.dll', 'bin\Oracle.ManagedDataAccess.dll', 'keep-existing.txt')) {
        Assert-True ((Get-TestHash (Join-Path $Fixture.Site $path)) -eq $Fixture.SiteBefore[$path]) ('Protected site file changed: ' + $path)
    }
}
function Invoke-TestCase([string]$Name, [scriptblock]$Action) {
    $started = [DateTime]::UtcNow
    try {
        & $Action
        $script:Results.Add([pscustomobject]@{name=$Name; status='PASS'; seconds=([DateTime]::UtcNow-$started).TotalSeconds})
        Write-Host ('PASS ' + $Name)
    } catch {
        $script:Results.Add([pscustomobject]@{name=$Name; status='FAIL'; seconds=([DateTime]::UtcNow-$started).TotalSeconds; error=$_.Exception.Message})
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message) -ForegroundColor Red
    }
}

# Correctness is checked using file bytes, presence/absence, and fresh snapshots.
. (Join-Path $repo 'scripts\Apply-WebUpdate.ps1') -PackageDirectory $PackageDirectory
$script:TestOriginalSetFile = (Get-Command Set-ReportDeskFile).ScriptBlock
$script:TestOriginalBackupRoot = (Get-Command Initialize-ReportDeskBackupRoot).ScriptBlock
$script:TestOriginalBackupTrusted = (Get-Command Assert-ReportDeskBackupTrusted).ScriptBlock
$script:TestCurrentFixture = $null
$script:TestCopyCount = 0
$script:TestFailCopyAt = 0
$script:TestIisMutationCalls = 0
$script:TestHealthCount = 0
$script:TestOfflineCount = 0
$script:TestHealthFailureAt = 0
$script:TestOfflineFailure = $false
$script:TestMutateSourceAt = 0
$script:TestSourceMutationObserved = $false
$script:TestBackupTrustChecks = 0
$script:TestIsAdministrator = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# Boundary mocks. None of these inspect or change the actual IIS installation.
function Assert-ReportDeskAdministrator { }
function Assert-ReportDeskBackupTrusted {
    param([string]$Directory)
    $script:TestBackupTrustChecks++
}
function Get-ReportDeskIisContext {
    param([string]$SitePath)
    $f = $script:TestCurrentFixture
    return [pscustomobject]@{
        SitePath=$SitePath; SiteName='IsolatedReportDeskFixture'; AppPath='/'; PoolName='FixtureSharedPool'
        SnapshotHash=('A' * 64); CandidateBaseUrls=@('http://127.0.0.1:38443/')
        Bindings=@([pscustomobject]@{Protocol='http';BindingInformation='127.0.0.1:38443:'})
        Mappings=@(
            [pscustomobject]@{PhysicalPath=$SitePath;VirtualPath='/';SiteName='IsolatedReportDeskFixture';AppPath='/';PoolName='FixtureSharedPool'},
            [pscustomobject]@{PhysicalPath=$f.Other;VirtualPath='/';SiteName='OtherFixture';AppPath='/';PoolName='FixtureSharedPool'}
        )
    }
}
function Test-ReportDeskHealth {
    param([string]$BaseUrl)
    $script:TestHealthCount++
    if ($script:TestHealthFailureAt -eq $script:TestHealthCount) { throw 'Injected final health check failure.' }
    return [pscustomobject]@{BaseUrl=$BaseUrl;Healthy=$true;HomeStatus=200;SessionStatus=200}
}
function Test-ReportDeskOffline {
    param([string]$BaseUrl, [string]$Marker)
    $script:TestOfflineCount++
    if ($script:TestOfflineFailure) { throw 'Injected offline confirmation failure.' }
    return [pscustomobject]@{BaseUrl=$BaseUrl;Offline=$true}
}
function Initialize-ReportDeskBackupRoot {
    param([string]$Path)
    if ($script:TestIsAdministrator) { & $script:TestOriginalBackupRoot -Path $Path }
    else {
        # Do not remove this unelevated process's access to its test fixtures.
        # Actual Administrator/SYSTEM-only backup ACL acceptance remains NOT RUN.
        [void][IO.Directory]::CreateDirectory($Path)
        return $Path
    }
}
function Set-ReportDeskFile {
    param([string]$Source, [string]$Destination, [string]$ExpectedHash)
    $script:TestCopyCount++
    if ($script:TestFailCopyAt -eq $script:TestCopyCount) {
        $script:TestFailCopyAt = 0
        throw 'Injected file replacement failure.'
    }
    if ($script:TestMutateSourceAt -eq $script:TestCopyCount) {
        Assert-True ($Source.StartsWith($script:TestCurrentFixture.Root + '\', [StringComparison]::OrdinalIgnoreCase)) 'Source race injection must target a disposable fixture package.'
        [IO.File]::AppendAllText($Source, "`r`n// simulated source replacement after preflight")
        $script:TestSourceMutationObserved = $true
    }
    & $script:TestOriginalSetFile -Source $Source -Destination $Destination -ExpectedHash $ExpectedHash
}
function Stop-Service { $script:TestIisMutationCalls++; throw 'Forbidden service mutation in updater.' }
function Start-Service { $script:TestIisMutationCalls++; throw 'Forbidden service mutation in updater.' }
function Restart-Service { $script:TestIisMutationCalls++; throw 'Forbidden service mutation in updater.' }
function Stop-WebAppPool { $script:TestIisMutationCalls++; throw 'Forbidden app pool mutation in updater.' }
function Start-WebAppPool { $script:TestIisMutationCalls++; throw 'Forbidden app pool mutation in updater.' }
function Restart-WebAppPool { $script:TestIisMutationCalls++; throw 'Forbidden app pool mutation in updater.' }
function Stop-Website { $script:TestIisMutationCalls++; throw 'Forbidden website mutation in updater.' }
function Start-Website { $script:TestIisMutationCalls++; throw 'Forbidden website mutation in updater.' }
function iisreset { $script:TestIisMutationCalls++; throw 'Forbidden IIS reset in updater.' }

function Set-TestContext($Fixture) {
    $script:TestCurrentFixture = $Fixture
    $script:TestCopyCount = 0
    $script:TestFailCopyAt = 0
    $script:TestHealthCount = 0
    $script:TestHealthFailureAt = 0
    $script:TestOfflineCount = 0
    $script:TestOfflineFailure = $false
    $script:TestMutateSourceAt = 0
    $script:TestSourceMutationObserved = $false
    $script:TestBackupTrustChecks = 0
}
function Invoke-TestUpdate($Fixture, [string]$Bundle = $PackageDirectory, [switch]$CheckOnly) {
    Invoke-ReportDeskWebUpdate -SitePath $Fixture.Site -PackageDirectory $Bundle -BackupRoot $Fixture.Backups -HealthUrl 'http://127.0.0.1:38443/' -CheckOnly:$CheckOnly
}
function Invoke-TestRollback($Fixture, [string]$Backup) {
    Invoke-ReportDeskWebUpdate -SitePath $Fixture.Site -PackageDirectory $PackageDirectory -BackupRoot $Fixture.Backups -HealthUrl 'http://127.0.0.1:38443/' -Rollback -BackupDirectory $Backup
}
function Assert-NoTestOffline($Fixture) {
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Fixture.Site 'app_offline.htm'))) 'Updater must remove only its own offline file on completion.'
}

Invoke-TestCase 'check-only leaves site, config, data, reports and neighbor untouched' {
    $f = New-TestFixture 'check-only'; Set-TestContext $f
    $result = Invoke-TestUpdate $f -CheckOnly
    Assert-True ($result.Status -eq 'Checked') 'Expected Checked status.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Check-only changed site files'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
    Assert-True ($script:TestOfflineCount -eq 0) 'Check-only must never enter maintenance.'
}
Invoke-TestCase 'appSettings clear and remove directives reject without rewriting site configuration' {
    foreach ($directive in @('<clear />','<remove key="InheritedSetting" />')) {
        $name = if ($directive.StartsWith('<clear')) { 'config-clear' } else { 'config-remove' }
        $f = New-TestFixture $name; Set-TestContext $f
        $configPath = Join-Path $f.Site 'Web.config'
        $config = [IO.File]::ReadAllText($configPath).Replace('<appSettings>', ('<appSettings>'+ $directive))
        Write-TestText $configPath $config
        $before = Get-TestSnapshot $f.Site
        Assert-TestFailure { Invoke-TestUpdate $f } 'clear/remove|effective.configuration'
        Assert-TestSnapshot $before $f.Site 'Configuration rejection must not rewrite appSettings'
        Assert-TestSnapshot $f.DataBefore $f.Data 'Configuration rejection must preserve connection settings'
        Assert-TestSnapshot $f.ReportsBefore $f.Reports 'Configuration rejection must preserve HIS files'
        Assert-TestSnapshot $f.OtherBefore $f.Other 'Configuration rejection must preserve other projects'
        Assert-NoTestOffline $f
        Assert-True ($script:TestOfflineCount -eq 0) 'Complex configuration must reject before maintenance.'
    }
}
Invoke-TestCase 'update exact allowlist preserves Core, Oracle, config, data and shared-pool neighbor' {
    $f = New-TestFixture 'successful-update'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    Assert-True ($result.Status -eq 'Updated') 'Expected Updated status.'
    Assert-True (Test-Path -LiteralPath (Join-Path $result.BackupDirectory 'journal.json')) 'Missing rollback journal.'
    $payload = Join-Path $PackageDirectory 'payload'
    $updatedPaths = @{}
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
        $relative = $file.FullName.Substring($payload.Length+1)
        $updatedPaths[$relative] = $true
        Assert-True ((Get-TestHash (Join-Path $f.Site $relative)) -eq (Get-TestHash $file.FullName)) ('Updated payload differs: '+$relative)
    }
    foreach ($relative in $f.SiteBefore.Keys) {
        if (-not $updatedPaths.ContainsKey($relative)) {
            Assert-True ((Get-TestHash (Join-Path $f.Site $relative)) -eq $f.SiteBefore[$relative]) ('Non-payload site file changed: '+$relative)
        }
    }
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
    Assert-True ($script:TestOfflineCount -gt 0) 'Maintenance confirmation was not performed.'
    Assert-True ($script:TestHealthCount -ge 2) 'Pre/post HTTP health checks were not performed.'
    $script:TestSuccessFixture = $f
    $script:TestSuccessBackup = $result.BackupDirectory
}
Invoke-TestCase 'manual rollback restores old bytes and removes only introduced payload files' {
    $f = $script:TestSuccessFixture; Set-TestContext $f
    Assert-True (Test-Path -LiteralPath (Join-Path $f.Site 'UI\local-directory.js')) 'New file missing before rollback.'
    $result = Invoke-TestRollback $f $script:TestSuccessBackup
    Assert-True ($result.Status -eq 'RolledBack') 'Expected RolledBack status.'
    Assert-True ($script:TestBackupTrustChecks -gt 0) 'Rollback must invoke its backup trust boundary before consuming the journal.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Rollback must exactly restore original site files'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'wrong Core rejects before site mutation' {
    $f = New-TestFixture 'wrong-core'; Set-TestContext $f
    [IO.File]::AppendAllText((Join-Path $f.Site 'bin\ReportDesk.Core.dll'), 'CORRUPT TEST CORE')
    $before = Get-TestSnapshot $f.Site
    Assert-TestFailure { Invoke-TestUpdate $f } 'Core|baseline|hash'
    Assert-TestSnapshot $before $f.Site 'Core rejection must preserve original files'
    Assert-NoTestOffline $f
    Assert-True ($script:TestOfflineCount -eq 0) 'Core mismatch must reject before maintenance.'
}
Invoke-TestCase 'corrupt bundle rejects even if mutable manifest is changed to match corruption' {
    $f = New-TestFixture 'corrupt-bundle'; Set-TestContext $f
    $bundle = Join-Path $f.Root 'CorruptPackage'; Copy-TestTree $PackageDirectory $bundle
    $bad = Join-Path $bundle 'payload\UI\web.js'; [IO.File]::AppendAllText($bad, "`r`n// corrupt fixture")
    $manifest = Get-Content -LiteralPath (Join-Path $bundle 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($entry in $manifest) { if ($entry.path -eq 'payload/UI/web.js') { $entry.sha256 = Get-TestHash $bad } }
    Write-TestText (Join-Path $bundle 'manifest.json') ($manifest | ConvertTo-Json -Depth 10)
    Assert-TestFailure { Invoke-TestUpdate $f $bundle } 'hash|payload|package|bundle'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Corrupt package rejection changed site files'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'unexpected payload file is rejected before mutation' {
    $f = New-TestFixture 'unknown-payload'; Set-TestContext $f
    $bundle = Join-Path $f.Root 'UnknownPackage'; Copy-TestTree $PackageDirectory $bundle
    Write-TestText (Join-Path $bundle 'payload\Web.config') '<configuration />'
    Assert-TestFailure { Invoke-TestUpdate $f $bundle } 'payload|allow|unexpected|package'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Unexpected payload rejection changed site files'
    Assert-NoTestOffline $f
}
Invoke-TestCase 'partial replacement failure automatically restores exact previous files' {
    $f = New-TestFixture 'copy-failure'; Set-TestContext $f
    $script:TestFailCopyAt = 9
    Assert-TestFailure { Invoke-TestUpdate $f } 'Injected|rollback|restore|failed'
    Assert-True ($script:TestCopyCount -gt 9) 'Rollback did not replace any previously changed files.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Automatic rollback did not restore exact site'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'post-update HTTP failure restores exact previous files' {
    $f = New-TestFixture 'health-failure'; Set-TestContext $f
    $script:TestHealthFailureAt = 2
    Assert-TestFailure { Invoke-TestUpdate $f } 'Injected|health|rollback|restore|failed'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Health failure rollback did not restore exact site'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'source replacement after preflight cannot install unpinned bytes' {
    $f = New-TestFixture 'source-race'; Set-TestContext $f
    $bundle = Join-Path $f.Root 'MutablePackage'; Copy-TestTree $PackageDirectory $bundle
    $script:TestMutateSourceAt = 3
    Assert-TestFailure { Invoke-TestUpdate $f $bundle } 'hash|staged|pin|package|failed|restored'
    Assert-True $script:TestSourceMutationObserved 'Source race injection was not reached.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Package race rollback must restore exact previous files'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'manual rollback refuses concurrent payload edits without overwriting them' {
    $f = New-TestFixture 'rollback-payload-conflict'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    [IO.File]::AppendAllText((Join-Path $f.Site 'UI\web.js'), "`r`n// concurrent maintenance edit")
    $before = Get-TestSnapshot $f.Site
    Assert-TestFailure { Invoke-TestRollback $f $result.BackupDirectory } 'changed|conflict|hash|match'
    Assert-TestSnapshot $before $f.Site 'Rollback conflict must not overwrite payload edits'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'manual rollback refuses changed connection settings and preserves their exact bytes' {
    $f = New-TestFixture 'rollback-config-conflict'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    Write-TestText (Join-Path $f.Data 'config\connection.json') '{"TestOnlyMarker":"new-settings-after-update","SavePassword":false}'
    $beforeSite = Get-TestSnapshot $f.Site; $beforeData = Get-TestSnapshot $f.Data
    Assert-TestFailure { Invoke-TestRollback $f $result.BackupDirectory } 'changed|conflict|config|protected|match'
    Assert-TestSnapshot $beforeSite $f.Site 'Rollback config conflict must leave site alone'
    Assert-TestSnapshot $beforeData $f.Data 'Rollback must not replace newer database settings'
    Assert-NoTestOffline $f
}
Invoke-TestCase 'foreign app_offline is preserved and update refuses before writes' {
    $f = New-TestFixture 'existing-offline'; Set-TestContext $f
    Write-TestText (Join-Path $f.Site 'app_offline.htm') 'Existing maintenance by another operator'
    $before = Get-TestSnapshot $f.Site
    Assert-TestFailure { Invoke-TestUpdate $f } 'offline|maintenance'
    Assert-TestSnapshot $before $f.Site 'Foreign maintenance marker must never be removed or overwritten'
    Assert-TestPreserved $f
}
Invoke-TestCase 'offline HTTP confirmation failure restores original availability without replacing payload' {
    $f = New-TestFixture 'offline-unconfirmed'; Set-TestContext $f
    $script:TestOfflineFailure = $true
    Assert-TestFailure { Invoke-TestUpdate $f } 'offline|Injected|rollback|failed'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Unconfirmed offline must not change site bytes'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'backup root inside website is rejected' {
    $f = New-TestFixture 'backup-overlap'; Set-TestContext $f
    $f.Backups = Join-Path $f.Site 'Backups'
    Assert-TestFailure { Invoke-TestUpdate $f } 'backup|overlap|inside|outside'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Backup overlap rejection changed site files'
    Assert-NoTestOffline $f
}
Invoke-TestCase 'website ancestor junction is rejected' {
    $f = New-TestFixture 'ancestor-junction'; Set-TestContext $f
    $link = Join-Path $f.Root 'SiteParentLink'
    [void](New-Item -ItemType Junction -Path $link -Target $f.Root)
    $linkedSite = Join-Path $link 'ReportDesk'
    Assert-TestFailure { Invoke-ReportDeskWebUpdate -SitePath $linkedSite -PackageDirectory $PackageDirectory -BackupRoot $f.Backups -HealthUrl 'http://127.0.0.1:38443/' } 'junction|link|reparse'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Junction rejection changed site files'
    Assert-NoTestOffline $f
}
Invoke-TestCase 'concurrent updater is rejected by a cross-thread named mutex' {
    $f = New-TestFixture 'concurrent-updater'; Set-TestContext $f
    $ready = New-Object Threading.ManualResetEventSlim($false)
    $release = New-Object Threading.ManualResetEventSlim($false)
    $worker = [PowerShell]::Create()
    $null = $worker.AddScript({
        param($Name, $Ready, $Release)
        $lock = New-Object Threading.Mutex($false, $Name)
        $held = $false
        try {
            $held = $lock.WaitOne(1000)
            if (-not $held) { throw 'Fixture could not acquire updater lock.' }
            $Ready.Set()
            [void]$Release.Wait(15000)
        } finally {
            if ($held) { $lock.ReleaseMutex() }
            $lock.Dispose()
        }
    }).AddArgument((Get-ReportDeskMutexName $f.Site)).AddArgument($ready).AddArgument($release)
    $pending = $worker.BeginInvoke()
    try {
        Assert-True ($ready.Wait(5000)) 'Mutex fixture did not become ready.'
        Assert-TestFailure { Invoke-TestUpdate $f } 'another updater|operating|lock'
        Assert-TestSnapshot $f.SiteBefore $f.Site 'Concurrent updater must not mutate site files'
        Assert-NoTestOffline $f
    } finally {
        $release.Set()
        $null = $worker.EndInvoke($pending)
        $worker.Dispose(); $ready.Dispose(); $release.Dispose()
    }
}
Invoke-TestCase 'recorded interrupted partial update recovers with owned maintenance page' {
    $f = New-TestFixture 'interrupted-update'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    $journalPath = Join-Path $result.BackupDirectory 'journal.json'
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    # Model a mixed old/new directory at interruption, using genuine backed-up
    # bytes. The journal and original backup were produced by a real test update.
    Copy-Item -LiteralPath (Join-Path $result.BackupDirectory 'files\site\UI\web.js') -Destination (Join-Path $f.Site 'UI\web.js') -Force
    $journal.State = 'Updating'
    Write-TestText $journalPath ($journal | ConvertTo-Json -Depth 12)
    New-ReportDeskOffline -SitePath $f.Site -Marker $journal.Marker
    $staged = @(Get-ReportDeskStageFiles -Paths (Get-ReportDeskPaths $f.Site) -Marker $journal.Marker)[0]
    Write-TestText $staged 'A partially copied staging file left by process interruption'
    $rollback = Invoke-TestRollback $f $result.BackupDirectory
    Assert-True ($rollback.Status -eq 'RolledBack') 'Interrupted update was not recovered.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Interrupted update recovery must restore exact previous files'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'interruption during Preparing safely cancels maintenance without needing a complete backup' {
    $f = New-TestFixture 'interrupted-preparing'; Set-TestContext $f
    $script:TestOfflineFailure = $true
    # Leave recovery pending by also failing the early-cleanup availability probe.
    $script:TestHealthFailureAt = 2
    Assert-TestFailure { Invoke-TestUpdate $f } 'offline|Injected|failed'
    $backup = @(Get-ChildItem -LiteralPath $f.Backups -Directory)[0].FullName
    $journal = Get-Content -LiteralPath (Join-Path $backup 'journal.json') -Raw | ConvertFrom-Json
    Assert-True ($journal.State -eq 'Preparing') 'Fixture must represent an incomplete backup, before any application write.'
    Set-TestContext $f
    New-ReportDeskOffline -SitePath $f.Site -Marker $journal.Marker
    $result = Invoke-TestRollback $f $backup
    Assert-True ($result.Status -eq 'Cancelled') 'Preparing transaction should cancel rather than claim restored files.'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Preparing cancellation must leave site bytes untouched'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'Preparing recovery clears only its exact partially written offline staging file' {
    $f = New-TestFixture 'interrupted-offline-staging'; Set-TestContext $f
    $script:TestOfflineFailure = $true
    $script:TestHealthFailureAt = 2
    Assert-TestFailure { Invoke-TestUpdate $f } 'offline|Injected|failed'
    $backup = @(Get-ChildItem -LiteralPath $f.Backups -Directory)[0].FullName
    $journal = Get-Content -LiteralPath (Join-Path $backup 'journal.json') -Raw | ConvertFrom-Json
    Assert-True ($journal.State -eq 'Preparing') 'Offline staging fixture must use Preparing journal state.'
    Set-TestContext $f
    Write-TestText (Join-Path $f.Site '.reportdesk-offline-unrelated-operation.tmp') 'Preserve another operation temporary file'
    $expected = Get-TestSnapshot $f.Site
    $staged = Join-Path $f.Site ('.reportdesk-offline-' + $journal.Marker + '.tmp')
    Write-TestText $staged 'Partially written maintenance page before rename'
    $result = Invoke-TestRollback $f $backup
    Assert-True ($result.Status -eq 'Cancelled') 'Offline staging interruption should cancel Preparing transaction.'
    Assert-TestSnapshot $expected $f.Site 'Preparing recovery must remove only its own exact staging file'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'real backup trust validator rejects ordinary inherited user-writable test directory' {
    $f = New-TestFixture 'untrusted-backup'
    Assert-TestFailure { & $script:TestOriginalBackupTrusted -Directory $f.Root } 'ACL|owner|Administrators|trusted|protected|private|backup'
    Assert-TestSnapshot $f.SiteBefore $f.Site 'Untrusted backup check must be read-only'
}
Invoke-TestCase 'rollback refuses foreign maintenance without removing it' {
    $f = New-TestFixture 'rollback-foreign-offline'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    Write-TestText (Join-Path $f.Site 'app_offline.htm') 'Another operator is maintaining this application'
    $before = Get-TestSnapshot $f.Site
    Assert-TestFailure { Invoke-TestRollback $f $result.BackupDirectory } 'another operation|maintenance|offline'
    Assert-TestSnapshot $before $f.Site 'Rollback must not delete another operation maintenance page'
    Assert-TestPreserved $f
}
Invoke-TestCase 'corrupted rollback backup rejects before changing live payload' {
    $f = New-TestFixture 'corrupt-backup'; Set-TestContext $f
    $result = Invoke-TestUpdate $f
    [IO.File]::AppendAllText((Join-Path $result.BackupDirectory 'files\site\bin\ReportDesk.Web.dll'), 'CORRUPT BACKUP FIXTURE')
    $before = Get-TestSnapshot $f.Site
    Assert-TestFailure { Invoke-TestRollback $f $result.BackupDirectory } 'backup|hash|mismatch'
    Assert-TestSnapshot $before $f.Site 'Invalid rollback backup must leave live payload and availability unchanged'
    Assert-TestPreserved $f
    Assert-NoTestOffline $f
}
Invoke-TestCase 'IIS mapping overlap and multiple root aliases are rejected by pure inventory checks' {
    $f = New-TestFixture 'iis-overlap'
    $main = [pscustomobject]@{PhysicalPath=$f.Site;VirtualPath='/';SiteName='One';AppPath='/';PoolName='Shared'}
    $neighbor = [pscustomobject]@{PhysicalPath=$f.Other;VirtualPath='/';SiteName='Two';AppPath='/';PoolName='Shared'}
    $selected = Test-ReportDeskIisInventory -SitePath $f.Site -Mappings @($main,$neighbor)
    Assert-True ($selected.SiteName -eq 'One') 'Independent sites sharing a pool should be recognized safely.'
    $nested = [pscustomobject]@{PhysicalPath=(Join-Path $f.Site 'child');VirtualPath='/';SiteName='Nested';AppPath='/';PoolName='Shared'}
    Assert-TestFailure { Test-ReportDeskIisInventory -SitePath $f.Site -Mappings @($main,$nested) } 'overlap'
    $alias = [pscustomobject]@{PhysicalPath=$f.Site;VirtualPath='/';SiteName='Alias';AppPath='/';PoolName='Other'}
    Assert-TestFailure { Test-ReportDeskIisInventory -SitePath $f.Site -Mappings @($main,$alias) } 'exactly one'
}
Invoke-TestCase 'binding URL candidates preserve application path and limit wildcard TLS to supplied local IPs' {
    $bindings = @(
        [pscustomobject]@{Protocol='https';BindingInformation='*:8443:'},
        [pscustomobject]@{Protocol='https';BindingInformation='*:8443:reportdesk.test'},
        [pscustomobject]@{Protocol='http';BindingInformation='*:8080:'},
        [pscustomobject]@{Protocol='net.tcp';BindingInformation='808:*'}
    )
    $urls = @(Get-ReportDeskBindingUrls -Bindings $bindings -AppPath '/reports' -LocalIPv4Addresses @('172.18.20.65','172.18.20.66'))
    Assert-True ($urls.Count -eq 4) 'Unexpected or unsafe binding URL candidate.'
    Assert-True ($urls -contains 'https://reportdesk.test:8443/reports/') 'Named HTTPS binding URL lost application path.'
    Assert-True ($urls -contains 'https://172.18.20.65:8443/reports/' -and $urls -contains 'https://172.18.20.66:8443/reports/') 'Wildcard TLS candidates must come only from local IPs.'
    Assert-True ($urls -contains 'http://localhost:8080/reports/') 'HTTP wildcard binding should use local host.'
    Assert-True ((ConvertTo-ReportDeskBaseUrl 'https://127.0.0.1:8443/reports') -eq 'https://127.0.0.1:8443/reports/') 'Base URL normalization differs.'
    foreach ($url in @('file:///C:/inetpub','https://user:pass@127.0.0.1/','https://127.0.0.1/?x=1','https://127.0.0.1/#fragment')) {
        Assert-TestFailure { ConvertTo-ReportDeskBaseUrl $url } 'URL|HTTP|credentials|query|fragment'
    }
}
Invoke-TestCase 'health URL must match selected application and local IIS binding' {
    $context = [pscustomobject]@{AppPath='/reports';Bindings=@([pscustomobject]@{Protocol='http';BindingInformation='127.0.0.1:38443:'})}
    Assert-True ((Assert-ReportDeskHealthBinding -BaseUrl 'http://127.0.0.1:38443/reports/' -Context $context) -eq 'http://127.0.0.1:38443/reports/') 'Matching local application URL should pass.'
    Assert-TestFailure { Assert-ReportDeskHealthBinding -BaseUrl 'http://127.0.0.1:38443/' -Context $context } 'application path'
    Assert-TestFailure { Assert-ReportDeskHealthBinding -BaseUrl 'http://127.0.0.1:38444/reports/' -Context $context } 'bindings'
}
Invoke-TestCase 'updater never invokes IIS, app pool or service mutation commands' {
    Assert-True ($script:TestIisMutationCalls -eq 0) 'A forbidden service/IIS mutation was attempted.'
    foreach ($file in @('Apply-WebUpdate.ps1','WebUpdate.Iis.ps1')) {
        $tokens=$null; $errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('scripts\'+$file)), [ref]$tokens, [ref]$errors)
        Assert-True ($errors.Count -eq 0) ($file+' has PowerShell parse errors')
        foreach ($command in $ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst]}, $true)) {
            Assert-True ($command.GetCommandName() -notmatch '(?i)^(?:iisreset(?:\.exe)?|(?:Stop|Start|Restart)-(?:Service|Website|WebAppPool)|Set-WebConfiguration(?:Property)?|appcmd(?:\.exe)?)$') ($file+' contains an IIS/service mutation command')
        }
        foreach ($method in $ast.FindAll({param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst]}, $true)) {
            Assert-True ($method.Member.Value -ne 'CommitChanges') ($file+' contains an IIS CommitChanges call')
        }
    }
}

$summary = [ordered]@{
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    TestDirectory=$OutputDirectory
    PackageDirectory=$PackageDirectory
    CoreBaseline=$script:ExpectedCore
    Tests=@($script:Results.ToArray())
    Passed=@($script:Results | Where-Object {$_.status -eq 'PASS'}).Count
    Failed=@($script:Results | Where-Object {$_.status -eq 'FAIL'}).Count
    RealIisAcceptance='NOT RUN - isolated filesystem fixtures, IIS/HTTP boundaries mocked'
    BackupAclAcceptance=$(if ($script:TestIsAdministrator) {'ACL initialization executed on isolated fixture directories; rollback ACL trust boundary mocked'} else {'NOT RUN - process is not elevated; ACL initialization and rollback ACL trust boundary mocked only in tests'})
    RealOracleAcceptance='NOT RUN - no database calls'
}
Write-TestText (Join-Path $OutputDirectory 'results.json') ($summary | ConvertTo-Json -Depth 12)
Write-Host ('Results: '+(Join-Path $OutputDirectory 'results.json'))
if ($summary.Failed -gt 0) { throw ('Web updater checks failed: '+$summary.Failed) }
Write-Host ('Web updater checks passed: '+$summary.Passed)
