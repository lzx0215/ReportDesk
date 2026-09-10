param([string]$ScanDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $projectRoot
try {
    $packageRoot = Join-Path $projectRoot 'artifacts\packages'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    foreach ($architecture in @('x86', 'x64')) {
        $release = Join-Path $projectRoot "artifacts\release\win-$architecture"
        $checks = Join-Path $projectRoot "artifacts\check-bin\$architecture"
        $evidence = Join-Path $projectRoot "artifacts\verification\0.1.4\$architecture"
        New-Item -ItemType Directory -Path $release,$checks,$evidence -Force | Out-Null
        # Rebuild shared intermediate assemblies when switching CPU architectures.
        & dotnet build 'src\ReportDesk.App\ReportDesk.App.csproj' --no-incremental -c Release "-p:PlatformTarget=$architecture" -o $release --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "App build failed: $architecture" }
        & dotnet build 'tests\ReportDesk.Checks\ReportDesk.Checks.csproj' --no-incremental -c Release "-p:PlatformTarget=$architecture" -o $checks --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Checks build failed: $architecture" }
        $checkArgs = @($evidence)
        if ($ScanDirectory) { $checkArgs += $ScanDirectory }
        & (Join-Path $checks 'ReportDesk.Checks.exe') @checkArgs | Tee-Object -FilePath (Join-Path $evidence 'checks.txt')
        if ($LASTEXITCODE -ne 0) { throw "Checks failed: $architecture" }
        $smokePath = Join-Path $evidence 'ui'
        # Only launches this project's program with its isolated test catalog. Never connects a DB.
        $process = Start-Process -FilePath (Join-Path $release 'ReportDesk.exe') -ArgumentList @('--smoke', ('"' + $smokePath + '"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(30000)) { throw "UI check timed out; inspect PID $($process.Id)" }
        $smoke = Join-Path $smokePath 'smoke.txt'
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $smoke) -or -not ((Get-Content -LiteralPath $smoke -Raw).StartsWith('PASS'))) { throw "UI check failed: $architecture, see $smoke" }
        Copy-Item -LiteralPath 'README.md' -Destination $release
        Copy-Item -LiteralPath 'docs\Acceptance.md' -Destination $release
        Copy-Item -LiteralPath 'docs\ThirdParty.md' -Destination $release
        Copy-Item -LiteralPath 'docs\ReportVisibility.md' -Destination $release
        Copy-Item -LiteralPath 'config\report-visibility.example.xml' -Destination $release
        $oracleLicense = Join-Path $env:USERPROFILE '.nuget\packages\oracle.manageddataaccess\19.32.0\LICENSE.txt'
        if (Test-Path -LiteralPath $oracleLicense) { Copy-Item -LiteralPath $oracleLicense -Destination (Join-Path $release 'Oracle-LICENSE.txt') }
        $assets = Get-Content -LiteralPath 'src\ReportDesk.App\obj\project.assets.json' -Raw | ConvertFrom-Json
        $cacheRoots = @($assets.packageFolders.PSObject.Properties.Name)
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package' -or $library.Name -like 'Microsoft.NETFramework.ReferenceAssemblies*') { continue }
            foreach ($cacheRoot in $cacheRoots) {
                $packagePath = Join-Path $cacheRoot $library.Value.path
                if (-not (Test-Path -LiteralPath $packagePath)) { continue }
                $licenseDestination = Join-Path $release ('licenses\' + $library.Name.Replace('/', '-'))
                New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
                Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match 'LICENSE|NOTICE|\.nuspec$' } | Copy-Item -Destination $licenseDestination
            }
        }
        $zip = Join-Path $packageRoot "ReportDesk-0.1.4-win-$architecture.zip"
        Compress-Archive -Path (Join-Path $release '*') -DestinationPath $zip -Force
        Get-FileHash -LiteralPath $zip -Algorithm SHA256 | Format-List
    }
    Get-ChildItem -LiteralPath $packageRoot -Filter '*.zip' | Get-FileHash -Algorithm SHA256 | Select-Object Hash,@{Name='File';Expression={Split-Path -Leaf $_.Path}} | Export-Csv -LiteralPath (Join-Path $packageRoot 'SHA256.csv') -NoTypeInformation -Encoding UTF8
} finally { Pop-Location }
