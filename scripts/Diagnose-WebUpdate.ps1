#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SitePath = 'C:\a_userspace\ReportTool\ReportDesk-Web',
    [string]$OutputDirectory,
    [string[]]$SearchRoots,
    [ValidateRange(1, 120)][int]$SearchSeconds = 30,
    [ValidateRange(1, 20000)][int]$MaximumDirectories = 5000
)

. (Join-Path $PSScriptRoot 'WebDiagnostic.Iis.ps1')
# Reuse the tested native directory canonicalizer for output-path isolation.
. (Join-Path $PSScriptRoot 'WebUpdate.Iis.ps1')

function Get-RdDiagnosticError($Record) {
    if ($Record.CategoryInfo.Category -eq 'PermissionDenied' -or $Record.Exception -is [UnauthorizedAccessException]) { return 'AccessDenied' }
    if ($Record.CategoryInfo.Category -eq 'ObjectNotFound') { return 'NotFound' }
    return $Record.Exception.GetType().Name
}

function ConvertTo-RdDiagnosticPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(2).Contains(':') -or $Path -match '[*?]') { return $null }
    foreach ($part in $Path.Substring(3).Split([char[]]'\/')) {
        if ($part -in @('.', '..') -or $part -match '[. ]$') { return $null }
    }
    try { return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') } catch { return $null }
}

function Get-RdDiagnosticPathStatus([string]$Path) {
    $safe = ConvertTo-RdDiagnosticPath $Path
    if (-not $safe -or $safe.Length -le 2) { return 'UnsupportedPath' }
    for ($current = $safe; $current -and $current.Length -gt 2; $current = [IO.Path]::GetDirectoryName($current)) {
        try {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return 'ReparsePointSkipped' }
        } catch {
            $errorName = Get-RdDiagnosticError $_
            if ($errorName -ne 'NotFound') { return $errorName }
        }
    }
    try {
        $item = Get-Item -LiteralPath $safe -Force -ErrorAction Stop
        if ($item.PSIsContainer) { return 'DirectoryExists' }
        return 'FileExists'
    } catch { return Get-RdDiagnosticError $_ }
}

function Read-RdDiagnosticConfig([string]$SitePath) {
    $path = Join-Path $SitePath 'Web.config'
    $status = Get-RdDiagnosticPathStatus $path
    if ($status -ne 'FileExists') { return [pscustomobject]@{ Status = $status; Path = $path; Settings = @(); HasExternalSettings = $false; HasDirectives = $false } }
    $reader = $null
    try {
        $readerSettings = New-Object Xml.XmlReaderSettings
        $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $readerSettings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($path, $readerSettings)
        $xml = New-Object Xml.XmlDocument; $xml.XmlResolver = $null; $xml.Load($reader)
        $section = $xml.SelectSingleNode('/configuration/appSettings')
        if ($null -eq $section) { throw 'No local appSettings.' }
        $entries = @()
        foreach ($key in @('ReportDesk.DataDirectory', 'ReportDesk.ReportsDirectory')) {
            $nodes = $section.SelectNodes('add[@key="' + $key + '"]')
            foreach ($node in $nodes) {
                $raw = $node.GetAttribute('value')
                $value = ConvertTo-RdDiagnosticPath $raw
                $display = $value
                if (-not $display) { $display = '[empty or non-local path omitted]' }
                $entries += [pscustomobject]@{ Key = $key; Path = $display; Status = (Get-RdDiagnosticPathStatus $raw) }
            }
        }
        return [pscustomobject]@{ Status = 'Read'; Path = $path; Settings = $entries;
            HasExternalSettings = ($section.HasAttribute('file') -or $section.HasAttribute('configSource'));
            HasDirectives = ($section.SelectNodes('clear|remove').Count -gt 0 -or $xml.SelectNodes('/configuration/location').Count -gt 0) }
    } catch { return [pscustomobject]@{ Status = ('ReadFailed:' + (Get-RdDiagnosticError $_)); Path = $path; Settings = @(); HasExternalSettings = $false; HasDirectives = $false } }
    finally { if ($null -ne $reader) { $reader.Dispose() } }
}

function Get-RdDiagnosticRoots([string]$SitePath, $LocalSettings, $EffectiveSettings) {
    [IO.Path]::GetDirectoryName($SitePath)
    foreach ($entry in @($LocalSettings) + @($EffectiveSettings)) {
        if ($null -eq $entry) { continue }
        $value = $null
        if ($null -ne $entry.PSObject.Properties['Path']) { $value = [string]$entry.Path }
        elseif ($null -ne $entry.PSObject.Properties['Value']) { $value = [string]$entry.Value }
        $safe = ConvertTo-RdDiagnosticPath $value
        if ($safe -and $safe.Length -gt 2 -and $entry.Key -eq 'ReportDesk.DataDirectory') { $safe }
    }
    # These are candidates only. A profile/backup file is not assumed to be the active Web configuration.
    foreach ($path in @((Join-Path $env:ProgramData 'ReportDesk'), (Join-Path $env:LOCALAPPDATA 'ReportDesk'),
        (Join-Path $env:WINDIR 'System32\config\systemprofile\AppData\Local\ReportDesk'),
        (Join-Path $env:WINDIR 'SysWOW64\config\systemprofile\AppData\Local\ReportDesk'))) { $path }
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if ($drive.DriveType -ne [IO.DriveType]::Fixed -or -not $drive.IsReady) { continue }
        try {
            foreach ($folder in Get-ChildItem -LiteralPath $drive.RootDirectory.FullName -Directory -Force -ErrorAction Stop) {
                if ($folder.Name -like '*ReportDesk*' -or $folder.Name -like '*ReportTool*') { $folder.FullName }
            }
        } catch { } # Root discovery is best-effort; limitations are reported by the caller.
    }
    $users = Join-Path ($env:SystemDrive + '\') 'Users'
    if ((Get-RdDiagnosticPathStatus $users) -eq 'DirectoryExists') {
        try {
            foreach ($profile in Get-ChildItem -LiteralPath $users -Directory -Force -ErrorAction Stop) {
                if (($profile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                Join-Path $profile.FullName 'AppData\Local\ReportDesk'
            }
        } catch { }
    }
}

function Find-RdDiagnosticConnectionFiles([string[]]$Roots, [int]$Seconds = 30, [int]$MaximumDirectories = 5000) {
    $queue = New-Object 'System.Collections.Generic.Queue[object]'
    $visited = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $files = New-Object 'System.Collections.Generic.List[object]'
    $issues = New-Object 'System.Collections.Generic.List[object]'
    $scope = New-Object 'System.Collections.Generic.List[object]'
    foreach ($root in @($Roots | Sort-Object -Unique)) {
        $safe = ConvertTo-RdDiagnosticPath $root
        $status = Get-RdDiagnosticPathStatus $root
        $scope.Add([pscustomobject]@{ Path = $(if ($safe) { $safe } else { '[unsupported path omitted]' }); Status = $status })
        if ($status -eq 'DirectoryExists') { $queue.Enqueue([pscustomobject]@{ Path = $safe; Depth = 0 }) }
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $count = 0; $entries = 0; $bounded = $false
    while ($queue.Count -gt 0) {
        if ($clock.Elapsed.TotalSeconds -ge $Seconds -or $count -ge $MaximumDirectories -or $entries -ge 100000) { $bounded = $true; break }
        $current = $queue.Dequeue()
        if (-not $visited.Add($current.Path)) { continue }
        $status = Get-RdDiagnosticPathStatus $current.Path
        if ($status -ne 'DirectoryExists') { $issues.Add([pscustomobject]@{ Path = $current.Path; Reason = $status }); continue }
        $count++
        try {
            # No -Include ambiguity; hidden files are included and links are never descended into.
            foreach ($item in Get-ChildItem -LiteralPath $current.Path -Force -ErrorAction Stop) {
                $entries++
                if ($clock.Elapsed.TotalSeconds -ge $Seconds -or $entries -ge 100000) { $bounded = $true; break }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $issues.Add([pscustomobject]@{ Path = $item.FullName; Reason = 'ReparsePointSkipped' }); continue
                }
                if ($item.PSIsContainer) {
                    if ($current.Depth -lt 8) { $queue.Enqueue([pscustomobject]@{ Path = $item.FullName; Depth = ($current.Depth + 1) }) }
                    else { $issues.Add([pscustomobject]@{ Path = $item.FullName; Reason = 'DepthLimit' }); $bounded = $true }
                } elseif ($item.Name -in @('connection.json', 'catalog.json')) {
                    $files.Add([pscustomobject]@{ Path = $item.FullName; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o'); Hidden = [bool]($item.Attributes -band [IO.FileAttributes]::Hidden) })
                }
            }
        } catch { $issues.Add([pscustomobject]@{ Path = $current.Path; Reason = (Get-RdDiagnosticError $_) }) }
    }
    return [pscustomobject]@{ Roots = @($scope.ToArray()); Files = @($files.ToArray()); Issues = @($issues.ToArray());
        Truncated = ($bounded -or $queue.Count -gt 0); DirectoriesInspected = $count; EntriesInspected = $entries;
        Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2); WholeServerSearch = $false }
}

function Invoke-ReportDeskWebDiagnostic {
    [CmdletBinding()]
    param([string]$SitePath = 'C:\a_userspace\ReportTool\ReportDesk-Web', [string]$OutputDirectory,
        [string[]]$SearchRoots, [int]$SearchSeconds = 30, [int]$MaximumDirectories = 5000)
    $ErrorActionPreference = 'Stop'
    $site = ConvertTo-RdDiagnosticPath $SitePath
    if (-not $site -or $site.Length -le 2 -or (Get-RdDiagnosticPathStatus $site) -ne 'DirectoryExists') { throw 'Expected an existing local application directory without reparse points.' }
    Write-Host ('Reading application configuration and DLL hashes: ' + $site)
    $local = Read-RdDiagnosticConfig $site
    $binaries = @()
    foreach ($name in @('ReportDesk.Core.dll', 'ReportDesk.Web.dll')) {
        $path = Join-Path $site ('bin\' + $name)
        $status = Get-RdDiagnosticPathStatus $path
        $hash = $null
        if ($status -eq 'FileExists') {
            try { $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash }
            catch { $status = Get-RdDiagnosticError $_ }
        }
        $binaries += [pscustomobject]@{ Name = $name; Path = $path; Status = $status; SHA256 = $hash }
    }
    Write-Host 'Reading IIS mapping and effective directory settings (no IIS changes).'
    $iis = Get-ReportDeskDiagnosticIis -SitePath $site
    $effective = @()
    if ($null -ne $iis.PSObject.Properties['EffectiveSettings']) { $effective = @($iis.EffectiveSettings) }
    elseif ($null -ne $iis.PSObject.Properties['Selected'] -and $null -ne $iis.Selected -and $null -ne $iis.Selected.PSObject.Properties['EffectiveSettings']) {
        $effective = @($iis.Selected.EffectiveSettings)
    }
    $roots = $SearchRoots
    if (-not $roots) { $roots = @(Get-RdDiagnosticRoots $site $local.Settings $effective) }
    Write-Host 'Searching candidate directories for connection.json/catalog.json names only.'
    $search = Find-RdDiagnosticConnectionFiles -Roots $roots -Seconds $SearchSeconds -MaximumDirectories $MaximumDirectories
    $report = [pscustomobject][ordered]@{
        Version = '20261106-diagnostic-v1'; CreatedUtc = [DateTime]::UtcNow.ToString('o'); ComputerName = $env:COMPUTERNAME;
        PowerShellVersion = $PSVersionTable.PSVersion.ToString(); Is64BitProcess = [Environment]::Is64BitProcess;
        SitePath = $site; LocalWebConfig = $local; Binaries = $binaries; IIS = $iis; ConnectionFileSearch = $search;
        ExpectedUpdateCoreSHA256 = '6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7';
        Limitations = @('Connection files are inspected by name/metadata only; their content is never opened. No SQL, query result, or process memory is read.',
            'Only allowlisted Web.config/IIS metadata is reported; database accounts, passwords and other configuration contents are not output.',
            'No HTTP request, database connection, configuration write, site restart, application-pool operation, or update is performed.',
            'IIS effective configuration is read from disk; it does not prove the values already cached by a running worker.',
            'Candidate discovery is best-effort and limited to named locations; profile and backup matches are not automatically the active Web configuration.',
            'No result means no match in inspected accessible directories, not proof that the server has no saved configuration.',
            'Scanning skips reparse points, depth > 8 and inaccessible paths; budgets apply between directory operations, so an individual OS read may take longer.')
    }
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ReportDesk-WebDiagnostic-' + [Guid]::NewGuid().ToString('N')) }
    $output = ConvertTo-RdDiagnosticPath $OutputDirectory
    if (-not $output -or (Get-RdDiagnosticPathStatus $output) -ne 'NotFound') { throw 'Diagnostic output must be a new local directory without reparse points.' }
    $canonicalOutput = Get-ReportDeskCanonicalDirectoryPath $output
    # Only this new report directory is written. Never write reports into application/data/HIS roots.
    foreach ($protected in @($site) + @($local.Settings | ForEach-Object { $_.Path }) + @($effective | ForEach-Object { if ($null -ne $_.PSObject.Properties['Path']) { $_.Path } elseif ($null -ne $_.PSObject.Properties['Value']) { $_.Value } })) {
        $safe = ConvertTo-RdDiagnosticPath $protected
        if ($safe -and $safe.Length -gt 2) {
            $canonicalProtected = Get-ReportDeskCanonicalDirectoryPath $safe
            if ($canonicalOutput.Equals($canonicalProtected, [StringComparison]::OrdinalIgnoreCase) -or $canonicalOutput.StartsWith($canonicalProtected + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Diagnostic output must be outside application/data/report directories.'
            }
        }
    }
    $null = [IO.Directory]::CreateDirectory($output)
    $jsonPath = Join-Path $output 'ReportDesk-diagnostic.json'
    $textPath = Join-Path $output 'ReportDesk-diagnostic.txt'
    $json = $report | ConvertTo-Json -Depth 14
    [IO.File]::WriteAllText($jsonPath, $json, (New-Object Text.UTF8Encoding($true)))
    [IO.File]::WriteAllText($textPath, $json, (New-Object Text.UTF8Encoding($true)))
    Write-Host ('Diagnostic report: ' + $textPath)
    Write-Host ('Candidate files found: ' + $search.Files.Count + '; search truncated: ' + $search.Truncated)
    Write-Host 'Diagnosis only; original website and configuration have not been changed.'
    return [pscustomobject]@{ ReportPath = $textPath; JsonPath = $jsonPath; Report = $report }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $result = Invoke-ReportDeskWebDiagnostic @PSBoundParameters
        # Notepad is intentionally opened for the operator to view the requested diagnostic result.
        & notepad.exe $result.ReportPath
        exit 0
    } catch {
        Write-Host ('Diagnostic stopped: ' + $_.Exception.GetType().Name + '. No automatic repair was attempted.') -ForegroundColor Red
        exit 1
    }
}
