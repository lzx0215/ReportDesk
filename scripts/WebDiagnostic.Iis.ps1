#requires -Version 5.1
# Read-only IIS diagnostics. No HTTP, database, service, pool or configuration writes.

function Get-ReportDeskDiagnosticIisFailure {
    param([Parameter(Mandatory=$true)][Exception]$Exception)
    $cause = $Exception.GetBaseException()
    $category = 'ReadFailed'
    if ($cause -is [UnauthorizedAccessException] -or $cause -is [Security.SecurityException]) { $category = 'AccessDenied' }
    elseif ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { $category = 'NotFound' }
    # Never emit exception messages: configuration errors can embed setting values.
    return [pscustomobject][ordered]@{
        Category = $category
        ExceptionType = $cause.GetType().FullName
        HResult = ('0x{0:X8}' -f $cause.HResult)
    }
}

function Get-ReportDeskDiagnosticIis {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$SitePath)
    $ErrorActionPreference = 'Stop'
    $result = [pscustomobject][ordered]@{
        Status = 'NotChecked'
        SitePath = $SitePath
        MatchingMethod = 'Case-insensitive absolute path text after environment-variable expansion; aliases are not resolved.'
        Matches = @()
        Selected = $null
        Failure = $null
        Note = 'IIS configuration observed now; this is not a dump of settings already loaded in a worker process.'
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $result.Status = 'UnsupportedPlatform'
        return $result
    }
    try {
        $expanded = [Environment]::ExpandEnvironmentVariables($SitePath)
        if ($expanded -notmatch '^[A-Za-z]:[\\/]' -or $expanded.Substring(2).Contains(':')) {
            $result.Status = 'InvalidLocalPath'
            return $result
        }
        $target = [IO.Path]::GetFullPath($expanded).TrimEnd('\', '/')
        if ($target.Length -le 2) { $result.Status = 'InvalidLocalPath'; return $result }
        $result.SitePath = $target
        $systemDirectory = 'System32'
        if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { $systemDirectory = 'Sysnative' }
        $assembly = Join-Path $env:WINDIR ($systemDirectory + '\inetsrv\Microsoft.Web.Administration.dll')
        if (-not [IO.File]::Exists($assembly)) {
            $result.Status = 'IisManagementAssemblyUnavailable'
            return $result
        }
        [void][Reflection.Assembly]::LoadFrom($assembly)
        $manager = $null
        try {
            $manager = New-Object Microsoft.Web.Administration.ServerManager
            $matchedApplications = @()
            foreach ($site in $manager.Sites) {
                foreach ($application in $site.Applications) {
                    foreach ($directory in $application.VirtualDirectories) {
                        if ([string]$directory.Path -ne '/') { continue }
                        $physical = [Environment]::ExpandEnvironmentVariables([string]$directory.PhysicalPath)
                        # An unrelated UNC/invalid mapping must not suppress diagnostics for this application.
                        if ($physical -notmatch '^[A-Za-z]:[\\/]') { continue }
                        try { $physical = [IO.Path]::GetFullPath($physical).TrimEnd('\', '/') } catch { continue }
                        if (-not [string]::Equals($physical, $target, [StringComparison]::OrdinalIgnoreCase)) { continue }
                        $bindingRows = @()
                        foreach ($binding in $site.Bindings) {
                            $bindingRows += [pscustomobject][ordered]@{
                                Protocol = [string]$binding.Protocol
                                BindingInformation = [string]$binding.BindingInformation
                            }
                        }
                        $matchedApplications += [pscustomobject][ordered]@{
                            SiteName = [string]$site.Name
                            SiteId = [long]$site.Id
                            ApplicationPath = [string]$application.Path
                            PhysicalPath = $physical
                            PoolName = [string]$application.ApplicationPoolName
                            Bindings = $bindingRows
                        }
                    }
                }
            }
            $result.Matches = @($matchedApplications)
            if ($matchedApplications.Count -eq 0) { $result.Status = 'NoExactApplicationRootMatch'; return $result }
            if ($matchedApplications.Count -ne 1) { $result.Status = 'MultipleApplicationRootMatches'; return $result }
            $selectedMatch = $matchedApplications[0]
            $selected = [pscustomobject][ordered]@{
                SiteName = $selectedMatch.SiteName
                ApplicationPath = $selectedMatch.ApplicationPath
                PhysicalPath = $selectedMatch.PhysicalPath
                PoolName = $selectedMatch.PoolName
                Bindings = $selectedMatch.Bindings
                PoolStatus = 'NotChecked'
                Pool = $null
                PoolFailure = $null
                WorkersStatus = 'NotChecked'
                Workers = @()
                WorkersFailure = $null
                EffectiveSettingsStatus = 'NotChecked'
                EffectiveSettingsSource = 'Microsoft.Web.Administration.GetWebConfiguration(site, applicationPath).GetSection(appSettings)'
                EffectiveSettings = @()
                EffectiveSettingsFailure = $null
            }
            $result.Selected = $selected
            try {
                $pool = $manager.ApplicationPools[$selectedMatch.PoolName]
                if ($null -eq $pool) { $selected.PoolStatus = 'PoolNotFound' }
                else {
                    $selected.Pool = [pscustomobject][ordered]@{
                        Name = [string]$pool.Name
                        IdentityType = [string]$pool.ProcessModel.IdentityType
                        UserName = [string]$pool.ProcessModel.UserName
                        LoadUserProfile = [bool]$pool.ProcessModel.LoadUserProfile
                        Enable32BitAppOnWin64 = [bool]$pool.Enable32BitAppOnWin64
                        ManagedRuntimeVersion = [string]$pool.ManagedRuntimeVersion
                        ManagedPipelineMode = [string]$pool.ManagedPipelineMode
                    }
                    $selected.PoolStatus = 'Read'
                }
            } catch {
                $selected.PoolStatus = 'Unavailable'
                $selected.PoolFailure = Get-ReportDeskDiagnosticIisFailure $_.Exception
            }
            try {
                $workers = @()
                foreach ($worker in $manager.WorkerProcesses) {
                    if ([string]$worker.AppPoolName -ne $selectedMatch.PoolName) { continue }
                    $workerId = [int]$worker.ProcessId
                    $startedUtc = $null
                    $startTimeStatus = 'Unavailable'
                    try {
                        $process = Get-Process -Id $workerId -ErrorAction Stop
                        try { $startedUtc = $process.StartTime.ToUniversalTime().ToString('o'); $startTimeStatus = 'Read' }
                        finally { $process.Dispose() }
                    } catch { }
                    $workers += [pscustomobject][ordered]@{
                        ProcessId = $workerId
                        StartTimeUtc = $startedUtc
                        StartTimeStatus = $startTimeStatus
                    }
                }
                $selected.Workers = $workers
                $selected.WorkersStatus = 'Read'
            } catch {
                $selected.WorkersStatus = 'Unavailable'
                $selected.WorkersFailure = Get-ReportDeskDiagnosticIisFailure $_.Exception
            }
            try {
                $configuration = $manager.GetWebConfiguration($selectedMatch.SiteName, $selectedMatch.ApplicationPath)
                $section = $configuration.GetSection('appSettings')
                $collection = $section.GetCollection()
                $settingsRows = @()
                foreach ($key in @('ReportDesk.DataDirectory', 'ReportDesk.ReportsDirectory')) {
                    $values = @()
                    foreach ($element in $collection) {
                        if ([string]$element.GetAttributeValue('key') -eq $key) {
                            # Only the two allowlisted directory values are ever read/emitted.
                            $values += [string]$element.GetAttributeValue('value')
                        }
                    }
                    $entryStatus = 'Missing'
                    $value = $null
                    if ($values.Count -eq 1) { $entryStatus = 'Present'; $value = $values[0] }
                    elseif ($values.Count -gt 1) { $entryStatus = 'Ambiguous' }
                    $settingsRows += [pscustomobject][ordered]@{ Key = $key; Status = $entryStatus; Value = $value }
                }
                $selected.EffectiveSettings = $settingsRows
                $selected.EffectiveSettingsStatus = 'Read'
            } catch {
                $selected.EffectiveSettingsStatus = 'Unavailable'
                $selected.EffectiveSettingsFailure = Get-ReportDeskDiagnosticIisFailure $_.Exception
            }
            $result.Status = 'Read'
            if ($selected.PoolStatus -ne 'Read' -or $selected.WorkersStatus -ne 'Read' -or $selected.EffectiveSettingsStatus -ne 'Read') {
                $result.Status = 'Partial'
            }
            return $result
        } finally { if ($null -ne $manager) { $manager.Dispose() } }
    } catch {
        $result.Status = 'Unavailable'
        $result.Failure = Get-ReportDeskDiagnosticIisFailure $_.Exception
        return $result
    }
}
