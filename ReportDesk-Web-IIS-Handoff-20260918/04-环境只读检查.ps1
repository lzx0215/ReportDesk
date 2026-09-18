# Read-only inventory. Does not install, restart, connect to Oracle, or read secrets.
# Run on the TARGET SERVER in Windows PowerShell. Emits JSON to standard output.
# Optional local save: .\04-环境只读检查.ps1 | Out-File .\server-environment.json -Encoding utf8
# NOT RUN on the hospital server when this handoff was prepared.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$reportDeskInventory = [ordered]@{
    CollectedAt = (Get-Date).ToString('o')
    Scope = 'Read-only target-server inventory; no changes'
}

try {
    $reportDeskOs = Get-CimInstance Win32_OperatingSystem
    $reportDeskInventory.OS = [ordered]@{
        Caption = $reportDeskOs.Caption
        Version = $reportDeskOs.Version
        BuildNumber = $reportDeskOs.BuildNumber
        Architecture = $reportDeskOs.OSArchitecture
        LastBootUpTime = $reportDeskOs.LastBootUpTime
    }
} catch { $reportDeskInventory.OS = 'Unavailable; inspect locally with winver.' }

try {
    $reportDeskComputer = Get-CimInstance Win32_ComputerSystem
    $reportDeskInventory.Hardware = [ordered]@{
        LogicalProcessors = $reportDeskComputer.NumberOfLogicalProcessors
        MemoryGB = [math]::Round($reportDeskComputer.TotalPhysicalMemory / 1GB, 1)
    }
    $reportDeskInventory.Disks = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
        Select-Object DeviceID,
            @{Name='SizeGB';Expression={[math]::Round($_.Size / 1GB, 1)}},
            @{Name='FreeGB';Expression={[math]::Round($_.FreeSpace / 1GB, 1)}})
} catch { $reportDeskInventory.HardwareError = 'Some hardware fields unavailable.' }

try {
    $reportDeskIis = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\InetStp'
    $reportDeskInventory.IIS = [ordered]@{
        Version = $reportDeskIis.VersionString
        MajorVersion = $reportDeskIis.MajorVersion
        MinorVersion = $reportDeskIis.MinorVersion
    }
} catch { $reportDeskInventory.IIS = 'IIS registry information unavailable.' }

try {
    $reportDeskDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $reportDeskDotnet) {
        $reportDeskInventory.DotnetRuntimes = @(& $reportDeskDotnet.Source --list-runtimes 2>&1 |
            ForEach-Object { $_.ToString() })
    } else { $reportDeskInventory.DotnetRuntimes = @('dotnet not found on PATH') }
} catch { $reportDeskInventory.DotnetRuntimes = @('Runtime inventory unavailable') }

try {
    $reportDeskModule = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    if (Test-Path -LiteralPath $reportDeskModule) {
        $reportDeskInventory.AspNetCoreModuleVersion = (Get-Item -LiteralPath $reportDeskModule).VersionInfo.FileVersion
    } else { $reportDeskInventory.AspNetCoreModuleVersion = 'Not found at standard location' }
} catch { $reportDeskInventory.AspNetCoreModuleVersion = 'Unavailable' }

try {
    Import-Module WebAdministration -ErrorAction Stop
    $reportDeskInventory.Sites = @(Get-Website | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            State = $_.State.ToString()
            ApplicationPool = $_.ApplicationPool
            Bindings = @($_.Bindings.Collection | ForEach-Object {
                [pscustomobject]@{Protocol=$_.protocol; Binding=$_.bindingInformation}
            })
        }
    })
} catch { $reportDeskInventory.Sites = @('Unavailable; read IIS Manager locally. No change attempted.') }

try {
    $reportDeskInventory.RecentHotFixes = @(Get-HotFix |
        Sort-Object InstalledOn -Descending | Select-Object -First 8 HotFixID, InstalledOn)
} catch { $reportDeskInventory.RecentHotFixes = @('Unavailable') }

$reportDeskInventory.Notes = @(
    'Hotfix list alone does not prove that all required updates are installed.',
    'CPU and RAM totals do not establish spare capacity or 50-user query throughput.',
    'Site names and bindings are internal information; review before sharing.'
)
$reportDeskInventory | ConvertTo-Json -Depth 7
