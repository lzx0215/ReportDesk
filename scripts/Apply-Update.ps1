param(
    [Parameter(Mandatory=$true)][string]$TargetDirectory,
    [switch]$VerifyOnly,
    [switch]$Rollback,
    [string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# A deliberately fixed list: no user data, visibility configuration, dependencies or runtime replacement.
$allowed = @('resources/app.asar','resources/host/ReportDesk.Host.exe','resources/host/ReportDesk.Core.dll')
$targetRoot = (Resolve-Path -LiteralPath $TargetDirectory).Path.TrimEnd('\')
function Inside([string]$baseDirectory, [string]$relative) {
    if ([System.IO.Path]::IsPathRooted($relative) -or (($relative -split '[\\/]') -contains '..')) { throw 'Invalid relative update path.' }
    $full = [System.IO.Path]::GetFullPath((Join-Path $baseDirectory $relative))
    if (-not $full.StartsWith($baseDirectory.TrimEnd('\') + '\',[System.StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escapes the selected directory.' }
    $current = $full
    while ($current.Length -ge $baseDirectory.Length) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw 'Update paths must not contain directory or file links.' }
        if ($current -eq $baseDirectory) { break }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
    return $full
}
function FileHash([string]$file) {
    $stream = [System.IO.File]::OpenRead($file)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function AssertHash([string]$file, [string]$expected) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (FileHash $file) -ne $expected) {
        throw ('File verification failed: ' + $file + '. Use the matching update package; no changes have been applied during preflight.')
    }
}
function AssertClosed {
    foreach ($proc in @(Get-Process -Name ReportDesk,ReportDesk.Host -ErrorAction SilentlyContinue)) {
        $processPath = $proc.Path
        if (-not $processPath -or $processPath.StartsWith($targetRoot + '\',[System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close ReportDesk and wait for its background process to exit before updating. No process will be killed.'
        }
    }
}
if (-not (Test-Path -LiteralPath (Inside $targetRoot 'ReportDesk.exe') -PathType Leaf)) { throw 'Select the Electron ReportDesk directory containing ReportDesk.exe and resources.' }
AssertClosed
if ($Rollback) {
    if (-not $BackupDirectory) { throw 'Rollback requires -BackupDirectory.' }
    $backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path.TrimEnd('\')
    $backupManifest = Get-Content -LiteralPath (Inside $backupRoot 'backup-manifest.json') -Raw | ConvertFrom-Json
    if ($backupManifest.target -ne $targetRoot -or @($backupManifest.files).Count -ne $allowed.Count) { throw 'Backup does not match this installation.' }
    if (@($backupManifest.files.path | Sort-Object -Unique).Count -ne $allowed.Count) { throw 'Duplicate backup file.' }
    foreach ($file in $backupManifest.files) {
        if ($allowed -cnotcontains $file.path) { throw 'Unexpected backup file.' }
        AssertHash (Inside $backupRoot $file.path) $file.sha256
        $null = Inside $targetRoot $file.path
    }
    if ($VerifyOnly) { Write-Output 'PASS rollback verification; no files changed.'; return }
    foreach ($file in $backupManifest.files) { Copy-Item -LiteralPath (Inside $backupRoot $file.path) -Destination (Inside $targetRoot $file.path) -Force }
    foreach ($file in $backupManifest.files) { AssertHash (Inside $targetRoot $file.path) $file.sha256 }
    Write-Output 'PASS rollback. User data and settings were not changed.'
    return
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or @($manifest.files).Count -ne $allowed.Count) { throw 'Unsupported update manifest.' }
if (@($manifest.files.path | Sort-Object -Unique).Count -ne $allowed.Count) { throw 'Duplicate update file.' }
foreach ($required in $manifest.requires) { AssertHash (Inside $targetRoot $required.path) $required.sha256 }
$payloadRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'payload')).Path
$allApplied = $true
foreach ($file in $manifest.files) {
    if ($allowed -cnotcontains $file.path) { throw 'Unexpected update file.' }
    AssertHash (Inside $payloadRoot $file.path) $file.sha256
    $targetFile = Inside $targetRoot $file.path
    if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf)) { throw ('Missing installed file: ' + $file.path) }
    $actual = FileHash $targetFile
    if ($actual -ne $file.sha256) { $allApplied = $false }
}
if ($allApplied) { Write-Output 'PASS: this update is already installed; no files changed.'; return }
# Reject partial/manual updates as well as a different baseline before creating a backup or writing anything.
foreach ($file in $manifest.files) { AssertHash (Inside $targetRoot $file.path) $file.baseSha256 }
if ($VerifyOnly) { Write-Output 'PASS update verification; no files changed.'; return }
foreach ($file in $manifest.files) {
    $handle = [System.IO.File]::Open((Inside $targetRoot $file.path),[System.IO.FileMode]::Open,[System.IO.FileAccess]::ReadWrite,[System.IO.FileShare]::None)
    $handle.Dispose()
}
$backupRelative = 'update-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$backupRoot = Inside $targetRoot $backupRelative
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$backupFiles = @()
foreach ($file in $manifest.files) {
    $backupFile = Inside $backupRoot $file.path
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($backupFile)) -Force | Out-Null
    Copy-Item -LiteralPath (Inside $targetRoot $file.path) -Destination $backupFile
    AssertHash $backupFile $file.baseSha256
    $backupFiles += [PSCustomObject]@{path=$file.path;sha256=$file.baseSha256}
}
@{target=$targetRoot;files=$backupFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupRoot 'backup-manifest.json') -Encoding UTF8
try {
    AssertClosed
    foreach ($file in $manifest.files) { Copy-Item -LiteralPath (Inside $payloadRoot $file.path) -Destination (Inside $targetRoot $file.path) -Force }
    foreach ($file in $manifest.files) { AssertHash (Inside $targetRoot $file.path) $file.sha256 }
} catch {
    $updateFailure = $_
    try {
        foreach ($file in $manifest.files) { Copy-Item -LiteralPath (Inside $backupRoot $file.path) -Destination (Inside $targetRoot $file.path) -Force }
        foreach ($file in $manifest.files) { AssertHash (Inside $targetRoot $file.path) $file.baseSha256 }
    } catch { throw ('Update and automatic rollback failed. Keep ReportDesk closed and restore the backup: ' + $backupRoot) }
    throw ('Update failed; original files restored. ' + $updateFailure.Exception.Message)
}
Write-Output ('PASS update. Backup: ' + $backupRoot)
Write-Output 'Start ReportDesk.exe. User data, saved passwords and report-visibility.xml were not changed.'
