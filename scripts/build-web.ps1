param([string]$OutputDirectory, [ValidateSet('net48','net462')][string]$TargetFramework = 'net48')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ('artifacts\web\ReportDesk-Web-' + $TargetFramework + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Output must be a new directory; existing files are never overwritten.' }
$buildOutput = Join-Path $projectRoot ('artifacts\web-build\' + $TargetFramework + '-' + [Guid]::NewGuid().ToString('N'))
Push-Location $projectRoot
try {
    # Force re-evaluation when switching frameworks; obj restore caches are shared.
    & dotnet restore 'src\ReportDesk.Web\ReportDesk.Web.csproj' -p:ReportDeskWebTargetFramework=$TargetFramework --force --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Web dependency restore failed.' }
    & dotnet build 'src\ReportDesk.Web\ReportDesk.Web.csproj' -c Release -p:ReportDeskWebTargetFramework=$TargetFramework -o $buildOutput --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' }
    $bin = New-Item -ItemType Directory -Path (Join-Path $destination 'bin') -Force
    $ui = New-Item -ItemType Directory -Path (Join-Path $destination 'UI') -Force
    Get-ChildItem -LiteralPath $buildOutput -File | Where-Object { $_.Extension -in '.dll', '.config' } | Copy-Item -Destination $bin.FullName
    Copy-Item -LiteralPath 'src\ReportDesk.Web\Web.config','src\ReportDesk.Web\Global.asax' -Destination $destination
    # ASP.NET reads redirects from Web.config, not the library's .dll.config.
    $webConfigPath = Join-Path $destination 'Web.config'
    [xml]$webConfiguration = Get-Content -LiteralPath $webConfigPath -Raw
    $frameworkVersion = if ($TargetFramework -eq 'net462') { '4.6.2' } else { '4.8' }
    $webConfiguration.configuration.'system.web'.compilation.SetAttribute('targetFramework', $frameworkVersion)
    $webConfiguration.configuration.'system.web'.httpRuntime.SetAttribute('targetFramework', $frameworkVersion)
    [xml]$libraryConfiguration = Get-Content -LiteralPath (Join-Path $buildOutput 'ReportDesk.Web.dll.config') -Raw
    $runtimeNode = $libraryConfiguration.SelectSingleNode('/configuration/runtime')
    if ($null -eq $runtimeNode) { throw 'Generated assembly binding redirects are missing.' }
    $existingRuntime = $webConfiguration.SelectSingleNode('/configuration/runtime')
    if ($null -ne $existingRuntime) { [void]$webConfiguration.DocumentElement.RemoveChild($existingRuntime) }
    [void]$webConfiguration.DocumentElement.AppendChild($webConfiguration.ImportNode($runtimeNode, $true))
    $webConfiguration.Save($webConfigPath)
    foreach ($name in @('styles.css','execution.css','sql-editor.css','query-form.js','renderer.js','sql-editor.js','close-emblem.svg')) {
        Copy-Item -LiteralPath (Join-Path 'src\ReportDesk.Desktop\ui' $name) -Destination $ui.FullName
    }
    Get-ChildItem -LiteralPath 'src\ReportDesk.Web\UI' -File | Copy-Item -Destination $ui.FullName
    Copy-Item -LiteralPath 'docs\Web-QuickStart.md','docs\Verification-Web-20260918.md','docs\Verification-Web-IntranetHttp-20260918.md' -Destination $destination
    if ($TargetFramework -eq 'net462') {
        Copy-Item -LiteralPath 'docs\Web-net462-Compatibility.md' -Destination $destination
    }
    $license = Join-Path $env:USERPROFILE '.nuget\packages\oracle.manageddataaccess\19.32.0\LICENSE.txt'
    if (Test-Path -LiteralPath $license) { Copy-Item -LiteralPath $license -Destination (Join-Path $destination 'Oracle-LICENSE.txt') }
    $manifest = @(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object {
        @{ path = $_.FullName.Substring($destination.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
    Write-Output ('Web package: ' + $destination)
    Write-Output 'Default: offline, HTTP allowed, empty maintenance list allows all functions, HIS synchronization disabled. No IIS configuration was changed.'
} finally { Pop-Location }
