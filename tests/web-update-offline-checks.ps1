#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)

# Isolated HTTP-boundary tests for the real offline gate. No IIS, website files,
# application pools, credentials, or databases are accessed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('artifacts\verification\web-update-offline-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$allowedOutput = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\verification')) + '\'
if (-not $OutputDirectory.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'Output must be a new directory under artifacts\verification.'
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$script:OfflineResults = New-Object 'System.Collections.Generic.List[object]'
$script:OfflineMarker = 'ReportDeskUpdate-0123456789abcdef0123456789abcdef'
$script:ResponseCanary = 'DO_NOT_OUTPUT_RESPONSE_PASSWORD_20261106'
$script:ExceptionCanary = 'DO_NOT_OUTPUT_NETWORK_TOKEN_20261106'

function Assert-OfflineTest([bool]$Value, [string]$Message) {
    if (-not $Value) { throw ('ASSERT: ' + $Message) }
}

function Invoke-OfflineTest([string]$Name, [scriptblock]$Action) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $script:OfflineResults.Add([pscustomobject]@{ Name=$Name; Status='PASS'; Seconds=[Math]::Round($clock.Elapsed.TotalSeconds,3) })
        Write-Host ('PASS: ' + $Name)
    } catch {
        $script:OfflineResults.Add([pscustomobject]@{ Name=$Name; Status='FAIL'; Seconds=[Math]::Round($clock.Elapsed.TotalSeconds,3); Error=$_.Exception.Message })
        Write-Host ('FAIL: ' + $Name + ' | ' + $_.Exception.Message)
    }
}

function Set-OfflineScenario([string]$Name) {
    $script:OfflineScenario = $Name
    $script:HomeRequests = 0
    $script:SessionRequests = 0
    $script:OfflineUrls = New-Object 'System.Collections.Generic.List[string]'
    $script:HttpTimeouts = New-Object 'System.Collections.Generic.List[int]'
}

function Install-OfflineHttpMock {
    function script:Invoke-ReportDeskHttp {
        [CmdletBinding()]
        param([string]$Url, [int]$TimeoutMilliseconds=10000, [int]$MaximumBytes=2097152)
        $script:OfflineUrls.Add($Url)
        $script:HttpTimeouts.Add($TimeoutMilliseconds)
        $uri = [Uri]$Url
        $sessionRoute = $uri.AbsolutePath.EndsWith('/api/session', [StringComparison]::Ordinal)
        if ($sessionRoute) { $script:SessionRequests++ } else { $script:HomeRequests++ }
        $status = 503
        $body = '<html><body>Maintenance<!-- ' + $script:OfflineMarker + ' --></body></html>'
        switch ($script:OfflineScenario) {
            'Delayed' {
                if (-not $sessionRoute -and $script:HomeRequests -eq 1) { $status=200; $body='<html>Previous application page</html>' }
            }
            'Generic503' { $body='<html>Generic server failure ' + $script:ResponseCanary + '</html>' }
            'WrongMarker' { $body='<html><!-- ReportDeskUpdate-ffffffffffffffffffffffffffffffff --></html>' }
            'TokenWith200' { $status=200 }
            'SessionStale' {
                if ($sessionRoute) { $status=200; $body='{"csrf":"' + $script:ResponseCanary + '","admin":true,"offline":false}' }
            }
            'TransientNetwork' {
                if (-not $sessionRoute -and $script:HomeRequests -eq 1) { throw $script:ExceptionCanary }
            }
            'PersistentNetwork' { throw $script:ExceptionCanary }
            'ResetConsecutive' {
                if ($sessionRoute -and $script:SessionRequests -eq 2) { $status=502; $body='proxy failure ' + $script:ResponseCanary }
            }
            'AlwaysReady' { }
            default { throw 'Unknown fixture scenario.' }
        }
        return [pscustomobject]@{StatusCode=$status;ContentType='text/html; charset=utf-8';Body=$body;Bytes=[Text.Encoding]::UTF8.GetBytes($body)}
    }
}

function Assert-OfflineEvidenceSafe($Evidence) {
    Assert-OfflineTest ($null -ne $Evidence) 'Expected non-null structured probe evidence.'
    if ($Evidence -is [string]) { $text = $Evidence; $null = $text | ConvertFrom-Json -ErrorAction Stop }
    else { $text = $Evidence | ConvertTo-Json -Depth 15 }
    Assert-OfflineTest (-not [string]::IsNullOrWhiteSpace($text)) 'Expected safe structured probe evidence.'
    Assert-OfflineTest (-not $text.Contains($script:ResponseCanary)) 'Response content leaked into probe evidence.'
    Assert-OfflineTest (-not $text.Contains($script:ExceptionCanary)) 'Raw network exception leaked into probe evidence.'
    return $text
}

function Invoke-OfflineExpectedRejection([string]$Scenario) {
    Set-OfflineScenario $Scenario
    $captured = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $null = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 1 -PollMilliseconds 10 -RequiredConsecutivePasses 2
    } catch { $captured = $_ }
    Assert-OfflineTest ($null -ne $captured) ('Unsafe offline response was accepted: ' + $Scenario)
    Assert-OfflineTest ($clock.Elapsed.TotalSeconds -lt 5) 'Bounded mocked rejection exceeded its deadline tolerance.'
    Assert-OfflineTest ($script:HomeRequests -gt 1) 'Gate rejected after one probe instead of bounded polling.'
    $evidence = $captured.Exception.Data['ReportDeskOfflineEvidence']
    $safeText = Assert-OfflineEvidenceSafe $evidence
    Assert-OfflineTest (-not $captured.Exception.Message.Contains($script:ResponseCanary)) 'Response content leaked into error text.'
    Assert-OfflineTest (-not $captured.Exception.Message.Contains($script:ExceptionCanary)) 'Network exception leaked into error text.'
    return [pscustomobject]@{Failure=$captured; Evidence=$safeText; Seconds=$clock.Elapsed.TotalSeconds}
}

# Baseline reproduction uses the already-delivered v1 helper. It is not changed
# or rewritten by this test, and thus remains an independent pre-fix specimen.
$oldHelper = Join-Path $repo 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalHis-AutoUpdate-20261106-v1\WebUpdate.Iis.ps1'
. $oldHelper
Install-OfflineHttpMock
Invoke-OfflineTest 'delivered v1 rejects a single stale response before a delayed maintenance page can become ready' {
    Set-OfflineScenario 'Delayed'
    $failed = $false
    try { $null = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker } catch { $failed=$true }
    Assert-OfflineTest $failed 'The delivered baseline did not reproduce the single-probe failure.'
    Assert-OfflineTest ($script:HomeRequests -eq 1 -and $script:SessionRequests -eq 0) 'Baseline did not fail at its first home-page probe.'
}

. (Join-Path $repo 'scripts\WebUpdate.Iis.ps1')
Install-OfflineHttpMock

Invoke-OfflineTest 'delayed 200 response is retried until both routes return stable marker plus 503' {
    Set-OfflineScenario 'Delayed'
    $result = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -RequiredConsecutivePasses 2
    Assert-OfflineTest $result.Offline 'Offline result was not verified.'
    Assert-OfflineTest ($script:HomeRequests -ge 3 -and $script:SessionRequests -ge 2) 'Both routes did not reach consecutive successful checks.'
    $null = Assert-OfflineEvidenceSafe $result.Evidence
}

Invoke-OfflineTest 'generic 503 without this operation marker is never accepted' {
    $failure = Invoke-OfflineExpectedRejection 'Generic503'
    Assert-OfflineTest ($failure.Evidence.Contains('503')) 'Safe evidence omitted the observed HTTP status.'
}

Invoke-OfflineTest 'HTTP 200 with a copied operation marker is never accepted' {
    $failure = Invoke-OfflineExpectedRejection 'TokenWith200'
    Assert-OfflineTest ($failure.Evidence.Contains('200')) 'Safe evidence omitted the observed HTTP status.'
}

Invoke-OfflineTest 'another maintenance operation marker is never accepted' {
    $null = Invoke-OfflineExpectedRejection 'WrongMarker'
}

Invoke-OfflineTest 'home offline with session endpoint still serving the application is never accepted' {
    $failure = Invoke-OfflineExpectedRejection 'SessionStale'
    Assert-OfflineTest ($script:SessionRequests -gt 1) 'Session endpoint was not checked on repeated attempts.'
    Assert-OfflineTest ($failure.Evidence.Contains('200')) 'Session response status was not recorded safely.'
}

Invoke-OfflineTest 'transient network error is retried without exposing the original exception' {
    Set-OfflineScenario 'TransientNetwork'
    $result = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -RequiredConsecutivePasses 2
    Assert-OfflineTest $result.Offline 'Transient network error prevented verified recovery.'
    Assert-OfflineTest ($script:HomeRequests -ge 3) 'Network error did not require fresh stable success.'
    $null = Assert-OfflineEvidenceSafe $result.Evidence
}

Invoke-OfflineTest 'persistent network error is bounded and exposes no raw exception or response data' {
    $null = Invoke-OfflineExpectedRejection 'PersistentNetwork'
    Assert-OfflineTest (@($script:HttpTimeouts | Where-Object { $_ -lt 1 -or $_ -gt 1000 }).Count -eq 0) 'An individual HTTP request exceeded the total one-second gate budget.'
}

Invoke-OfflineTest 'a failed route between successful rounds resets consecutive success' {
    Set-OfflineScenario 'ResetConsecutive'
    $result = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -RequiredConsecutivePasses 2
    Assert-OfflineTest $result.Offline 'Expected the gate to recover after interrupted readiness.'
    Assert-OfflineTest ($script:HomeRequests -ge 4 -and $script:SessionRequests -ge 4) 'A failed round did not reset the consecutive-success requirement.'
}

Invoke-OfflineTest 'subapplication probes stay on both selected routes and never include the operation marker in query text' {
    Set-OfflineScenario 'AlwaysReady'
    $result = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -RequiredConsecutivePasses 2
    Assert-OfflineTest $result.Offline 'Expected verified readiness.'
    foreach ($url in $script:OfflineUrls) {
        $uri = [Uri]$url
        Assert-OfflineTest ($uri.AbsolutePath -in @('/reports/','/reports/api/session')) 'Probe escaped the selected application routes.'
        Assert-OfflineTest (-not $uri.Query.Contains($script:OfflineMarker)) 'Operation marker must not be reflected from query text.'
    }
}

Invoke-OfflineTest 'lost maintenance ownership before the first probe aborts without HTTP or raw exception disclosure' {
    Set-OfflineScenario 'AlwaysReady'
    $captured=$null
    try {
        $null = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -AssertMaintenance { throw $script:ExceptionCanary }
    } catch { $captured=$_ }
    Assert-OfflineTest ($null -ne $captured) 'Lost maintenance ownership was accepted.'
    Assert-OfflineTest ($script:OfflineUrls.Count -eq 0) 'Probe continued after initial ownership loss.'
    $safeText=Assert-OfflineEvidenceSafe $captured.Exception.Data['ReportDeskOfflineEvidence']
    Assert-OfflineTest ($safeText.Contains('MaintenanceOwnershipFailed')) 'Ownership failure phase was not reported.'
    Assert-OfflineTest (-not $captured.Exception.Message.Contains($script:ExceptionCanary)) 'Ownership callback exception leaked into error text.'
}

Invoke-OfflineTest 'ownership lost after the final successful response still prevents gate success' {
    Set-OfflineScenario 'AlwaysReady'
    $script:OwnershipChecks=0
    $captured=$null
    try {
        $null = Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/reports/' -Marker $script:OfflineMarker -TimeoutSeconds 2 -PollMilliseconds 10 -AssertMaintenance {
            $script:OwnershipChecks++
            if ($script:OwnershipChecks -eq 4) { throw $script:ExceptionCanary }
        }
    } catch { $captured=$_ }
    Assert-OfflineTest ($null -ne $captured) 'Gate accepted readiness after losing maintenance ownership.'
    Assert-OfflineTest ($script:OwnershipChecks -eq 4 -and $script:HomeRequests -eq 2 -and $script:SessionRequests -eq 2) 'Ownership was not checked before and after both rounds.'
    $safeText=Assert-OfflineEvidenceSafe $captured.Exception.Data['ReportDeskOfflineEvidence']
    Assert-OfflineTest ($safeText.Contains('MaintenanceOwnershipFailed')) 'Final ownership failure was not reported.'
}

Invoke-OfflineTest 'unsafe marker and invalid budgets are rejected before HTTP' {
    Set-OfflineScenario 'AlwaysReady'
    foreach ($invalid in @('short','../outside','has whitespace marker here')) {
        $failed=$false
        try { $null=Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/' -Marker $invalid -TimeoutSeconds 1 -PollMilliseconds 10 } catch {$failed=$true}
        Assert-OfflineTest $failed 'Invalid marker was accepted.'
    }
    foreach ($invalidTimeout in @(0,121)) {
        $failed=$false
        try { $null=Test-ReportDeskOffline -BaseUrl 'http://127.0.0.1:38443/' -Marker $script:OfflineMarker -TimeoutSeconds $invalidTimeout -PollMilliseconds 10 } catch {$failed=$true}
        Assert-OfflineTest $failed 'Invalid timeout was accepted.'
    }
    Assert-OfflineTest ($script:OfflineUrls.Count -eq 0) 'Input rejection performed an HTTP request.'
}

$summary = [pscustomobject][ordered]@{
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    OldHelperSHA256=(Get-FileHash -LiteralPath $oldHelper -Algorithm SHA256).Hash
    NewHelperSHA256=(Get-FileHash -LiteralPath (Join-Path $repo 'scripts\WebUpdate.Iis.ps1') -Algorithm SHA256).Hash
    Passed=@($script:OfflineResults | Where-Object {$_.Status -eq 'PASS'}).Count
    Failed=@($script:OfflineResults | Where-Object {$_.Status -eq 'FAIL'}).Count
    Tests=$script:OfflineResults.ToArray()
    RealIisAcceptance='NOT RUN - response boundary mocked; no real website or database accessed'
    Scope='Confirms retry and strict proof semantics. It does not establish why the hospital server failed its v1 probe.'
}
$json = $summary | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'results.json'), $json, (New-Object Text.UTF8Encoding($false)))
Write-Host ('Results: ' + (Join-Path $OutputDirectory 'results.json'))
if ($summary.Failed -gt 0) { throw ('Offline checks failed: ' + $summary.Failed) }
Write-Host ('Offline checks passed: ' + $summary.Passed)
