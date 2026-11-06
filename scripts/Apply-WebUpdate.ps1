#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SitePath,
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$BackupRoot = (Join-Path $env:ProgramData 'ReportDesk\WebUpdateBackups'),
    [string]$HealthUrl,
    [switch]$CheckOnly,
    [switch]$Rollback,
    [string]$BackupDirectory
)

# This updater is deliberately pinned to this release, not a general file copier.
$script:WebUpdateRelease = 'LocalHis-20261106'
$script:WebUpdateCoreHash = '6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7'
$script:WebUpdateFiles = [ordered]@{
    'bin/ReportDesk.Web.dll' = 'FE28A586EAAB3807E01EF2B7B5834238A27F90322C141E10476F75DE707553FF'
    'UI/close-emblem.svg' = '278CF05CC8041341408B41420A57C97A55BDC630B18D8EEC4C9517662FAA815B'
    'UI/execution.css' = 'D2D22782AEF6BF4A246B6B7399C7B97355E81088924A77C98A271C3F2779F279'
    'UI/index.html' = '86100EB0B10163B98739A3D5A10161E3B9D2324F7BCA91C85179CB4A8F028CEE'
    'UI/local-directory.js' = '58F8A6BA50F1AACBCFC430B61398357D9D0383E213E71351E6458EB7326E1BB1'
    'UI/query-form.js' = 'CFCEEF4E0009085B0F33EBB2D43FAA052BAA845CCB9F60D3C148EF23DF434BBE'
    'UI/README.md' = '100A4F39423BE9B0718E3C082E48A6C99BA4B4DCF6790B456E8E854397C84796'
    'UI/renderer.js' = 'C4542386D919EF9878302CC7BB386120FA79B573C513A1D92DCAFCC671C08159'
    'UI/sql-editor.css' = '57879F1B3CF250FB7DCBE40C750D67A076B45CCB1682BDEEABC42C922A2E80F1'
    'UI/sql-editor.js' = '57D2C576D6FEF353C14C50FBAA531CC3C59CFFA797865D5D62678B616AF72795'
    'UI/styles.css' = 'B18CF887BA0FE73D6E51C3900ADA6D877951777A605A6424215F27D73AC353C2'
    'UI/transport.js' = 'FFE96D1D003067E3A2A71DA6F2065ADEA646BB37A40C392FB8B669C86DF0E697'
    'UI/web.css' = '7241078D0F15CB9EE06CE5BEFA35DD5EE2A8E52F65627C06A679EA9146EEA98F'
    'UI/web.js' = '3B4DE03092E182A4000A127DAEB56D95F30373D3D0F95F9ADA94771513E2FCD4'
    'UI/xml-editor.js' = '4C2AB1984C4547B00B5374129211B93159B4CE8CE3F4D2F7039B44F996C11612'
}
. (Join-Path $PSScriptRoot 'WebUpdate.Iis.ps1')

function Assert-ReportDeskAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run Windows PowerShell as Administrator on the IIS server.'
    }
}

function Resolve-ReportDeskSafePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or
        $Path.Substring(2).Contains(':') -or $Path -match '[*?]') { throw 'An absolute local drive path is required.' }
    foreach ($part in $Path.Substring(3).Split([char[]]'\/')) {
        if ($part -in @('.', '..') -or $part -match '[. ]$') { throw 'Ambiguous path components are not allowed.' }
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full.Length -le 3) { throw 'A drive root cannot be used.' }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw ('Reparse points are not allowed: ' + $current)
            }
        }
    }
    return $full
}

function Test-ReportDeskInside([string]$Path, [string]$Root) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ReportDeskSeparate([string]$First, [string]$Second) {
    $canonicalFirst = Get-ReportDeskCanonicalDirectoryPath $First
    $canonicalSecond = Get-ReportDeskCanonicalDirectoryPath $Second
    if ((Test-ReportDeskInside $canonicalFirst $canonicalSecond) -or (Test-ReportDeskInside $canonicalSecond $canonicalFirst)) {
        throw ('Paths must not overlap: ' + $First + ' | ' + $Second)
    }
}

function Get-ReportDeskHash([string]$Path) {
    $safe = Resolve-ReportDeskSafePath $Path
    if (-not [IO.File]::Exists($safe)) { return $null }
    return (Get-FileHash -LiteralPath $safe -Algorithm SHA256).Hash
}

function Get-ReportDeskTreeFiles([string]$Root) {
    $safe = Resolve-ReportDeskSafePath $Root
    if (-not (Test-Path -LiteralPath $safe)) { return }
    if (-not [IO.Directory]::Exists($safe)) { throw ('Expected directory: ' + $safe) }
    foreach ($item in Get-ChildItem -LiteralPath $safe -Force) {
        $null = Resolve-ReportDeskSafePath $item.FullName
        if ($item.PSIsContainer) { Get-ReportDeskTreeFiles $item.FullName } else { $item.FullName }
    }
}

function Get-ReportDeskPaths([string]$SitePath) {
    $configPath = Join-Path $SitePath 'Web.config'
    $null = Resolve-ReportDeskSafePath $configPath
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($configPath, $settings)
    try { $document = New-Object Xml.XmlDocument; $document.XmlResolver = $null; $document.Load($reader) }
    finally { $reader.Dispose() }
    $appSettings = $document.SelectSingleNode('/configuration/appSettings')
    if ($null -eq $appSettings -or $appSettings.HasAttribute('configSource') -or $appSettings.HasAttribute('file') -or
        $document.SelectNodes('/configuration/location').Count -ne 0) {
        throw 'External/inherited/location-based settings require manual review; this updater needs explicit local appSettings.'
    }
    if ($appSettings.SelectNodes('clear|remove').Count -ne 0) { throw 'appSettings clear/remove directives require effective-configuration review before automatic update.' }
    $dataNodes = $appSettings.SelectNodes('add[@key="ReportDesk.DataDirectory"]')
    $reportNodes = $appSettings.SelectNodes('add[@key="ReportDesk.ReportsDirectory"]')
    if ($dataNodes.Count -ne 1 -or $reportNodes.Count -ne 1) { throw 'Explicit unique ReportDesk.DataDirectory and ReportDesk.ReportsDirectory settings are required (ReportsDirectory may be explicitly empty).' }
    $data = Resolve-ReportDeskSafePath $dataNodes[0].GetAttribute('value')
    $reports = Join-Path $data 'Reports'
    if ($reportNodes.Count -eq 1 -and $reportNodes[0].GetAttribute('value').Trim().Length -gt 0) {
        $reports = Resolve-ReportDeskSafePath $reportNodes[0].GetAttribute('value')
    }
    Assert-ReportDeskSeparate $SitePath $data
    Assert-ReportDeskSeparate $SitePath $reports
    if (-not [IO.Directory]::Exists($data)) { throw 'DataDirectory does not exist; verify the existing site configuration.' }
    return [pscustomobject]@{ SitePath = $SitePath; DataDirectory = $data; ConfigDirectory = (Join-Path $data 'config'); ReportsDirectory = $reports }
}

function Get-ReportDeskStageFiles($Paths, [string]$Marker) {
    if ($Marker -notmatch '^ReportDeskUpdate-[a-f0-9]{32}$') { throw 'Invalid staging marker.' }
    foreach ($relative in $script:WebUpdateFiles.Keys) {
        $target = Join-Path $Paths.SitePath $relative
        Join-Path ([IO.Path]::GetDirectoryName($target)) ('.reportdesk-update-' + $Marker + '-' + [IO.Path]::GetFileName($target) + '.tmp')
    }
}

function Get-ReportDeskSnapshot($Paths, [string]$StageMarker) {
    $files = @()
    $staging = @(); if ($StageMarker) { $staging = @(Get-ReportDeskStageFiles $Paths $StageMarker) }
    foreach ($name in @('bin', 'UI')) {
        foreach ($file in @(Get-ReportDeskTreeFiles (Join-Path $Paths.SitePath $name))) {
            if ($staging -contains $file) { continue }
            $relative = $file.Substring($Paths.SitePath.Length + 1).Replace('\', '/')
            $files += [pscustomobject]@{ Key = ('site/' + $relative); Hash = (Get-ReportDeskHash $file) }
        }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Paths.SitePath -File -Force | Where-Object { $_.Extension -eq '.config' -or $_.Name -eq 'Global.asax' }) {
        $files += [pscustomobject]@{ Key = ('site/' + $item.Name); Hash = (Get-ReportDeskHash $item.FullName) }
    }
    foreach ($file in @(Get-ReportDeskTreeFiles $Paths.ConfigDirectory)) {
        $relative = $file.Substring($Paths.ConfigDirectory.Length + 1).Replace('\', '/')
        $files += [pscustomobject]@{ Key = ('config/' + $relative); Hash = (Get-ReportDeskHash $file) }
    }
    return @($files | Sort-Object Key)
}

function Get-ReportDeskSnapshotFile($Paths, [string]$Key) {
    if ($Key -notmatch '^(site|config)/[^:]+$' -or $Key.Contains('\') -or
        @($Key.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) { throw 'Invalid backup manifest path.' }
    if ($Key.StartsWith('config/')) { $path = Join-Path $Paths.ConfigDirectory $Key.Substring(7) }
    else {
        $relative = $Key.Substring(5)
        if ($relative -notmatch '^(bin|UI)/' -and $relative -notmatch '^[^/]+\.config$' -and $relative -ne 'Global.asax') {
            throw 'Backup manifest path outside allowed application files.'
        }
        $path = Join-Path $Paths.SitePath $relative
    }
    return Resolve-ReportDeskSafePath $path
}

function Assert-ReportDeskSnapshot($Expected, $Actual, [switch]$ProtectedOnly) {
    $expectedMap = @{}; $actualMap = @{}
    foreach ($entry in @($Expected)) {
        if ($ProtectedOnly -and $entry.Key.StartsWith('site/') -and $script:WebUpdateFiles.Contains($entry.Key.Substring(5))) { continue }
        if ($expectedMap.ContainsKey($entry.Key)) { throw 'Duplicate manifest entry.' }
        $expectedMap[$entry.Key] = $entry.Hash
    }
    foreach ($entry in @($Actual)) {
        if ($ProtectedOnly -and $entry.Key.StartsWith('site/') -and $script:WebUpdateFiles.Contains($entry.Key.Substring(5))) { continue }
        $actualMap[$entry.Key] = $entry.Hash
    }
    if ($expectedMap.Count -ne $actualMap.Count) { throw 'Protected/baseline file list changed; no configuration will be overwritten.' }
    foreach ($key in $expectedMap.Keys) {
        if ($actualMap[$key] -ne $expectedMap[$key]) { throw ('File changed unexpectedly: ' + $key) }
    }
}

function Assert-ReportDeskPackage([string]$PackageDirectory) {
    $payload = Join-Path $PackageDirectory 'payload'
    $found = @(Get-ReportDeskTreeFiles $payload)
    if ($found.Count -ne $script:WebUpdateFiles.Count) { throw 'Update payload has missing or unexpected files.' }
    foreach ($relative in $script:WebUpdateFiles.Keys) {
        if ((Get-ReportDeskHash (Join-Path $payload $relative)) -ne $script:WebUpdateFiles[$relative]) {
            throw ('Update package SHA256 mismatch: ' + $relative)
        }
    }
}

function Initialize-ReportDeskBackupRoot([string]$Path) {
    $safe = Resolve-ReportDeskSafePath $Path
    if (-not [IO.Directory]::Exists($safe)) { $null = [IO.Directory]::CreateDirectory($safe) }
    # Backup files can contain encrypted connection settings. Disable inherited public access.
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $admin = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl.SetOwner($admin)
    foreach ($sid in @($admin, $system)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $safe -AclObject $acl
    return $safe
}

function Assert-ReportDeskBackupTrusted([string]$Directory) {
    $safe = Resolve-ReportDeskSafePath $Directory
    $allowed = @('S-1-5-32-544', 'S-1-5-18')
    $acl = Get-Acl -LiteralPath $safe
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($allowed -notcontains $owner -or -not $acl.AreAccessRulesProtected) { throw 'Backup root must be owned by Administrators/SYSTEM with protected permissions.' }
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue($safe)
    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        $null = Resolve-ReportDeskSafePath $item
        $security = Get-Acl -LiteralPath $item
        if ($allowed -notcontains $security.GetOwner([Security.Principal.SecurityIdentifier]).Value) {
            throw 'Every backup directory/file must be owned by Administrators/SYSTEM.'
        }
        foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $allowed -notcontains $rule.IdentityReference.Value) {
                throw 'Backup access is not restricted to Administrators/SYSTEM. Do not trust a writable/unprotected recovery directory.'
            }
        }
        if ([IO.Directory]::Exists($item)) {
            foreach ($child in Get-ChildItem -LiteralPath $item -Force) { $pending.Enqueue($child.FullName) }
        }
    }
}

function Set-ReportDeskFile([string]$Source, [string]$Destination, [string]$ExpectedHash) {
    $sourceSafe = Resolve-ReportDeskSafePath $Source
    $destinationSafe = Resolve-ReportDeskSafePath $Destination
    $parent = [IO.Path]::GetDirectoryName($destinationSafe)
    if (-not [IO.Directory]::Exists($parent)) { $null = [IO.Directory]::CreateDirectory($parent) }
    # Temp is in the same directory so replace/rename is atomic for this single file.
    if (-not $script:WebUpdateStageMarker -or $ExpectedHash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'A pinned hash and durable staging marker are required.' }
    $temporary = Join-Path $parent ('.reportdesk-update-' + $script:WebUpdateStageMarker + '-' + [IO.Path]::GetFileName($destinationSafe) + '.tmp')
    if (Test-Path -LiteralPath $temporary) { throw 'A staging file already exists. Resume using the recorded backup directory.' }
    try {
        [IO.File]::Copy($sourceSafe, $temporary, $false)
        if ((Get-ReportDeskHash $temporary) -ne $ExpectedHash) { throw 'Staged copy verification failed.' }
        if ([IO.File]::Exists($destinationSafe)) { [IO.File]::Replace($temporary, $destinationSafe, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $destinationSafe) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Save-ReportDeskJournal($Journal, [string]$Directory) {
    $destination = Join-Path $Directory 'journal.json'
    $temporary = Join-Path $Directory ('journal-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Journal | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
        if ([IO.File]::Exists($destination)) { [IO.File]::Replace($temporary, $destination, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $destination) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function New-ReportDeskOffline([string]$SitePath, [string]$Marker) {
    if ($Marker -notmatch '^ReportDeskUpdate-[a-f0-9]{32}$') { throw 'Invalid maintenance marker.' }
    $path = Resolve-ReportDeskSafePath (Join-Path $SitePath 'app_offline.htm')
    $temporary = Resolve-ReportDeskSafePath (Join-Path $SitePath ('.reportdesk-offline-' + $Marker + '.tmp'))
    if (Test-Path -LiteralPath $path) { throw 'An existing app_offline.htm must not be overwritten.' }
    $bytes = [Text.Encoding]::UTF8.GetBytes('<!doctype html><html><body><h1>ReportDesk maintenance</h1><p>Please retry shortly.</p><!-- ' + $Marker + ' --></body></html>' + (' ' * 1024))
    try {
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $path)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Remove-ReportDeskOfflineStage([string]$SitePath, [string]$Marker) {
    if ($Marker -notmatch '^ReportDeskUpdate-[a-f0-9]{32}$') { throw 'Invalid maintenance marker.' }
    $temporary = Resolve-ReportDeskSafePath (Join-Path $SitePath ('.reportdesk-offline-' + $Marker + '.tmp'))
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}

function Test-ReportDeskOwnedOffline([string]$SitePath, [string]$Marker) {
    $path = Resolve-ReportDeskSafePath (Join-Path $SitePath 'app_offline.htm')
    if (-not [IO.File]::Exists($path)) { return $false }
    $expected = '<!doctype html><html><body><h1>ReportDesk maintenance</h1><p>Please retry shortly.</p><!-- ' + $Marker + ' --></body></html>' + (' ' * 1024)
    return [IO.File]::ReadAllText($path) -ceq $expected
}

function Remove-ReportDeskOffline([string]$SitePath, [string]$Marker) {
    if (-not (Test-ReportDeskOwnedOffline $SitePath $Marker)) { throw 'Maintenance file ownership changed. It was not removed.' }
    [IO.File]::Delete((Join-Path $SitePath 'app_offline.htm'))
}

function Confirm-ReportDeskMaintenance($Journal, [string]$SitePath, [string]$Directory) {
    Write-Host 'Waiting up to 30 seconds for both maintenance responses (HTTP 503 + operation marker, two consecutive rounds).'
    try {
        $result = Test-ReportDeskOffline -BaseUrl $Journal.HealthUrl -Marker $Journal.Marker -AssertMaintenance {
            if (-not (Test-ReportDeskOwnedOffline $SitePath $Journal.Marker)) { throw 'Maintenance ownership changed.' }
        }
    } catch {
        $originalError = $_
        if ($_.Exception.Data.Contains('ReportDeskOfflineEvidence')) {
            try {
                $evidence = $_.Exception.Data['ReportDeskOfflineEvidence'] | ConvertFrom-Json
                $Journal | Add-Member -NotePropertyName OfflineEvidence -NotePropertyValue $evidence -Force
                Save-ReportDeskJournal $Journal $Directory
                Write-Host ('Maintenance diagnostic summary: ' + (Join-Path $Directory 'journal.json'))
            } catch { Write-Warning 'Could not persist the maintenance diagnostic summary; the application update remains blocked.' }
        }
        throw $originalError
    }
    if (-not (Test-ReportDeskOwnedOffline $SitePath $Journal.Marker)) { throw 'Maintenance ownership changed.' }
    if ($null -ne $result -and $null -ne $result.PSObject.Properties['Evidence']) {
        $Journal | Add-Member -NotePropertyName OfflineEvidence -NotePropertyValue $result.Evidence -Force
        Save-ReportDeskJournal $Journal $Directory
    }
}

function Assert-ReportDeskContext($Expected, [string]$SitePath) {
    $current = Get-ReportDeskIisContext -SitePath $SitePath
    if ($current.SnapshotHash -ne $Expected) { throw 'IIS application mappings/identity/settings changed; automatic update stopped.' }
}

function Get-ReportDeskMutexName([string]$SitePath) {
    $SitePath = Get-ReportDeskCanonicalDirectoryPath $SitePath
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hex = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($SitePath.ToLowerInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    return 'Global\ReportDeskWebUpdate-' + $hex
}

function Resolve-ReportDeskHealthUrl($Context, [string]$HealthUrl, [switch]$OfflineRecovery) {
    if ($HealthUrl) {
        $HealthUrl = Assert-ReportDeskHealthBinding -BaseUrl $HealthUrl -Context $Context
        if ($OfflineRecovery) { return $HealthUrl }
        $health = Test-ReportDeskHealth -BaseUrl $HealthUrl
        return $health.BaseUrl
    }
    foreach ($candidate in @($Context.CandidateBaseUrls)) {
        Write-Host ('Checking application URL: ' + $candidate)
        try { $candidate = Assert-ReportDeskHealthBinding -BaseUrl $candidate -Context $Context; $health = Test-ReportDeskHealth -BaseUrl $candidate; return $health.BaseUrl } catch { }
    }
    throw 'No reachable trusted site URL. Supply -HealthUrl with the existing HTTPS application URL (including its application path). Do not bypass certificate checks.'
}

function Assert-ReportDeskBackupLocation([string]$Directory, $Paths, $Context, [string]$PackageDirectory) {
    foreach ($other in @($Paths.SitePath, $Paths.DataDirectory, $Paths.ReportsDirectory, $PackageDirectory)) {
        Assert-ReportDeskSeparate $Directory $other
    }
    foreach ($mapping in @($Context.Mappings)) { Assert-ReportDeskSeparate $Directory $mapping.PhysicalPath }
}

function Assert-ReportDeskRecoveryFiles($Journal, $Paths, [string]$Directory) {
    # Validate the ENTIRE recovery set before writing any file. Protect later edits.
    Assert-ReportDeskSnapshot $Journal.Before @(Get-ReportDeskSnapshot $Paths $Journal.Marker) -ProtectedOnly
    $oldMap = @{}
    foreach ($entry in @($Journal.Before)) {
        $null = Get-ReportDeskSnapshotFile $Paths $entry.Key
        if ($oldMap.ContainsKey($entry.Key) -or $entry.Hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid recovery manifest.' }
        $oldMap[$entry.Key] = $entry.Hash
        $backupFile = Resolve-ReportDeskSafePath (Join-Path (Join-Path $Directory 'files') $entry.Key)
        if ((Get-ReportDeskHash $backupFile) -ne $entry.Hash) { throw ('Backup SHA256 mismatch: ' + $entry.Key) }
    }
    foreach ($relative in $script:WebUpdateFiles.Keys) {
        $current = Get-ReportDeskHash (Join-Path $Paths.SitePath $relative)
        $old = $oldMap['site/' + $relative]
        if ($current -ne $old -and $current -ne $script:WebUpdateFiles[$relative]) { throw ('File changed after update; manual review required: ' + $relative) }
    }
    return $oldMap
}

function Restore-ReportDeskFiles($Journal, $Paths, [string]$Directory) {
    $oldMap = Assert-ReportDeskRecoveryFiles $Journal $Paths $Directory
    # These names are reserved by the durable operation marker before any staging starts.
    # An interrupted copy may have partial bytes; only these exact scratch files may be removed.
    foreach ($temporary in @(Get-ReportDeskStageFiles $Paths $Journal.Marker)) {
        $null = Resolve-ReportDeskSafePath $temporary
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
    foreach ($relative in $script:WebUpdateFiles.Keys) {
        $target = Join-Path $Paths.SitePath $relative
        $old = $oldMap['site/' + $relative]
        $current = Get-ReportDeskHash $target
        if ($current -eq $old) { continue }
        if ($current -ne $script:WebUpdateFiles[$relative]) { throw ('Concurrent edit detected during rollback: ' + $relative) }
        if ($null -ne $old) { Set-ReportDeskFile -Source (Join-Path (Join-Path $Directory 'files/site') $relative) -Destination $target -ExpectedHash $old }
        else { [IO.File]::Delete((Resolve-ReportDeskSafePath $target)) }
    }
    Assert-ReportDeskSnapshot $Journal.Before @(Get-ReportDeskSnapshot $Paths)
}

function Invoke-ReportDeskWebUpdate {
    [CmdletBinding()]
    param([string]$SitePath, [string]$PackageDirectory = $PSScriptRoot,
        [string]$BackupRoot = (Join-Path $env:ProgramData 'ReportDesk\WebUpdateBackups'),
        [string]$HealthUrl, [switch]$CheckOnly, [switch]$Rollback, [string]$BackupDirectory)
    $ErrorActionPreference = 'Stop'
    if ($CheckOnly -and $Rollback) { throw 'CheckOnly and Rollback cannot be combined.' }
    Assert-ReportDeskAdministrator
    if (-not $SitePath) { $SitePath = (Read-Host 'ReportDesk IIS physical application path').Trim().Trim('"') }
    $site = Resolve-ReportDeskSafePath $SitePath
    $package = Resolve-ReportDeskSafePath $PackageDirectory
    Assert-ReportDeskSeparate $site $package
    if (-not [IO.Directory]::Exists((Join-Path $site 'bin')) -or -not [IO.Directory]::Exists((Join-Path $site 'UI')) -or
        -not [IO.File]::Exists((Join-Path $site 'Global.asax'))) { throw 'Expected existing ReportDesk application root (Global.asax, bin and UI).' }
    $mutex = New-Object Threading.Mutex($false, (Get-ReportDeskMutexName $site))
    $locked = $false; $journal = $null; $backup = $null; $ownedOffline = $false; $createdOfflineThisRun = $false; $canRestore = $false; $filesTouched = $false
    try {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'Another updater is already operating on this application.' }
        $paths = Get-ReportDeskPaths $site
        $context = Get-ReportDeskIisContext -SitePath $site
        if ((Get-ReportDeskHash (Join-Path $site 'bin/ReportDesk.Core.dll')) -ne $script:WebUpdateCoreHash) {
            throw ('Core baseline mismatch. Expected SHA256: ' + $script:WebUpdateCoreHash + '. No application files changed.')
        }
        $offlinePath = Join-Path $site 'app_offline.htm'
        if ($Rollback) {
            if (-not $BackupDirectory) { throw 'Rollback requires -BackupDirectory from the update output.' }
            $backup = Resolve-ReportDeskSafePath $BackupDirectory
            Assert-ReportDeskBackupLocation $backup $paths $context $package
            Assert-ReportDeskBackupTrusted $backup
            $journalPath = Resolve-ReportDeskSafePath (Join-Path $backup 'journal.json')
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
            if ($journal.Release -ne $script:WebUpdateRelease -or $journal.SitePath -ne $site -or
                $journal.DataDirectory -ne $paths.DataDirectory -or $journal.Marker -notmatch '^ReportDeskUpdate-[a-f0-9]{32}$' -or
                $journal.State -notin @('Preparing', 'BackedUp', 'Updating', 'Updated', 'RecoveryRequired', 'Restoring', 'RolledBack')) { throw 'Backup does not match this application/release or is incomplete.' }
            $script:WebUpdateStageMarker = $journal.Marker
            Assert-ReportDeskContext $journal.IisSnapshot $site
            if (Test-Path -LiteralPath $offlinePath) {
                if (-not (Test-ReportDeskOwnedOffline $site $journal.Marker)) { throw 'Existing maintenance file belongs to another operation.' }
                $ownedOffline = $true
            }
            $url = Resolve-ReportDeskHealthUrl $context $journal.HealthUrl -OfflineRecovery
            if ($HealthUrl -and $HealthUrl.TrimEnd('/') -ne $url.TrimEnd('/')) { throw 'Recovery must use the application URL recorded in the backup.' }
            if ($journal.State -eq 'Preparing') {
                # No application writes are allowed until the BackedUp journal is persisted.
                if ($ownedOffline) { Remove-ReportDeskOffline $site $journal.Marker; $ownedOffline = $false }
                Remove-ReportDeskOfflineStage $site $journal.Marker
                $null = Test-ReportDeskHealth -BaseUrl $url
                $journal | Add-Member -NotePropertyName Cleanup -NotePropertyValue ([pscustomobject]@{
                    MaintenanceRemoved = $true; HealthVerified = $true; Result = 'Verified'
                }) -Force
                $journal.State = 'Cancelled'; Save-ReportDeskJournal $journal $backup
                return [pscustomobject]@{ Status = 'Cancelled'; BackupDirectory = $backup; HealthUrl = $url }
            }
            $null = Assert-ReportDeskRecoveryFiles $journal $paths $backup
            Remove-ReportDeskOfflineStage $site $journal.Marker
            if (-not $ownedOffline) { New-ReportDeskOffline $site $journal.Marker; $ownedOffline = $true; $createdOfflineThisRun = $true }
            Confirm-ReportDeskMaintenance $journal $site $backup
            Assert-ReportDeskContext $journal.IisSnapshot $site
            $canRestore = $true
            $journal.State = 'Restoring'; Save-ReportDeskJournal $journal $backup
            Restore-ReportDeskFiles $journal $paths $backup
            Assert-ReportDeskContext $journal.IisSnapshot $site
            Remove-ReportDeskOffline $site $journal.Marker; $ownedOffline = $false
            $null = Test-ReportDeskHealth -BaseUrl $url
            $journal.State = 'RolledBack'; Save-ReportDeskJournal $journal $backup
            Write-Host 'ROLLBACK VERIFIED. Original application files restored; database configuration unchanged.'
            return [pscustomobject]@{ Status = 'RolledBack'; BackupDirectory = $backup; HealthUrl = $url }
        }
        if (Test-Path -LiteralPath $offlinePath) { throw 'app_offline.htm already exists. Use the matching backup rollback command or resolve the earlier maintenance first.' }
        Assert-ReportDeskPackage $package
        $backupBase = Resolve-ReportDeskSafePath $BackupRoot
        Assert-ReportDeskBackupLocation $backupBase $paths $context $package
        $url = Resolve-ReportDeskHealthUrl $context $HealthUrl
        # Reject links and unreadable files before going offline.
        $preflight = @(Get-ReportDeskSnapshot $paths)
        Write-Host ('Application: ' + $context.SiteName + $context.AppPath + ' | ' + $site)
        Write-Host ('Core SHA256 matched: ' + $script:WebUpdateCoreHash)
        Write-Host ('Health URL: ' + $url)
        if ($CheckOnly) {
            Write-Host 'CHECK PASSED. No maintenance file, backup, application, IIS or database change was made.'
            return [pscustomobject]@{ Status = 'Checked'; BackupDirectory = $null; HealthUrl = $url }
        }
        # Apply restrictive permissions only to a NEW per-operation directory, never to a user-selected existing root.
        $backup = Join-Path $backupBase ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
        $backup = Initialize-ReportDeskBackupRoot $backup
        Assert-ReportDeskBackupTrusted $backup
        $journal = [pscustomobject]@{ Format = 1; Release = $script:WebUpdateRelease; SitePath = $site; DataDirectory = $paths.DataDirectory;
            Marker = ('ReportDeskUpdate-' + [Guid]::NewGuid().ToString('N')); IisSnapshot = $context.SnapshotHash;
            HealthUrl = $url; State = 'Preparing'; Before = @(); CreatedUtc = [DateTime]::UtcNow.ToString('o') }
        Save-ReportDeskJournal $journal $backup
        $script:WebUpdateStageMarker = $journal.Marker
        Write-Host ('Backup/recovery directory: ' + $backup)
        Write-Host 'Only this application will briefly go offline. No IIS restart, application-pool operation or binding/configuration change.'
        New-ReportDeskOffline $site $journal.Marker; $ownedOffline = $true; $createdOfflineThisRun = $true
        Confirm-ReportDeskMaintenance $journal $site $backup
        Assert-ReportDeskContext $context.SnapshotHash $site
        $journal.Before = @(Get-ReportDeskSnapshot $paths)
        Assert-ReportDeskSnapshot $preflight $journal.Before
        foreach ($entry in $journal.Before) {
            if (-not (Test-ReportDeskOwnedOffline $site $journal.Marker)) { throw 'Maintenance ownership changed before backup completed.' }
            $source = Get-ReportDeskSnapshotFile $paths $entry.Key
            $destination = Join-Path (Join-Path $backup 'files') $entry.Key
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($source, $destination, $false)
            if ((Get-ReportDeskHash $destination) -ne $entry.Hash) { throw ('Backup verification failed: ' + $entry.Key) }
        }
        Assert-ReportDeskSnapshot $journal.Before @(Get-ReportDeskSnapshot $paths)
        Assert-ReportDeskBackupTrusted $backup
        $journal.State = 'BackedUp'; Save-ReportDeskJournal $journal $backup
        $canRestore = $true
        $journal.State = 'Updating'; Save-ReportDeskJournal $journal $backup
        foreach ($relative in $script:WebUpdateFiles.Keys) {
            if (-not (Test-ReportDeskOwnedOffline $site $journal.Marker)) { throw 'Maintenance ownership changed before file replacement.' }
            $target = Join-Path $site $relative
            $oldEntry = @($journal.Before | Where-Object { $_.Key -eq ('site/' + $relative) })
            $oldHash = $null; if ($oldEntry.Count -eq 1) { $oldHash = $oldEntry[0].Hash }
            if ((Get-ReportDeskHash $target) -ne $oldHash) { throw ('Concurrent edit: ' + $relative) }
            $source = Join-Path (Join-Path $package 'payload') $relative
            if ((Get-ReportDeskHash $source) -ne $script:WebUpdateFiles[$relative]) { throw 'Package changed while update was running.' }
            $filesTouched = $true
            Set-ReportDeskFile -Source $source -Destination $target -ExpectedHash $script:WebUpdateFiles[$relative]
            if ((Get-ReportDeskHash $target) -ne $script:WebUpdateFiles[$relative]) { throw ('Installed file verification failed: ' + $relative) }
        }
        Assert-ReportDeskSnapshot $journal.Before @(Get-ReportDeskSnapshot $paths) -ProtectedOnly
        Assert-ReportDeskContext $context.SnapshotHash $site
        Remove-ReportDeskOffline $site $journal.Marker; $ownedOffline = $false
        $null = Test-ReportDeskHealth -BaseUrl $url
        Assert-ReportDeskSnapshot $journal.Before @(Get-ReportDeskSnapshot $paths) -ProtectedOnly
        $journal.State = 'Updated'; Save-ReportDeskJournal $journal $backup
        Write-Host 'UPDATE VERIFIED. Core/dependencies, Web.config and database connection files are unchanged.'
        return [pscustomobject]@{ Status = 'Updated'; BackupDirectory = $backup; HealthUrl = $url }
    } catch {
        $failure = $_.Exception.Message
        if ($journal -and ($canRestore -or $filesTouched)) {
            try {
                if (-not $ownedOffline) { New-ReportDeskOffline $site $journal.Marker; $ownedOffline = $true }
                if (-not (Test-ReportDeskOwnedOffline $site $journal.Marker)) { throw 'Maintenance ownership changed.' }
                Confirm-ReportDeskMaintenance $journal $site $backup
                Assert-ReportDeskContext $journal.IisSnapshot $site
                $journal.State = 'Restoring'; Save-ReportDeskJournal $journal $backup
                Restore-ReportDeskFiles $journal $paths $backup
                Assert-ReportDeskContext $journal.IisSnapshot $site
                Remove-ReportDeskOffline $site $journal.Marker; $ownedOffline = $false
                $null = Test-ReportDeskHealth -BaseUrl $journal.HealthUrl
                $journal.State = 'RolledBack'; Save-ReportDeskJournal $journal $backup
                throw ('Update failed; original application automatically restored and health checked. Reason: ' + $failure)
            } catch {
                $recoveryFailure = $_.Exception.Message
                if ($journal.State -eq 'RolledBack') { throw $recoveryFailure }
                if (-not $ownedOffline -and -not (Test-Path -LiteralPath (Join-Path $site 'app_offline.htm'))) {
                    try { New-ReportDeskOffline $site $journal.Marker; $ownedOffline = $true } catch { }
                }
                $journal.State = 'RecoveryRequired'
                try { Save-ReportDeskJournal $journal $backup } catch { }
                throw ('RECOVERY REQUIRED. Backup: ' + $backup + '. Maintenance file retained when possible; do not delete it or overwrite config blindly. Update: ' + $failure + ' Recovery: ' + $recoveryFailure)
            }
        }
        if ($ownedOffline -and $createdOfflineThisRun -and $journal -and -not $filesTouched) {
            $previousState = $journal.State
            $cleanup = [pscustomobject]@{ MaintenanceRemoved = $false; HealthVerified = $false; Result = 'NotVerified' }
            try {
                Remove-ReportDeskOffline $site $journal.Marker; $ownedOffline = $false
                $cleanup.MaintenanceRemoved = $true
                $null = Test-ReportDeskHealth -BaseUrl $journal.HealthUrl
                Assert-ReportDeskContext $journal.IisSnapshot $site
                $cleanup.HealthVerified = $true; $cleanup.Result = 'Verified'
                if ($journal.State -eq 'Preparing') { $journal.State = 'Cancelled' }
                $journal | Add-Member -NotePropertyName Cleanup -NotePropertyValue $cleanup -Force
                Save-ReportDeskJournal $journal $backup
            } catch {
                $journal.State = $previousState
                $cleanup.Result = 'CleanupOrHealthNotVerified'
                $journal | Add-Member -NotePropertyName Cleanup -NotePropertyValue $cleanup -Force
                try { Save-ReportDeskJournal $journal $backup } catch { }
                throw ($failure + ' This attempt replaced no application files, but cleanup/availability could not be fully verified. Recovery directory: ' + $backup + '. Do not delete a maintenance file belonging to another operation.')
            }
            throw ($failure + ' This attempt replaced no application files. Its maintenance file was removed and application availability was health checked. Recovery/diagnostic directory: ' + $backup)
        }
        throw $failure
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        $script:WebUpdateStageMarker = $null
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ($Rollback -and -not $BackupDirectory) {
            $PSBoundParameters['BackupDirectory'] = (Read-Host 'Backup/recovery directory printed by the update').Trim().Trim('"')
        }
        $result = Invoke-ReportDeskWebUpdate @PSBoundParameters
        $result | Format-List
        exit 0
    } catch {
        Write-Host ('FAILED: ' + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
}
