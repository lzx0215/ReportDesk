param([string]$BaseDirectory = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $BaseDirectory) { $BaseDirectory = Join-Path $projectRoot 'artifacts/desktop/ReportDesk-win32-x64' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/updates/ReportDesk-0.2.0-xml-import-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
Push-Location $projectRoot
try {
    & dotnet build 'src/ReportDesk.Host/ReportDesk.Host.csproj' --no-incremental -c Release '-p:PlatformTarget=x64' -o 'artifacts/host' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
    & node 'scripts/create-update.cjs' $BaseDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Update creation failed.' }
    $files = @('payload','manifest.json','Apply-Update.ps1','README-Update.md') | ForEach-Object { Join-Path $OutputDirectory $_ }
    Compress-Archive -LiteralPath $files -DestinationPath ($OutputDirectory + '.zip')
    Get-FileHash -LiteralPath ($OutputDirectory + '.zip') -Algorithm SHA256 | Format-List
    Write-Output ('Update ZIP: ' + $OutputDirectory + '.zip')
} finally { Pop-Location }
