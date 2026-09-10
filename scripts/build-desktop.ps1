param([switch]$SkipPackage)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $projectRoot
try {
    & dotnet build 'src\ReportDesk.Host\ReportDesk.Host.csproj' -c Release -o 'artifacts\host' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Desktop host build failed' }
    & node 'tests\desktop\host-checks.cjs'
    if ($LASTEXITCODE -ne 0) { throw 'Desktop host checks failed' }
    Push-Location 'src\ReportDesk.Desktop'
    try {
        $env:electron_config_cache = Join-Path $projectRoot 'artifacts\runtime\cache'
        $env:ELECTRON_GET_USE_PROXY = 'true'
        & npm ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Desktop dependency restore failed' }
        & node (Join-Path $projectRoot 'tests\desktop\ui-checks.cjs')
        if ($LASTEXITCODE -ne 0) { throw 'Desktop UI checks failed' }
        if (-not $SkipPackage) {
            & npm run package
            if ($LASTEXITCODE -ne 0) { throw 'Desktop package failed' }
        }
    } finally { Pop-Location }
    if (-not $SkipPackage) {
        $destination = Join-Path $projectRoot 'artifacts\desktop\ReportDesk-win32-x64'
        Copy-Item -LiteralPath 'docs\ReportVisibility.md','docs\Acceptance.md','docs\Desktop.md','config\report-visibility.example.xml' -Destination $destination
        $oracleLicense = Join-Path $env:USERPROFILE '.nuget\packages\oracle.manageddataaccess\19.32.0\LICENSE.txt'
        if (Test-Path -LiteralPath $oracleLicense) { Copy-Item -LiteralPath $oracleLicense -Destination (Join-Path $destination 'Oracle-LICENSE.txt') }
        & node 'tests\desktop\package-checks.cjs'
        if ($LASTEXITCODE -ne 0) { throw 'Packaged EXE smoke check failed' }
        Write-Output ('Desktop output: ' + $destination)
    }
} finally { Pop-Location }
