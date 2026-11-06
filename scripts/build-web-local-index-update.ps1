#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$BaselineBin,
    [string]$OutputDirectory,
    [string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = 'LocalIndex-20261106-v1'
$expectedCore = '6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7'
if (-not $BaselineBin) { $BaselineBin = Join-Path $repo 'artifacts\verification\local-directory-20261106-legacy-final' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalIndex-AutoUpdate-20261106-v1' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $repo 'artifacts\verification\local-index-package-20261106-v1-bin' }
# Import safe-path and payload verification helpers, never the install entry point.
. (Join-Path $PSScriptRoot 'Apply-WebUpdate.ps1')
$destination = Resolve-ReportDeskSafePath ([IO.Path]::GetFullPath($OutputDirectory))
$build = Resolve-ReportDeskSafePath ([IO.Path]::GetFullPath($BuildDirectory))
$baseline = Resolve-ReportDeskSafePath ([IO.Path]::GetFullPath($BaselineBin))
$zipPath = $destination + '.zip'
foreach ($path in @($destination, $zipPath, ($zipPath + '.sha256'), $build)) {
    if (Test-Path -LiteralPath $path) { throw 'Use new output names; existing builds and packages are never overwritten.' }
}
Assert-ReportDeskSeparate $destination $build
Assert-ReportDeskSeparate $baseline $build
Assert-ReportDeskSeparate $baseline $destination
if ((Get-ReportDeskHash (Join-Path $baseline 'ReportDesk.Core.dll')) -ne $expectedCore) { throw 'Legacy Core baseline mismatch.' }

$uiSources = [ordered]@{}
foreach ($name in @('styles.css','execution.css','sql-editor.css','query-form.js','renderer.js','sql-editor.js','close-emblem.svg')) {
    $uiSources['UI/' + $name] = Join-Path $repo ('src\ReportDesk.Desktop\ui\' + $name)
}
foreach ($name in @('index.html','local-directory.js','README.md','transport.js','web.css','web.js','xml-editor.js')) {
    $uiSources['UI/' + $name] = Join-Path $repo ('src\ReportDesk.Web\UI\' + $name)
}
$updaterNames = @('Apply-WebUpdate.ps1','WebUpdate.Iis.ps1','Update-Web.cmd','Check-WebUpdate.cmd','Rollback-WebUpdate.cmd')
$docNames = @('Verification-Web-Settings-20261106.md','Verification-Web-ReportSources-20261106.md')
function Get-LocalIndexInputs {
    # This list includes all compiled Web/linked Host sources and every shipped UI/tool/doc input.
    Get-ChildItem -LiteralPath (Join-Path $repo 'src\ReportDesk.Web') -File -Recurse |
        Where-Object { $_.Extension -in '.cs','.csproj' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { $_.FullName }
    Get-ChildItem -LiteralPath (Join-Path $repo 'src\ReportDesk.Host') -Filter '*.cs' -File | ForEach-Object { $_.FullName }
    Join-Path $repo 'src\ReportDesk.Core\FullXmlEditor.cs'
    Join-Path $repo 'Directory.Build.props'
    foreach ($file in $uiSources.Values) { $file }
    foreach ($name in $updaterNames) { Join-Path $PSScriptRoot $name }
    foreach ($name in $docNames) { Join-Path $repo ('docs\' + $name) }
    Join-Path $repo 'docs\Web-LocalIndex-Update-20261106.md'
    Join-Path $PSScriptRoot 'build-web-legacy-core.ps1'
    $PSCommandPath
}
$inputs = @{}
foreach ($file in @(Get-LocalIndexInputs | Sort-Object -Unique)) { $inputs[$file] = Get-ReportDeskHash $file; if (-not $inputs[$file]) { throw 'Missing build/package input.' } }
function Assert-LocalIndexInputs {
    $current = @(Get-LocalIndexInputs | Sort-Object -Unique)
    if ($current.Count -ne $inputs.Count) { throw 'Source list changed while building. Stop and choose a new package name.' }
    foreach ($file in $current) { if (-not $inputs.ContainsKey($file) -or (Get-ReportDeskHash $file) -ne $inputs[$file]) { throw ('Source changed while building: ' + $file) } }
}
& (Join-Path $PSScriptRoot 'build-web-legacy-core.ps1') -BaselineBin $baseline -OutputDirectory $build
Assert-LocalIndexInputs
$webDll = Join-Path $build 'ReportDesk.Web.dll'
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($webDll)
$target = @([Reflection.CustomAttributeData]::GetCustomAttributes($assembly) | Where-Object { $_.AttributeType.FullName -eq 'System.Runtime.Versioning.TargetFrameworkAttribute' })
if ($target.Count -ne 1 -or $target[0].ConstructorArguments[0].Value -ne '.NETFramework,Version=v4.6.2') { throw 'The packaged Web DLL is not net462.' }
$null = [IO.Directory]::CreateDirectory((Join-Path $destination 'payload\bin'))
$null = [IO.Directory]::CreateDirectory((Join-Path $destination 'payload\UI'))
$null = [IO.Directory]::CreateDirectory((Join-Path $destination 'docs'))
[IO.File]::Copy($webDll, (Join-Path $destination 'payload\bin\ReportDesk.Web.dll'), $false)
foreach ($relative in $uiSources.Keys) { [IO.File]::Copy($uiSources[$relative], (Join-Path $destination ('payload\' + $relative)), $false) }
$payloadHashes = [ordered]@{}
$payloadHashes['bin/ReportDesk.Web.dll'] = Get-ReportDeskHash $webDll
foreach ($relative in @($uiSources.Keys | Sort-Object)) { $payloadHashes[$relative] = Get-ReportDeskHash (Join-Path $destination ('payload\' + $relative)) }
if ($payloadHashes.Count -ne 15) { throw 'Expected exactly 15 payload files.' }

# Only release identity and exact payload hashes vary; keep the current v2 installer body intact.
$updater = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Apply-WebUpdate.ps1'))
$oldRelease = "`$script:WebUpdateRelease = 'LocalHis-20261106'"
if (-not $updater.Contains($oldRelease)) { throw 'Unexpected installer template release.' }
$updater = $updater.Replace($oldRelease, ("`$script:WebUpdateRelease = '" + $release + "'"))
$pattern = '(?ms)^\$script:WebUpdateFiles = \[ordered\]@\{.*?^\}'
if ([regex]::Matches($updater, $pattern).Count -ne 1) { throw 'Unexpected installer hash table.' }
$hashLines = @('$script:WebUpdateFiles = [ordered]@{')
foreach ($relative in $payloadHashes.Keys) { $hashLines += "    '$relative' = '$($payloadHashes[$relative])'" }
$hashLines += '}'
$table = $hashLines -join "`r`n"
$updater = [regex]::Replace($updater, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $table })
$encoding = New-Object Text.UTF8Encoding($true)
[IO.File]::WriteAllText((Join-Path $destination 'Apply-WebUpdate.ps1'), $updater, $encoding)
foreach ($name in $updaterNames | Where-Object { $_ -ne 'Apply-WebUpdate.ps1' }) { [IO.File]::Copy((Join-Path $PSScriptRoot $name), (Join-Path $destination $name), $false) }
[IO.File]::Copy((Join-Path $repo 'docs\Web-LocalIndex-Update-20261106.md'), (Join-Path $destination 'README.md'), $false)
foreach ($name in $docNames) { [IO.File]::Copy((Join-Path $repo ('docs\' + $name)), (Join-Path $destination ('docs\' + $name)), $false) }
foreach ($name in @('Apply-WebUpdate.ps1','WebUpdate.Iis.ps1')) {
    $tokens = $null; $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $destination $name), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ('Packaged PowerShell syntax failed: ' + $name) }
}
. (Join-Path $destination 'Apply-WebUpdate.ps1')
if ($script:WebUpdateRelease -ne $release -or $script:WebUpdateCoreHash -ne $expectedCore) { throw 'Generated release identity mismatch.' }
Assert-ReportDeskPackage $destination
Assert-LocalIndexInputs
$releaseInfo = [ordered]@{
    release=$release; targetFramework='.NETFramework,Version=v4.6.2'; architecture='x64'
    coreBaselineSha256=$expectedCore; webDllSha256=$payloadHashes['bin/ReportDesk.Web.dll']; payloadFiles=15
    configurationPolicy='Preserve Web.config, Global.asax, dependency DLLs and all DataDirectory/config files byte-for-byte during update and rollback.'
    sourceInputs=@(foreach($file in @($inputs.Keys | Sort-Object)) { [ordered]@{path=$file.Substring($repo.Length+1).Replace('\','/');sha256=$inputs[$file]} })
}
[IO.File]::WriteAllText((Join-Path $destination 'release.json'), ($releaseInfo | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
$manifest = @(foreach ($file in @(Get-ReportDeskTreeFiles $destination | Sort-Object)) { [ordered]@{path=$file.Substring($destination.Length+1).Replace('\','/');sha256=(Get-ReportDeskHash $file)} })
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
$expectedFiles = @{}
foreach ($entry in $manifest) { $expectedFiles[$entry.path] = $entry.sha256 }
$expectedFiles['manifest.json'] = Get-ReportDeskHash (Join-Path $destination 'manifest.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($archive.Entries.Count -ne $expectedFiles.Count) { throw 'Unexpected ZIP file count.' }
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if ($seen.ContainsKey($name) -or -not $expectedFiles.ContainsKey($name)) { throw 'Unexpected/duplicate ZIP entry.' }
        $seen[$name] = $true; $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ne $expectedFiles[$name]) { throw ('ZIP hash mismatch: ' + $name) }
    }
} finally { $archive.Dispose() }
Assert-LocalIndexInputs
$zipHash = Get-ReportDeskHash $zipPath
[IO.File]::WriteAllText(($zipPath + '.sha256'), ($zipHash + '  ' + [IO.Path]::GetFileName($zipPath) + "`r`n"), (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{PackageDirectory=$destination;ZipPath=$zipPath;ZipSha256=$zipHash;BuildDirectory=$build;PayloadFiles=15;ZipFiles=$expectedFiles.Count;TargetFramework=$target[0].ConstructorArguments[0].Value;CoreSha256=$expectedCore}
