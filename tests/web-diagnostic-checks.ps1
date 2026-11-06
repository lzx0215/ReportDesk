#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'scripts\Diagnose-WebUpdate.ps1')
$runRoot = Join-Path $repo ('artifacts\verification\web-diagnostic-' + [Guid]::NewGuid().ToString('N'))
$siteFixture = Join-Path $runRoot 'site'
$candidateRoot = Join-Path $runRoot 'candidates'
$externalRoot = Join-Path $runRoot 'outside-search'
$effectiveRoot = Join-Path $runRoot 'effective-data'
foreach ($dir in @($siteFixture, $candidateRoot, $externalRoot, $effectiveRoot)) { $null = [IO.Directory]::CreateDirectory($dir) }
$canary = 'DO_NOT_OUTPUT_TEST_CONNECTION_SECRET_7319'
$missingData = Join-Path $runRoot 'missing-data'
$xml = '<configuration><appSettings><add key="ReportDesk.DataDirectory" value="' + $missingData + '"/><add key="ReportDesk.ReportsDirectory" value="' + $candidateRoot + '"/><add key="DatabasePassword" value="' + $canary + '"/></appSettings></configuration>'
[IO.File]::WriteAllText((Join-Path $siteFixture 'Web.config'), $xml)
[IO.File]::WriteAllText((Join-Path $candidateRoot 'connection.json'), $canary)
[IO.File]::WriteAllText((Join-Path $candidateRoot 'catalog.json'), $canary)
[IO.File]::SetAttributes((Join-Path $candidateRoot 'connection.json'), [IO.FileAttributes]::Hidden)
[IO.File]::WriteAllText((Join-Path $externalRoot 'connection.json'), $canary)
$junction = Join-Path $candidateRoot 'linked'
$null = New-Item -ItemType Junction -Path $junction -Target $externalRoot
$before = (Get-FileHash -LiteralPath (Join-Path $siteFixture 'Web.config')).Hash
function Assert-Diagnostic([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Get-ReportDeskDiagnosticIis {
    param([string]$SitePath)
    [pscustomobject]@{ Status='Matched'; Selected=[pscustomobject]@{EffectiveSettings=@([pscustomobject]@{Key='ReportDesk.DataDirectory';Status='Read';Value=$effectiveRoot})} }
}
$run = Invoke-ReportDeskWebDiagnostic -SitePath $siteFixture -SearchRoots @($candidateRoot,$missingData) -OutputDirectory (Join-Path $runRoot 'output')
$checks = New-Object 'System.Collections.Generic.List[string]'
Assert-Diagnostic ($run.Report.LocalWebConfig.Settings[0].Status -eq 'NotFound') 'Missing configured data directory must be reported without being created.'
Assert-Diagnostic (-not (Test-Path -LiteralPath $missingData)) 'Missing data directory was created.'
$checks.Add('Missing DataDirectory is reported and remains absent')
Assert-Diagnostic ($run.Report.ConnectionFileSearch.Files.Count -eq 2) 'Expected visible catalog and hidden connection candidate only.'
Assert-Diagnostic (@($run.Report.ConnectionFileSearch.Files | Where-Object {$_.Hidden}).Count -eq 1) 'Hidden candidate missing.'
$checks.Add('Hidden and visible candidates are found without using Include')
Assert-Diagnostic (@($run.Report.ConnectionFileSearch.Issues | Where-Object {$_.Reason -eq 'ReparsePointSkipped'}).Count -eq 1) 'Junction was not explicitly skipped.'
Assert-Diagnostic (@($run.Report.ConnectionFileSearch.Files | Where-Object {$_.Path.StartsWith($externalRoot)}).Count -eq 0) 'Search followed the junction.'
$checks.Add('Reparse point is reported and not traversed')
Assert-Diagnostic (-not (Get-Content -LiteralPath $run.JsonPath -Raw).Contains($canary)) 'Secret canary leaked into diagnostic output.'
$checks.Add('Report excludes unrelated Web.config and connection-file secret content')
Assert-Diagnostic ((Get-FileHash -LiteralPath (Join-Path $siteFixture 'Web.config')).Hash -eq $before) 'Site configuration changed.'
Assert-Diagnostic ([IO.File]::ReadAllText((Join-Path $candidateRoot 'connection.json')) -eq $canary) 'Connection file changed.'
$checks.Add('Original website and connection file bytes remain unchanged')
$bounded = Find-RdDiagnosticConnectionFiles -Roots @($candidateRoot,$effectiveRoot) -MaximumDirectories 1
Assert-Diagnostic $bounded.Truncated 'Bounded search must report truncation.'
$checks.Add('Directory budget explicitly reports truncation')
$caught = $false
try { $null = Invoke-ReportDeskWebDiagnostic -SitePath $siteFixture -SearchRoots @($candidateRoot) -OutputDirectory (Join-Path $siteFixture 'forbidden-output') } catch { $caught = $true }
Assert-Diagnostic $caught 'Output inside the website was not rejected.'
Assert-Diagnostic (-not (Test-Path -LiteralPath (Join-Path $siteFixture 'forbidden-output'))) 'Output directory was created inside website.'
$checks.Add('Report output inside the website is rejected before writing')
Assert-Diagnostic ($run.Report.IIS.Selected.EffectiveSettings[0].Value -eq $effectiveRoot) 'Effective settings were not preserved as distinct from local configuration.'
$checks.Add('Current IIS effective settings remain distinct from local Web.config')
foreach ($file in @('Diagnose-WebUpdate.ps1','WebDiagnostic.Iis.ps1','WebUpdate.Iis.ps1')) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo ('scripts\'+$file)),[ref]$tokens,[ref]$errors)
    Assert-Diagnostic ($errors.Count -eq 0) ('Parse failure: '+$file)
    foreach ($command in $ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst]},$true)) {
        Assert-Diagnostic ($command.GetCommandName() -notmatch '^(iisreset|appcmd|Stop-Service|Restart-Service|Restart-WebAppPool|Stop-WebAppPool|Start-WebAppPool|Stop-Website|Start-Website)$') 'IIS/service mutation command found.'
    }
    foreach ($method in $ast.FindAll({param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst]},$true)) {
        Assert-Diagnostic ($method.Member.Value -ne 'CommitChanges') 'IIS CommitChanges call found.'
    }
}
$checks.Add('PS5.1 parsing and no IIS/service mutation checks pass')
$summary=[pscustomobject]@{Passed=$checks.Count;Checks=$checks.ToArray();PowerShellVersion=$PSVersionTable.PSVersion.ToString();RealIis='NOT RUN - IIS boundary mocked; filesystem fixture is real';Result=$run.JsonPath}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'checks.json') -Encoding UTF8
$summary | Format-List
