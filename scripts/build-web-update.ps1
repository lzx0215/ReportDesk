#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SourcePackageDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SourcePackageDirectory) {
    $SourcePackageDirectory = Join-Path $projectRoot 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalHis-20261106'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\packages\ReportDesk-Web-net462-LegacyCore-LocalHis-AutoUpdate-20261106-v1'
}

# Dot-sourcing loads validation functions only; it does not run the updater.
. (Join-Path $PSScriptRoot 'Apply-WebUpdate.ps1')
$source = Resolve-ReportDeskSafePath ([IO.Path]::GetFullPath($SourcePackageDirectory))
$destination = Resolve-ReportDeskSafePath ([IO.Path]::GetFullPath($OutputDirectory))
$zipPath = Resolve-ReportDeskSafePath ($destination + '.zip')
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zipPath)) {
    throw 'Output directory or ZIP already exists. Choose a new output name; existing packages are never overwritten.'
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'The source update package does not exist.' }
Assert-ReportDeskSeparate $source $destination
Assert-ReportDeskPackage $source
if ($script:WebUpdateRelease -ne 'LocalHis-20261106' -or $script:WebUpdateFiles.Count -ne 15) {
    throw 'This packaging script requires the pinned LocalHis-20261106 release with exactly 15 payload files.'
}

$inputs = [ordered]@{}
foreach ($relative in $script:WebUpdateFiles.Keys) {
    $inputs['payload/' + $relative] = Join-Path (Join-Path $source 'payload') $relative
}
foreach ($name in @('Apply-WebUpdate.ps1', 'WebUpdate.Iis.ps1', 'Update-Web.cmd', 'Check-WebUpdate.cmd', 'Rollback-WebUpdate.cmd')) {
    $inputs[$name] = Join-Path $PSScriptRoot $name
}
$inputs['README.md'] = Join-Path $projectRoot 'docs\Web-OneClick-Update.md'
foreach ($name in @('Web-Local-His-Directory.md', 'Verification-Web-LocalDirectory-20261106.md')) {
    $inputs['docs/' + $name] = Join-Path (Join-Path $source 'docs') $name
}

# Verify every prerequisite before creating the output directory.
$inputHashes = @{}
foreach ($relative in $inputs.Keys) {
    $safeInput = Resolve-ReportDeskSafePath $inputs[$relative]
    if (-not (Test-Path -LiteralPath $safeInput -PathType Leaf)) {
        throw ('Required package input is missing: ' + $relative)
    }
    $inputHashes[$relative] = Get-ReportDeskHash $safeInput
}

# Failure leaves this newly created package for inspection; no cleanup can touch a delivered package.
$null = New-Item -ItemType Directory -Path $destination -ErrorAction Stop
foreach ($relative in $inputs.Keys) {
    $target = Resolve-ReportDeskSafePath (Join-Path $destination $relative)
    if (-not (Test-ReportDeskInside $target $destination)) { throw 'Package output escaped its destination.' }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($inputs[$relative], $target, $false)
    if ((Get-ReportDeskHash $target) -ne $inputHashes[$relative] -or
        (Get-ReportDeskHash $inputs[$relative]) -ne $inputHashes[$relative]) {
        throw ('Package input changed while copying: ' + $relative)
    }
}
Assert-ReportDeskPackage $destination

$manifest = @(
    foreach ($relative in @($inputs.Keys | Sort-Object)) {
        [pscustomobject][ordered]@{ path = $relative; sha256 = $inputHashes[$relative] }
    }
)
# The manifest covers every deliverable except itself; the ZIP hash also covers the manifest.
$manifestPath = Join-Path $destination 'manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
$expectedZipFiles = @{}
foreach ($entry in $manifest) { $expectedZipFiles[$entry.path] = $entry.sha256 }
$expectedZipFiles['manifest.json'] = Get-ReportDeskHash $manifestPath
$actualFiles = @(Get-ReportDeskTreeFiles $destination)
if ($actualFiles.Count -ne $expectedZipFiles.Count) { throw 'Unexpected files were found in the new package directory.' }
foreach ($file in $actualFiles) {
    $relative = $file.Substring($destination.Length + 1).Replace('\', '/')
    if (-not $expectedZipFiles.ContainsKey($relative) -or (Get-ReportDeskHash $file) -ne $expectedZipFiles[$relative]) {
        throw ('Package directory verification failed: ' + $relative)
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($destination, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($archive.Entries.Count -ne $expectedZipFiles.Count) { throw 'ZIP entry count does not match the package manifest.' }
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($seen.ContainsKey($name) -or -not $expectedZipFiles.ContainsKey($name)) { throw 'ZIP contains an unexpected or duplicate entry.' }
        $seen[$name] = $true
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($hash -ne $expectedZipFiles[$name]) { throw ('ZIP SHA256 verification failed: ' + $name) }
    }
} finally { $archive.Dispose() }

[pscustomobject][ordered]@{
    PackageDirectory = $destination
    ZipPath = $zipPath
    ZipSha256 = Get-ReportDeskHash $zipPath
    PayloadFiles = $script:WebUpdateFiles.Count
    ManifestFiles = $manifest.Count
    ZipFiles = $expectedZipFiles.Count
    CoreBaselineSha256 = $script:WebUpdateCoreHash
    Verification = 'Payload, copied inputs, directory manifest, and every ZIP entry verified. No DLL build or IIS action was performed.'
}
