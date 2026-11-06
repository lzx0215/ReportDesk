#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)

# Real HttpWebRequest/response-stream checks against an ephemeral loopback TCP
# HTTP fixture. This does not use IIS, URL ACLs, credentials or any database.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory=Join-Path $repo ('artifacts\verification\web-update-http-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$allowedOutput=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts\verification')) + '\'
if (-not $OutputDirectory.StartsWith($allowedOutput,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Output must be a new directory under artifacts\verification.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
. (Join-Path $repo 'scripts\WebUpdate.Iis.ps1')
$results=New-Object 'System.Collections.Generic.List[object]'
function Assert-HttpFixture([bool]$Value,[string]$Message) { if (-not $Value) { throw ('ASSERT: ' + $Message) } }
function Invoke-HttpFixtureTest([string]$Name,[scriptblock]$Action) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $results.Add([pscustomobject]@{Name=$Name;Status='PASS';Seconds=[Math]::Round($clock.Elapsed.TotalSeconds,3)})
        Write-Host ('PASS: ' + $Name)
    } catch {
        $results.Add([pscustomobject]@{Name=$Name;Status='FAIL';Seconds=[Math]::Round($clock.Elapsed.TotalSeconds,3);Error=$_.Exception.Message})
        Write-Host ('FAIL: ' + $Name + ' | ' + $_.Exception.Message)
    }
}

$marker='ReportDeskUpdate-112233445566778899aabbccddeeff00'
$shared=[hashtable]::Synchronized(@{DelayedHome=0;DelayedSession=0;RedirectTarget=0;Stop=$false;Started=$false})
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$server=$null
$async=$null
$listenerStopped=$false
$tlsBefore=[Net.ServicePointManager]::SecurityProtocol
$serverCode={
    param($Listener,$Shared,[string]$Marker)
    $ErrorActionPreference='Stop'
    $Shared.Started=$true
    while (-not $Shared.Stop) {
        $client=$null
        try {
            $client=$Listener.AcceptTcpClient()
            $stream=$client.GetStream()
            $stream.ReadTimeout=1500
            $stream.WriteTimeout=1500
            $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::ASCII,$false,1024,$true)
            $requestLine=$reader.ReadLine()
            if (-not $requestLine) { continue }
            $requestPath=($requestLine -split ' ')[1].Split('?')[0]
            do { $header=$reader.ReadLine() } while ($null -ne $header -and $header.Length -gt 0)
            $reader.Dispose()
            $code=200
            $reason='OK'
            $body='plain fixture response'
            $extraHeaders=''
            $delayHeader=0
            $delayBody=0
            $omitLength=$false
            switch ($requestPath) {
                '/ready/' { $code=503; $reason='Service Unavailable'; $body='<html><!-- ' + $Marker + ' --></html>' }
                '/plain/' { }
                '/redirect/' { $code=302; $reason='Found'; $extraHeaders="Location: /target/`r`n" }
                '/target/' { $Shared.RedirectTarget++; $body='redirect target' }
                '/large/' { $body=('x' * 4096) }
                '/large-unannounced/' { $body=('x' * 4096); $omitLength=$true }
                '/delayed/' {
                    $Shared.DelayedHome++
                    if ($Shared.DelayedHome -gt 1) { $code=503; $reason='Service Unavailable'; $body='<html><!-- ' + $Marker + ' --></html>' }
                }
                '/delayed/api/session' { $Shared.DelayedSession++; $code=503; $reason='Service Unavailable'; $body='<html><!-- ' + $Marker + ' --></html>' }
                '/slow-body/' { $delayBody=550; $body='delayed response body' }
                '/shared-deadline/' { $delayHeader=180; $delayBody=220; $body='body beyond the common deadline' }
                default { $code=404; $reason='Not Found'; $body='unknown fixture path' }
            }
            $bytes=[Text.Encoding]::UTF8.GetBytes($body)
            $lengthHeader=if ($omitLength) { '' } else { 'Content-Length: ' + $bytes.Length + "`r`n" }
            $headers=[Text.Encoding]::ASCII.GetBytes('HTTP/1.1 ' + $code + ' ' + $reason + "`r`nContent-Type: text/html; charset=utf-8`r`nConnection: close`r`n" + $lengthHeader + $extraHeaders + "`r`n")
            if ($delayHeader -gt 0) { Start-Sleep -Milliseconds $delayHeader }
            $stream.Write($headers,0,$headers.Length)
            $stream.Flush()
            if ($delayBody -gt 0) { Start-Sleep -Milliseconds $delayBody }
            $stream.Write($bytes,0,$bytes.Length)
            $stream.Flush()
        } catch {
            # Expected when a bounded client rejects or times out and closes its
            # socket, and when finally stops the listener's pending accept.
            if ($Shared.Stop) { break }
        } finally {
            if ($null -ne $client) { $client.Close() }
        }
    }
}

try {
    $listener.Start()
    $port=([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $baseUrl='http://127.0.0.1:' + $port + '/'
    $server=[PowerShell]::Create()
    $null=$server.AddScript($serverCode.ToString()).AddArgument($listener).AddArgument($shared).AddArgument($marker)
    $async=$server.BeginInvoke()
    $startup=[Diagnostics.Stopwatch]::StartNew()
    while (-not $shared.Started -and $startup.ElapsedMilliseconds -lt 3000) { Start-Sleep -Milliseconds 10 }
    Assert-HttpFixture $shared.Started 'Loopback fixture runspace did not start.'

    Invoke-HttpFixtureTest 'real HTTP 503 retains body and operation marker' {
        $r=Invoke-ReportDeskHttp -Url ($baseUrl+'ready/') -TimeoutMilliseconds 1500
        Assert-HttpFixture ($r.StatusCode -eq 503 -and $r.Body.Contains($marker)) 'HTTP 503 body was not read through the WebException response.'
        Assert-HttpFixture ($r.Bytes.Length -eq [Text.Encoding]::UTF8.GetByteCount($r.Body)) 'Response bytes differed from the received fixture body.'
    }
    Invoke-HttpFixtureTest 'real HTTP 200 is read normally' {
        $r=Invoke-ReportDeskHttp -Url ($baseUrl+'plain/') -TimeoutMilliseconds 1500
        Assert-HttpFixture ($r.StatusCode -eq 200 -and $r.Body -eq 'plain fixture response') 'HTTP 200 response mismatch.'
    }
    Invoke-HttpFixtureTest 'real redirect is returned without following its location' {
        $r=Invoke-ReportDeskHttp -Url ($baseUrl+'redirect/') -TimeoutMilliseconds 1500
        Assert-HttpFixture ($r.StatusCode -eq 302 -and $shared.RedirectTarget -eq 0) 'Health request followed a redirect.'
    }
    Invoke-HttpFixtureTest 'declared response length exceeding the cap is rejected' {
        $failure=$null
        try { $null=Invoke-ReportDeskHttp -Url ($baseUrl+'large/') -MaximumBytes 128 -TimeoutMilliseconds 1500 } catch {$failure=$_}
        Assert-HttpFixture ($null -ne $failure -and $failure.Exception.Message -match 'size limit') 'Declared oversized response was accepted.'
    }
    Invoke-HttpFixtureTest 'response with no declared length is still capped while reading' {
        $failure=$null
        try { $null=Invoke-ReportDeskHttp -Url ($baseUrl+'large-unannounced/') -MaximumBytes 128 -TimeoutMilliseconds 1500 } catch {$failure=$_}
        Assert-HttpFixture ($null -ne $failure -and $failure.Exception.Message -match 'size limit') 'Unannounced oversized response was accepted.'
    }
    Invoke-HttpFixtureTest 'real offline gate retries old 200 and proves both routes in consecutive rounds' {
        $r=Test-ReportDeskOffline -BaseUrl ($baseUrl+'delayed/') -Marker $marker -TimeoutSeconds 3 -PollMilliseconds 10
        Assert-HttpFixture ($r.Offline -and $shared.DelayedHome -ge 3 -and $shared.DelayedSession -ge 3) 'Real responses did not prove both routes after the stale page.'
        Assert-HttpFixture ($r.Evidence.ConsecutivePasses -ge 2) 'Real gate accepted fewer than two successful rounds.'
    }
    Invoke-HttpFixtureTest 'slow body is rejected within the read deadline' {
        $failure=$null
        $clock=[Diagnostics.Stopwatch]::StartNew()
        try { $null=Invoke-ReportDeskHttp -Url ($baseUrl+'slow-body/') -TimeoutMilliseconds 250 } catch {$failure=$_}
        Assert-HttpFixture ($null -ne $failure) 'Slow body exceeded the deadline but was accepted.'
        Assert-HttpFixture ($clock.ElapsedMilliseconds -lt 1500) 'Slow body read was not bounded.'
    }
    # Let the single fixture worker finish its intentionally delayed write before
    # the next independent request; this is not part of the HTTP timeout test.
    Start-Sleep -Milliseconds 600
    Invoke-HttpFixtureTest 'headers and body consume one common timeout budget' {
        $failure=$null
        $clock=[Diagnostics.Stopwatch]::StartNew()
        try { $null=Invoke-ReportDeskHttp -Url ($baseUrl+'shared-deadline/') -TimeoutMilliseconds 300 } catch {$failure=$_}
        Assert-HttpFixture ($null -ne $failure) 'Header and body waits each consumed a fresh deadline.'
        Assert-HttpFixture ($clock.ElapsedMilliseconds -lt 1500) 'Combined header and body deadline was not bounded.'
    }
    Invoke-HttpFixtureTest 'TLS protocol setting is restored after all success and failure paths' {
        Assert-HttpFixture ([Net.ServicePointManager]::SecurityProtocol -eq $tlsBefore) 'HTTP helper left changed TLS protocol settings.'
    }
} finally {
    $shared.Stop=$true
    $listener.Stop()
    $listenerStopped=$true
    if ($null -ne $async) {
        if (-not $async.AsyncWaitHandle.WaitOne(2000)) { $server.Stop() }
        try { $null=$server.EndInvoke($async) } catch { }
    }
    if ($null -ne $server) { $server.Dispose() }
}
$summary=[pscustomobject][ordered]@{
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    HelperSHA256=(Get-FileHash -LiteralPath (Join-Path $repo 'scripts\WebUpdate.Iis.ps1') -Algorithm SHA256).Hash
    Fixture='Real TCP/HTTP bound only to loopback on an OS-selected ephemeral port; no mocks'
    ListenerStopped=$listenerStopped
    Passed=@($results | Where-Object {$_.Status -eq 'PASS'}).Count
    Failed=@($results | Where-Object {$_.Status -eq 'FAIL'}).Count
    Tests=$results.ToArray()
    RealIisAcceptance='NOT RUN - no IIS, hospital server, TLS certificate, or database accessed'
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'results.json'),($summary | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
Write-Host ('Results: ' + (Join-Path $OutputDirectory 'results.json'))
if ($summary.Failed -gt 0) { throw ('HTTP checks failed: ' + $summary.Failed) }
Write-Host ('HTTP checks passed: ' + $summary.Passed)
