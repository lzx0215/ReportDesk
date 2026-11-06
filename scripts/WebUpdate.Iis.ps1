# Read-only IIS inventory and bounded HTTP checks for Apply-WebUpdate.ps1.
# Compatible with Windows PowerShell 5.1. This file never changes IIS settings.

function ConvertTo-ReportDeskLocalPath {
    param([Parameter(Mandatory=$true)][string]$Path)
    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ($expanded -notmatch '^[A-Za-z]:[\\/]' -or $expanded.Substring(2).Contains(':')) {
        throw 'Only absolute local drive paths are supported for automatic IIS update.'
    }
    $full = [IO.Path]::GetFullPath($expanded).TrimEnd([char[]]@('\', '/'))
    if ($full.Length -le 2) { throw 'A drive root cannot be used as an application directory.' }
    return $full
}

function Assert-ReportDeskNoPathLinks {
    param([Parameter(Mandatory=$true)][string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A physical mapping contains a junction or symbolic link: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Get-ReportDeskCanonicalDirectoryPath {
    # Directory handles resolve 8.3 names and SUBST drives to a volume GUID path.
    # GetFullPath alone cannot detect those aliases.
    param([Parameter(Mandatory=$true)][string]$Path)
    if ($null -eq ('ReportDesk.WebUpdate.NativePaths' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace ReportDesk.WebUpdate {
    public static class NativePaths {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path,
            uint length, uint flags);
        public static string ResolveDirectory(string path) {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (length >= buffer.Capacity) throw new InvalidOperationException("Physical path is too long.");
                return buffer.ToString();
            }
        }
    }
}
'@ -ErrorAction Stop
    }
    $current = [IO.Path]::GetFullPath($Path)
    $tail = @()
    while (-not (Test-Path -LiteralPath $current -PathType Container)) {
        if (Test-Path -LiteralPath $current) { throw 'An IIS directory mapping points to a file.' }
        $tail = @([IO.Path]::GetFileName($current)) + $tail
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { throw 'An IIS physical mapping has no accessible local volume.' }
        $current = $parent.FullName
    }
    $canonical = [ReportDesk.WebUpdate.NativePaths]::ResolveDirectory($current).TrimEnd('\')
    if ($canonical -notmatch '^\\\\\?\\Volume\{[A-Fa-f0-9-]+\}\\?') {
        throw 'The IIS physical mapping does not resolve to a local volume.'
    }
    if ($tail.Count -gt 0) { $canonical += '\' + ($tail -join '\') }
    return $canonical
}

function Test-ReportDeskIisInventory {
    # Pure path/mapping check, also used by isolated tests without IIS.
    param(
        [Parameter(Mandatory=$true)][string]$SitePath,
        [Parameter(Mandatory=$true)][object[]]$Mappings,
        [string]$CanonicalSitePath
    )
    $target = ConvertTo-ReportDeskLocalPath $SitePath
    $matches = @($Mappings | Where-Object {
        $_.VirtualPath -eq '/' -and (
            [string]::Equals([string]$_.PhysicalPath, $target, [StringComparison]::OrdinalIgnoreCase) -or
            ($CanonicalSitePath -and $null -ne $_.PSObject.Properties['CanonicalPath'] -and
             [string]::Equals([string]$_.CanonicalPath, $CanonicalSitePath, [StringComparison]::OrdinalIgnoreCase)))
    })
    if ($matches.Count -ne 1) {
        throw 'The directory must match exactly one IIS application root virtual directory.'
    }
    $selected = $matches[0]
    $comparisonTarget = $target
    if ($CanonicalSitePath) { $comparisonTarget = $CanonicalSitePath }
    foreach ($mapping in $Mappings) {
        if ([object]::ReferenceEquals($mapping, $selected)) { continue }
        $other = [string]$mapping.PhysicalPath
        if ($other.StartsWith('\\')) {
            throw 'An IIS UNC mapping may alias a local directory. Automatic isolation verification is unavailable.'
        }
        if ($CanonicalSitePath) {
            if ($null -eq $mapping.PSObject.Properties['CanonicalPath']) { throw 'An IIS mapping lacks its canonical physical path.' }
            $other = [string]$mapping.CanonicalPath
        }
        if ([string]::Equals($other, $comparisonTarget, [StringComparison]::OrdinalIgnoreCase) -or
            $other.StartsWith($comparisonTarget + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $comparisonTarget.StartsWith($other.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Another IIS application or virtual directory overlaps this physical directory. Automatic update is refused.'
        }
    }
    return $selected
}

function Get-ReportDeskTextHash {
    param([Parameter(Mandatory=$true)][string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '')
    } finally { $sha.Dispose() }
}

function Get-ReportDeskBindingUrls {
    param(
        [Parameter(Mandatory=$true)][object[]]$Bindings,
        [Parameter(Mandatory=$true)][string]$AppPath,
        [string[]]$LocalIPv4Addresses
    )
    $urls = @()
    $haveAddresses = $PSBoundParameters.ContainsKey('LocalIPv4Addresses')
    foreach ($binding in $Bindings) {
        if ($binding.Protocol -notin @('http', 'https')) { continue }
        $info = [string]$binding.BindingInformation
        if ($info -notmatch '^(.*):(\d+):([^:]*)$') { continue }
        $ip = $Matches[1].Trim('[', ']')
        $port = [int]$Matches[2]
        $hostName = $Matches[3]
        $hostNames = @()
        if (-not $hostName) {
            if ($ip -and $ip -notin @('*', '0.0.0.0', '::')) { $hostName = $ip }
            elseif ($binding.Protocol -eq 'http') { $hostName = 'localhost' }
            else {
                if (-not $haveAddresses) {
                    $LocalIPv4Addresses = @(
                        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
                            if ($adapter.OperationalStatus -ne [Net.NetworkInformation.OperationalStatus]::Up) { continue }
                            foreach ($address in $adapter.GetIPProperties().UnicastAddresses) {
                                if ($address.Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
                                    -not [Net.IPAddress]::IsLoopback($address.Address)) { $address.Address.ToString() }
                            }
                        }
                    )
                    $haveAddresses = $true
                }
                # Merely candidates: every address still requires strict certificate + app checks.
                $hostNames = @($LocalIPv4Addresses | Sort-Object -Unique | Select-Object -First 16)
            }
        }
        if ($hostName) { $hostNames = @($hostName) }
        foreach ($candidateHost in $hostNames) {
            $builder = New-Object UriBuilder
            $builder.Scheme = [string]$binding.Protocol
            $builder.Host = $candidateHost
            $builder.Port = $port
            $builder.Path = $AppPath.TrimEnd('/') + '/'
            $urls += $builder.Uri.AbsoluteUri
        }
    }
    return @($urls | Sort-Object -Unique | Sort-Object @{Expression={ if ($_.StartsWith('https:')) { 0 } else { 1 } }}, @{Expression={ $_ }})
}

function Get-ReportDeskIisContext {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$SitePath)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'IIS inspection requires Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run Windows PowerShell as Administrator to inspect IIS safely.'
        }
    } finally { $identity.Dispose() }
    $target = ConvertTo-ReportDeskLocalPath $SitePath
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'The IIS application directory does not exist.' }
    Assert-ReportDeskNoPathLinks $target
    $canonicalTarget = Get-ReportDeskCanonicalDirectoryPath $target
    $systemFolder = 'System32'
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { $systemFolder = 'Sysnative' }
    $assembly = Join-Path $env:WINDIR "$systemFolder\inetsrv\Microsoft.Web.Administration.dll"
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
        throw 'IIS management scripting tools (Microsoft.Web.Administration) are not installed.'
    }
    [void][Reflection.Assembly]::LoadFrom($assembly)
    $manager = New-Object Microsoft.Web.Administration.ServerManager
    try {
        $mappings = @()
        $sites = @()
        $pools = @()
        foreach ($site in $manager.Sites) {
            $bindings = @($site.Bindings | ForEach-Object {
                $certificateHash = ''
                $certificateStore = ''
                if ($_.Protocol -eq 'https') {
                    if ($null -ne $_.CertificateHash) { $certificateHash = [BitConverter]::ToString($_.CertificateHash).Replace('-', '') }
                    $certificateStore = [string]$_.CertificateStoreName
                }
                [pscustomobject][ordered]@{
                    Protocol = [string]$_.Protocol
                    BindingInformation = [string]$_.BindingInformation
                    SslFlags = [string]$_.GetAttributeValue('sslFlags')
                    CertificateHash = $certificateHash
                    CertificateStoreName = $certificateStore
                }
            } | Sort-Object Protocol, BindingInformation)
            $sites += [pscustomobject][ordered]@{ Name = [string]$site.Name; Id = [long]$site.Id; Bindings = $bindings }
            foreach ($app in $site.Applications) {
                foreach ($vdir in $app.VirtualDirectories) {
                    $physical = [Environment]::ExpandEnvironmentVariables([string]$vdir.PhysicalPath)
                    if ($physical -match '^[A-Za-z]:[\\/]') {
                        $physical = ConvertTo-ReportDeskLocalPath $physical
                        Assert-ReportDeskNoPathLinks $physical
                    } elseif ($physical.StartsWith('\\')) {
                        # A local share/DFS alias cannot be proved disjoint by lexical comparison.
                        throw 'An IIS UNC mapping may alias a local directory. Automatic isolation verification is unavailable.'
                    } else { throw 'An IIS physical mapping cannot be resolved to an absolute path.' }
                    $mappings += [pscustomobject][ordered]@{
                        SiteName = [string]$site.Name
                        AppPath = [string]$app.Path
                        PoolName = [string]$app.ApplicationPoolName
                        EnabledProtocols = [string]$app.EnabledProtocols
                        VirtualPath = [string]$vdir.Path
                        PhysicalPath = $physical
                        CanonicalPath = Get-ReportDeskCanonicalDirectoryPath $physical
                        ConnectAsUserName = [string]$vdir.UserName
                    }
                }
            }
        }
        foreach ($pool in $manager.ApplicationPools) {
            $pools += [pscustomobject][ordered]@{
                Name = [string]$pool.Name
                ManagedRuntimeVersion = [string]$pool.ManagedRuntimeVersion
                ManagedPipelineMode = [string]$pool.ManagedPipelineMode
                Enable32BitAppOnWin64 = [bool]$pool.Enable32BitAppOnWin64
                AutoStart = [bool]$pool.AutoStart
                StartMode = [string]$pool.GetAttributeValue('startMode')
                IdentityType = [string]$pool.ProcessModel.IdentityType
                UserName = [string]$pool.ProcessModel.UserName
                LoadUserProfile = [bool]$pool.ProcessModel.LoadUserProfile
                MaxProcesses = [long]$pool.ProcessModel.MaxProcesses
                IdleTimeout = [string]$pool.ProcessModel.IdleTimeout
                PingingEnabled = [bool]$pool.ProcessModel.PingingEnabled
                ShutdownTimeLimit = [string]$pool.ProcessModel.ShutdownTimeLimit
            }
        }
        $mappings = @($mappings | Sort-Object SiteName, AppPath, VirtualPath)
        $sites = @($sites | Sort-Object Name)
        $pools = @($pools | Sort-Object Name)
        $selected = Test-ReportDeskIisInventory -SitePath $target -Mappings $mappings -CanonicalSitePath $canonicalTarget
        $selectedSite = @($sites | Where-Object { $_.Name -eq $selected.SiteName })[0]
        $selectedPools = @($pools | Where-Object { $_.Name -eq $selected.PoolName })
        if ($selectedPools.Count -ne 1) { throw 'The selected IIS application pool could not be resolved.' }
        $snapshot = [ordered]@{ Sites = $sites; Mappings = $mappings; Pools = $pools }
        $snapshotHash = Get-ReportDeskTextHash ($snapshot | ConvertTo-Json -Depth 12 -Compress)
        return [pscustomobject][ordered]@{
            SitePath = $target
            SiteName = $selected.SiteName
            AppPath = $selected.AppPath
            PoolName = $selected.PoolName
            PoolSettings = $selectedPools[0]
            Bindings = $selectedSite.Bindings
            Sites = $sites
            SnapshotHash = $snapshotHash
            CandidateBaseUrls = @(Get-ReportDeskBindingUrls -Bindings $selectedSite.Bindings -AppPath $selected.AppPath)
            SharedPoolApplications = @($mappings | Where-Object { $_.VirtualPath -eq '/' -and $_.PoolName -eq $selected.PoolName } | Select-Object SiteName, AppPath)
            Mappings = $mappings
        }
    } finally { $manager.Dispose() }
}

function ConvertTo-ReportDeskBaseUrl {
    param([Parameter(Mandatory=$true)][string]$BaseUrl)
    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'HealthUrl must be an absolute HTTP(S) application root URL without credentials, query, or fragment.'
    }
    return $uri.AbsoluteUri.TrimEnd('/') + '/'
}

function Assert-ReportDeskHealthBinding {
    param([Parameter(Mandatory=$true)][string]$BaseUrl, [Parameter(Mandatory=$true)]$Context)
    $normalized = ConvertTo-ReportDeskBaseUrl $BaseUrl
    $uri = [Uri]$normalized
    if ([Uri]::UnescapeDataString($uri.AbsolutePath) -cne ($Context.AppPath.TrimEnd('/') + '/')) {
        throw 'HealthUrl path does not match the selected IIS application path.'
    }
    $matched = $false
    foreach ($binding in $Context.Bindings) {
        if ($binding.Protocol -ne $uri.Scheme -or [string]$binding.BindingInformation -notmatch '^(.*):(\d+):([^:]*)$') { continue }
        $ip = $Matches[1].Trim('[', ']')
        $port = [int]$Matches[2]
        $hostName = $Matches[3]
        if ($port -ne $uri.Port) { continue }
        if ($hostName) {
            if ([string]::Equals($hostName, $uri.DnsSafeHost, [StringComparison]::OrdinalIgnoreCase)) { $matched = $true }
        } elseif ($ip -in @('', '*', '0.0.0.0', '::')) {
            $matched = $true
        } elseif ([string]::Equals($ip, $uri.DnsSafeHost.Trim('[', ']'), [StringComparison]::OrdinalIgnoreCase)) {
            $matched = $true
        }
    }
    if (-not $matched) { throw 'HealthUrl does not match the selected IIS site bindings.' }
    # DNS must resolve only to this server (or loopback); do not probe another server by accident.
    $local = @([Net.IPAddress]::Loopback.ToString(), [Net.IPAddress]::IPv6Loopback.ToString())
    foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        foreach ($address in $adapter.GetIPProperties().UnicastAddresses) { $local += $address.Address.ToString() }
    }
    $resolved = @([Net.Dns]::GetHostAddresses($uri.DnsSafeHost))
    if ($resolved.Count -eq 0) { throw 'HealthUrl host has no address.' }
    foreach ($address in $resolved) {
        if (-not [Net.IPAddress]::IsLoopback($address) -and $local -notcontains $address.ToString()) {
            throw 'HealthUrl must resolve only to addresses on this IIS server.'
        }
    }
    if ($null -ne $Context.PSObject.Properties['Sites']) {
        foreach ($site in $Context.Sites) {
            if ($site.Name -eq $Context.SiteName) { continue }
            foreach ($binding in $site.Bindings) {
                if ($binding.Protocol -ne $uri.Scheme -or [string]$binding.BindingInformation -notmatch '^(.*):(\d+):([^:]*)$') { continue }
                $ip = $Matches[1].Trim('[', ']')
                $port = [int]$Matches[2]
                $hostName = $Matches[3]
                if ($port -ne $uri.Port) { continue }
                $addressMatches = $ip -in @('', '*', '0.0.0.0', '::') -or @($resolved | Where-Object { $_.ToString() -eq $ip }).Count -gt 0
                if ($addressMatches -and ($hostName -eq '' -or [string]::Equals($hostName, $uri.DnsSafeHost, [StringComparison]::OrdinalIgnoreCase))) {
                    throw 'HealthUrl also matches another IIS site binding. Use an unambiguous binding for the selected site.'
                }
            }
        }
    }
    return $normalized
}

function Invoke-ReportDeskHttp {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [ValidateRange(1, 60000)][int]$TimeoutMilliseconds = 10000,
        [ValidateRange(1, 16777216)][int]$MaximumBytes = 2097152
    )
    $uri = [Uri]$Url
    if ($uri.Scheme -notin @('http', 'https') -or $uri.UserInfo) { throw 'Invalid HTTP check URL.' }
    if ($null -ne [Net.ServicePointManager]::ServerCertificateValidationCallback) {
        throw 'A global TLS certificate callback is active. Use a clean PowerShell process for strict HTTPS validation.'
    }
    $request = [Net.HttpWebRequest]::Create($uri)
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $false
    $request.UseDefaultCredentials = $false
    $request.Credentials = $null
    $request.Proxy = $null
    $request.Timeout = $TimeoutMilliseconds
    $request.ReadWriteTimeout = $TimeoutMilliseconds
    $request.UserAgent = 'ReportDesk-WebUpdate-Health/1'
    $request.CachePolicy = New-Object Net.Cache.RequestCachePolicy([Net.Cache.RequestCacheLevel]::NoCacheNoStore)
    $request.Headers['Pragma'] = 'no-cache'
    $response = $null
    $stream = $null
    $memory = New-Object IO.MemoryStream
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $oldProtocols = [Net.ServicePointManager]::SecurityProtocol
    try {
        # Windows PowerShell/.NET 4.6 may otherwise offer only legacy TLS defaults.
        [Net.ServicePointManager]::SecurityProtocol = $oldProtocols -bor [Net.SecurityProtocolType]::Tls12
        try { $response = [Net.HttpWebResponse]$request.GetResponse() }
        catch [Net.WebException] {
            if ($null -eq $_.Exception.Response) { throw 'HTTP check failed (connection, timeout, or certificate validation). No TLS bypass was attempted.' }
            $response = [Net.HttpWebResponse]$_.Exception.Response
        }
        if ($response.ContentLength -gt $MaximumBytes) { throw 'HTTP check response exceeds the size limit.' }
        $stream = $response.GetResponseStream()
        $buffer = New-Object byte[] 8192
        while ($true) {
            $remaining = $TimeoutMilliseconds - $clock.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'HTTP check exceeded its response read deadline.' }
            if ($stream.CanTimeout) { $stream.ReadTimeout = [int]$remaining }
            $count = $stream.Read($buffer, 0, $buffer.Length)
            if ($count -eq 0) { break }
            if ($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw 'HTTP check exceeded its response read deadline.' }
            if ($memory.Length + $count -gt $MaximumBytes) { throw 'HTTP check response exceeds the size limit.' }
            $memory.Write($buffer, 0, $count)
        }
        $bytes = $memory.ToArray()
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            ContentType = [string]$response.ContentType
            Body = [Text.Encoding]::UTF8.GetString($bytes)
            Bytes = $bytes
        }
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $memory.Dispose()
        $request.Abort()
        [Net.ServicePointManager]::SecurityProtocol = $oldProtocols
    }
}

function Test-ReportDeskHealth {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BaseUrl)
    $normalized = ConvertTo-ReportDeskBaseUrl $BaseUrl
    $page = Invoke-ReportDeskHttp -Url $normalized
    if ($page.StatusCode -ne 200 -or $page.ContentType -notmatch '^text/html(?:;|$)' -or
        $page.Body -notmatch '(?is)<title>[^<]*ReportDesk[^<]*</title>' -or
        $page.Body -notmatch 'assets/(?:web|transport)\.js') {
        throw 'ReportDesk home page health check failed (redirects are not followed).'
    }
    $session = Invoke-ReportDeskHttp -Url ($normalized + 'api/session')
    if ($session.StatusCode -ne 200 -or $session.ContentType -notmatch '^application/json(?:;|$)') {
        throw 'ReportDesk session endpoint health check failed.'
    }
    try { $data = $session.Body | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'ReportDesk session endpoint returned invalid JSON.' }
    if ($null -eq $data -or $null -eq $data.PSObject.Properties['csrf'] -or
        $null -eq $data.PSObject.Properties['admin'] -or $null -eq $data.PSObject.Properties['offline'] -or
        $data.csrf -isnot [string] -or [string]::IsNullOrWhiteSpace($data.csrf) -or
        $data.admin -isnot [bool] -or $data.offline -isnot [bool]) {
        throw 'ReportDesk session endpoint schema does not match the expected application.'
    }
    return [pscustomobject]@{ BaseUrl = $normalized; Healthy = $true; HomeStatus = 200; SessionStatus = 200 }
}

function Test-ReportDeskOffline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$BaseUrl,
        [Parameter(Mandatory=$true)][string]$Marker,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 30,
        [ValidateRange(10, 5000)][int]$PollMilliseconds = 500,
        [ValidateRange(2, 10)][int]$RequiredConsecutivePasses = 2,
        [scriptblock]$AssertMaintenance
    )
    $normalized = ConvertTo-ReportDeskBaseUrl $BaseUrl
    if ($Marker -notmatch '^[A-Za-z0-9_-]{16,128}$') { throw 'Offline marker must be a unique 16-128 character alphanumeric token.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $deadline = $TimeoutSeconds * 1000
    $history = New-Object 'System.Collections.Generic.Queue[object]'
    $evidence = [pscustomobject][ordered]@{
        Phase = 'Waiting'; Attempts = 0; ElapsedMilliseconds = 0
        RequiredConsecutivePasses = $RequiredConsecutivePasses; ConsecutivePasses = 0
        LastResponses = @(); RecentRounds = @()
    }
    while ($clock.ElapsedMilliseconds -lt $deadline) {
        if ($AssertMaintenance) {
            try { $null = & $AssertMaintenance }
            catch { $evidence.Phase = 'MaintenanceOwnershipFailed'; break }
        }
        $evidence.Attempts++
        $round = @()
        foreach ($route in @('', 'api/session')) {
            $remaining = $deadline - $clock.ElapsedMilliseconds
            $observation = [pscustomobject][ordered]@{
                Route = ('/' + $route); StatusCode = $null; MarkerMatched = $false
                ContentKind = 'unknown'; ResponseBytes = 0; Outcome = 'DeadlineReached'
            }
            if ($remaining -gt 0) {
                try {
                    # Never put the maintenance marker in the URL: an error page
                    # echoing the request must not satisfy the ownership check.
                    $probeUrl = $normalized + $route + '?reportdesk_update_probe=' + [Guid]::NewGuid().ToString('N')
                    $response = Invoke-ReportDeskHttp -Url $probeUrl -TimeoutMilliseconds ([int][Math]::Min(5000, $remaining))
                    $observation.StatusCode = [int]$response.StatusCode
                    $observation.MarkerMatched = ([string]$response.Body).Contains($Marker)
                    $observation.ResponseBytes = $response.Bytes.Length
                    # Header values and bodies may contain sensitive data. Keep
                    # only a fixed MIME classification, status and marker match.
                    if ($response.ContentType -match '^text/html(?:;|$)') { $observation.ContentKind = 'html' }
                    elseif ($response.ContentType -match '^application/json(?:;|$)') { $observation.ContentKind = 'json' }
                    else { $observation.ContentKind = 'other' }
                    if ($observation.StatusCode -eq 503 -and $observation.MarkerMatched) { $observation.Outcome = 'Matched' }
                    else { $observation.Outcome = 'ResponseMismatch' }
                } catch { $observation.Outcome = 'HttpCheckFailed' }
            }
            $round += $observation
        }
        $evidence.LastResponses = $round
        $history.Enqueue([pscustomobject]@{ Attempt = $evidence.Attempts; Responses = $round })
        while ($history.Count -gt 12) { $null = $history.Dequeue() }
        $evidence.RecentRounds = @($history.ToArray())
        if ($AssertMaintenance) {
            try { $null = & $AssertMaintenance }
            catch { $evidence.Phase = 'MaintenanceOwnershipFailed'; break }
        }
        if (@($round | Where-Object { $_.Outcome -ne 'Matched' }).Count -eq 0) { $evidence.ConsecutivePasses++ }
        else { $evidence.ConsecutivePasses = 0 }
        if ($evidence.ConsecutivePasses -ge $RequiredConsecutivePasses -and $clock.ElapsedMilliseconds -lt $deadline) {
            $evidence.Phase = 'Verified'
            $evidence.ElapsedMilliseconds = $clock.ElapsedMilliseconds
            return [pscustomobject]@{ BaseUrl = $normalized; Offline = $true; Evidence = $evidence }
        }
        $remaining = $deadline - $clock.ElapsedMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Min($PollMilliseconds, $remaining)) }
    }
    if ($evidence.Phase -eq 'Waiting') { $evidence.Phase = 'TimedOut' }
    $evidence.ElapsedMilliseconds = $clock.ElapsedMilliseconds
    $summary = @($evidence.LastResponses | ForEach-Object {
        $status = if ($null -eq $_.StatusCode) { 'none' } else { [string]$_.StatusCode }
        $_.Route + ': HTTP=' + $status + ', marker=' + $_.MarkerMatched + ', result=' + $_.Outcome
    }) -join '; '
    $failure = New-Object InvalidOperationException ('Offline verification failed (' + $evidence.Phase + '). Both routes must return HTTP 503 with this operation marker for ' + $RequiredConsecutivePasses + ' consecutive rounds. ' + $summary)
    $failure.Data['ReportDeskOfflineEvidence'] = $evidence | ConvertTo-Json -Depth 8 -Compress
    throw $failure
}
