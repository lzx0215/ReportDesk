[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$OutputDirectory
)

# Windows PowerShell 5.1. Reuse the existing genuine old-package fixtures without
# running the original suite. IIS/HTTP/backup ACL boundaries are mocked; the
# updater's maintenance creation, ownership checks, cleanup and journal are real.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repo 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalHis-20261106'
}
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo ('artifacts\verification\web-update-maintenance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
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

# Import only named top-level fixture helpers. Never execute the old test body.
$fixtureNames = @('Assert-True', 'Get-TestHash', 'Write-TestText', 'Copy-TestTree',
    'Get-TestSnapshot', 'Assert-TestSnapshot', 'Assert-TestFailure',
    'New-TestFixture', 'Assert-TestPreserved', 'Invoke-TestCase')
$fixtureTokens = $null; $fixtureErrors = $null
$fixtureAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'web-update-checks.ps1'), [ref]$fixtureTokens, [ref]$fixtureErrors)
if ($fixtureErrors.Count -gt 0) { throw 'Original fixture helper file has parse errors.' }
foreach ($fixtureName in $fixtureNames) {
    $definitions = @($fixtureAst.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $fixtureName
    })
    if ($definitions.Count -ne 1) { throw ('Fixture helper missing or ambiguous: ' + $fixtureName) }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}

. (Join-Path $repo 'scripts\Apply-WebUpdate.ps1') -PackageDirectory $PackageDirectory
$script:RealOfflineCheck = (Get-Command Test-ReportDeskOffline).ScriptBlock
$script:MaintenanceFixture = $null
$script:MaintenanceMode = 'Timeout'
$script:MaintenanceHttpCalls = 0
$script:MaintenanceHealthCalls = 0
$script:MaintenanceHealthFail = $false
$script:MaintenanceCopyCalls = 0
$script:MaintenanceIisMutationCalls = 0
$script:PrivateBodySentinel = 'NEVER_LOG_RESPONSE_BODY_OR_CSRF_948c7c66470e'
$script:ForeignOfflineText = 'Another operator maintenance page - preserve exact bytes'

function Assert-ReportDeskAdministrator { }
function Assert-ReportDeskBackupTrusted { param([string]$Directory) }
function Initialize-ReportDeskBackupRoot {
    param([string]$Path)
    [void][IO.Directory]::CreateDirectory($Path)
    return $Path
}
function Get-ReportDeskIisContext {
    param([string]$SitePath)
    return [pscustomobject]@{
        SitePath=$SitePath; SiteName='MaintenanceFixture'; AppPath='/'; PoolName='FixtureSharedPool'
        SnapshotHash=('A' * 64); CandidateBaseUrls=@('http://127.0.0.1:38443/')
        Bindings=@([pscustomobject]@{Protocol='http';BindingInformation='127.0.0.1:38443:'})
        Mappings=@(
            [pscustomobject]@{PhysicalPath=$SitePath;VirtualPath='/';SiteName='MaintenanceFixture';AppPath='/';PoolName='FixtureSharedPool'},
            [pscustomobject]@{PhysicalPath=$script:MaintenanceFixture.Other;VirtualPath='/';SiteName='OtherFixture';AppPath='/';PoolName='FixtureSharedPool'}
        )
    }
}
function Test-ReportDeskHealth {
    param([string]$BaseUrl)
    $script:MaintenanceHealthCalls++
    if ($script:MaintenanceHealthFail -and $script:MaintenanceHealthCalls -gt 1) {
        throw 'Injected cleanup health check failure.'
    }
    return [pscustomobject]@{BaseUrl=$BaseUrl;Healthy=$true;HomeStatus=200;SessionStatus=200}
}
function Invoke-ReportDeskHttp {
    param([string]$Url, [int]$TimeoutMilliseconds, [int]$MaximumBytes)
    $script:MaintenanceHttpCalls++
    if ($script:MaintenanceMode -eq 'Foreign' -and $script:MaintenanceHttpCalls -eq 1) {
        Write-TestText (Join-Path $script:MaintenanceFixture.Site 'app_offline.htm') $script:ForeignOfflineText
    }
    $body = '<html><body>' + $script:PrivateBodySentinel + '</body></html>'
    return [pscustomobject]@{StatusCode=200;ContentType='text/html';Body=$body;Bytes=[Text.Encoding]::UTF8.GetBytes($body)}
}
function Test-ReportDeskOffline {
    [CmdletBinding()]
    param(
        [string]$BaseUrl, [string]$Marker,
        [int]$TimeoutSeconds=30, [int]$PollMilliseconds=500,
        [int]$RequiredConsecutivePasses=2, [scriptblock]$AssertMaintenance
    )
    # Shorten only the deadline; use the production polling implementation and
    # the real ownership callback supplied by the updater.
    & $script:RealOfflineCheck -BaseUrl $BaseUrl -Marker $Marker -TimeoutSeconds 1 -PollMilliseconds 50 -RequiredConsecutivePasses $RequiredConsecutivePasses -AssertMaintenance $AssertMaintenance
}
function Set-ReportDeskFile {
    param([string]$Source, [string]$Destination, [string]$ExpectedHash)
    $script:MaintenanceCopyCalls++
    throw 'Payload writes must never be reached by a failed maintenance gate.'
}
function Stop-Service { $script:MaintenanceIisMutationCalls++; throw 'Forbidden service mutation.' }
function Start-Service { $script:MaintenanceIisMutationCalls++; throw 'Forbidden service mutation.' }
function Restart-Service { $script:MaintenanceIisMutationCalls++; throw 'Forbidden service mutation.' }
function Stop-WebAppPool { $script:MaintenanceIisMutationCalls++; throw 'Forbidden pool mutation.' }
function Start-WebAppPool { $script:MaintenanceIisMutationCalls++; throw 'Forbidden pool mutation.' }
function Restart-WebAppPool { $script:MaintenanceIisMutationCalls++; throw 'Forbidden pool mutation.' }
function Stop-Website { $script:MaintenanceIisMutationCalls++; throw 'Forbidden site mutation.' }
function Start-Website { $script:MaintenanceIisMutationCalls++; throw 'Forbidden site mutation.' }
function iisreset { $script:MaintenanceIisMutationCalls++; throw 'Forbidden IIS reset.' }

function Set-MaintenanceContext($Fixture, [string]$Mode='Timeout', [bool]$HealthFail=$false) {
    $script:MaintenanceFixture=$Fixture; $script:MaintenanceMode=$Mode
    $script:MaintenanceHttpCalls=0; $script:MaintenanceHealthCalls=0
    $script:MaintenanceHealthFail=$HealthFail; $script:MaintenanceCopyCalls=0
}
function Invoke-MaintenanceUpdate($Fixture) {
    Invoke-ReportDeskWebUpdate -SitePath $Fixture.Site -PackageDirectory $PackageDirectory -BackupRoot $Fixture.Backups -HealthUrl 'http://127.0.0.1:38443/'
}
function Get-MaintenanceJournal($Fixture) {
    $directories = @(Get-ChildItem -LiteralPath $Fixture.Backups -Directory)
    Assert-True ($directories.Count -eq 1) 'Exactly one recovery directory is expected.'
    $journalPath = Join-Path $directories[0].FullName 'journal.json'
    return [pscustomobject]@{Directory=$directories[0].FullName;Path=$journalPath;Journal=(Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json)}
}
function Assert-NoMaintenanceMutation($Fixture, [switch]$AllowForeignOffline) {
    Assert-True ($script:MaintenanceCopyCalls -eq 0) 'Payload replacement must not occur without confirmed maintenance.'
    Assert-TestPreserved $Fixture
    if ($AllowForeignOffline) {
        $actual = Get-TestSnapshot $Fixture.Site
        Assert-True ($actual.Count -eq $Fixture.SiteBefore.Count + 1) 'Only the foreign maintenance file may have been added.'
        foreach ($key in $Fixture.SiteBefore.Keys) {
            Assert-True ($actual[$key] -eq $Fixture.SiteBefore[$key]) ('Existing site file changed: ' + $key)
        }
        Assert-True ([IO.File]::ReadAllText((Join-Path $Fixture.Site 'app_offline.htm')) -ceq $script:ForeignOfflineText) 'Foreign maintenance was changed or deleted.'
    } else {
        Assert-TestSnapshot $Fixture.SiteBefore $Fixture.Site 'Maintenance failure must leave original site files unchanged'
    }
}

function Assert-MaintenanceEvidence($Record) {
    Assert-True ($null -ne $Record.Journal.PSObject.Properties['OfflineEvidence']) 'Failure evidence must be saved in the recovery journal.'
    $evidence = $Record.Journal.OfflineEvidence
    Assert-True ($evidence.Attempts -ge 1) 'Journal must contain actual probe attempts.'
    Assert-True ($evidence.RequiredConsecutivePasses -eq 2) 'The strict default stability requirement must be retained.'
    Assert-True (@($evidence.LastResponses).Count -gt 0) 'Journal must retain safe response summaries.'
    Assert-True (@($evidence.RecentRounds).Count -le 12) 'Probe diagnostics must remain bounded.'
    Assert-True (-not ([IO.File]::ReadAllText($Record.Path).Contains($script:PrivateBodySentinel))) 'Response bodies or session contents leaked into the journal.'
    foreach ($response in @($evidence.LastResponses)) {
        Assert-True ($null -eq $response.PSObject.Properties['Body']) 'Response body must never be a diagnostic field.'
        Assert-True ($null -eq $response.PSObject.Properties['Url']) 'Probe query values must not be recorded as full URLs.'
    }
}

Invoke-TestCase 'maintenance timeout saves safe evidence, cancels cleanly and never writes payload' {
    $f = New-TestFixture 'timeout-cleanup-success'; Set-MaintenanceContext $f
    Assert-TestFailure { Invoke-MaintenanceUpdate $f } 'offline|maintenance|503|confirm'
    Assert-NoMaintenanceMutation $f
    $record = Get-MaintenanceJournal $f
    Assert-True ($record.Journal.State -eq 'Cancelled') 'Healthy cleanup must record cancellation, not a successful update or a rollback.'
    Assert-True ($record.Journal.Before.Count -eq 0) 'No complete backup may be claimed before the maintenance gate.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $record.Directory 'files'))) 'Payload backup must not start before confirmed maintenance.'
    Assert-True ($script:MaintenanceHealthCalls -ge 2) 'Cleanup must check original application availability.'
    Assert-MaintenanceEvidence $record
    Assert-True ($record.Journal.OfflineEvidence.Attempts -gt 1) 'The main updater must use the bounded retry helper, not a single probe.'
    foreach ($response in @($record.Journal.OfflineEvidence.LastResponses)) {
        Assert-True ($response.StatusCode -eq 200 -and -not $response.MarkerMatched) 'Actual mismatch status must be recorded without loosening the gate.'
    }
}

Invoke-TestCase 'failed cleanup health leaves Preparing journal and supports later explicit cancellation' {
    $f = New-TestFixture 'timeout-cleanup-unconfirmed'; Set-MaintenanceContext $f -HealthFail $true
    Assert-TestFailure { Invoke-MaintenanceUpdate $f } 'offline|maintenance|503|confirm|health'
    Assert-NoMaintenanceMutation $f
    $record = Get-MaintenanceJournal $f
    Assert-True ($record.Journal.State -eq 'Preparing') 'Unconfirmed availability must remain recoverable rather than be declared cancelled.'
    Assert-True ($record.Journal.Cleanup.MaintenanceRemoved -and -not $record.Journal.Cleanup.HealthVerified) 'Cleanup evidence must distinguish file removal from health confirmation.'
    Assert-MaintenanceEvidence $record
    Set-MaintenanceContext $f
    $result = Invoke-ReportDeskWebUpdate -SitePath $f.Site -PackageDirectory $PackageDirectory -BackupRoot $f.Backups -HealthUrl 'http://127.0.0.1:38443/' -Rollback -BackupDirectory $record.Directory
    Assert-True ($result.Status -eq 'Cancelled') 'A Preparing transaction with no writes should cancel without restoring payload.'
    Assert-NoMaintenanceMutation $f
    $final = Get-MaintenanceJournal $f
    Assert-True ($final.Journal.State -eq 'Cancelled') 'Successful later cancellation must persist its terminal state.'
    Assert-True ($final.Journal.Cleanup.MaintenanceRemoved -and $final.Journal.Cleanup.HealthVerified) 'Later successful cancellation must refresh the earlier unconfirmed cleanup evidence.'
    Assert-True ($null -ne $final.Journal.PSObject.Properties['OfflineEvidence']) 'Manual cancellation must retain the original failure diagnosis.'
}

Invoke-TestCase 'foreign maintenance replacement during polling is preserved with no payload writes' {
    $f = New-TestFixture 'foreign-during-poll'; Set-MaintenanceContext $f -Mode 'Foreign'
    Assert-TestFailure { Invoke-MaintenanceUpdate $f } 'ownership|maintenance|offline|another|cleanup'
    Assert-NoMaintenanceMutation $f -AllowForeignOffline
    $record = Get-MaintenanceJournal $f
    Assert-True ($record.Journal.State -eq 'Preparing') 'Foreign maintenance must not be labelled removed or safely cancelled.'
    Assert-True (-not $record.Journal.Cleanup.MaintenanceRemoved) 'Cleanup must record that it did not remove foreign maintenance.'
    Assert-MaintenanceEvidence $record
    Assert-True ($record.Journal.OfflineEvidence.Phase -eq 'MaintenanceOwnershipFailed') 'Ownership loss must retain its specific failure category.'
    Assert-True ($script:MaintenanceHttpCalls -le 2) 'Ownership loss must abort promptly rather than retry until timeout.'
}

Invoke-TestCase 'preexisting foreign maintenance refuses before polling and is never removed' {
    $f = New-TestFixture 'preexisting-foreign'; Set-MaintenanceContext $f
    Write-TestText (Join-Path $f.Site 'app_offline.htm') $script:ForeignOfflineText
    Assert-TestFailure { Invoke-MaintenanceUpdate $f } 'maintenance|offline'
    Assert-NoMaintenanceMutation $f -AllowForeignOffline
    Assert-True ($script:MaintenanceHttpCalls -eq 0) 'A preexisting foreign maintenance page must reject before polling.'
}

Invoke-TestCase 'maintenance failure never invokes IIS, site, application pool or service mutations' {
    Assert-True ($script:MaintenanceIisMutationCalls -eq 0) 'A forbidden server-wide or neighboring-project mutation was attempted.'
}

$summary = [ordered]@{
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    TestDirectory=$OutputDirectory
    PackageDirectory=$PackageDirectory
    Tests=@($script:Results.ToArray())
    Passed=@($script:Results | Where-Object {$_.status -eq 'PASS'}).Count
    Failed=@($script:Results | Where-Object {$_.status -eq 'FAIL'}).Count
    RealIisAcceptance='NOT RUN - isolated filesystem fixtures with IIS and HTTP boundaries mocked'
    BackupAclAcceptance='NOT RUN - backup ACL initialization and trust boundaries mocked on disposable fixtures'
    RealOracleAcceptance='NOT RUN - no database calls'
}
Write-TestText (Join-Path $OutputDirectory 'results.json') ($summary | ConvertTo-Json -Depth 12)
Write-Host ('Results: ' + (Join-Path $OutputDirectory 'results.json'))
if ($summary.Failed -gt 0) { throw ('Maintenance updater checks failed: ' + $summary.Failed) }
Write-Host ('Maintenance updater checks passed: ' + $summary.Passed)
