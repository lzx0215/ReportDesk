param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [ValidateSet('4.6.2','4.8')][string]$FrameworkVersion = '4.6.2',
    [string]$AssetsFile
)
$ErrorActionPreference = 'Stop'
if (-not $AssetsFile) { $AssetsFile = Join-Path $PSScriptRoot '../src/ReportDesk.Web/obj/project.assets.json' }
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run with Windows powershell.exe for reflection-only assembly inspection.' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
[xml]$config = Get-Content -LiteralPath (Join-Path $package 'Web.config') -Raw
foreach ($node in @($config.configuration.'system.web'.compilation, $config.configuration.'system.web'.httpRuntime)) {
    if ($node.targetFramework -ne $FrameworkVersion) { throw 'Web.config target mismatch.' }
}
if (-not $config.SelectSingleNode('/configuration/runtime/*[local-name()="assemblyBinding"]')) { throw 'Missing binding redirects.' }
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $path = Join-Path $package $entry.path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Hash mismatch: ' + $entry.path) }
}
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' })
if ($files.Count -ne @($manifest).Count) { throw 'Manifest file count mismatch.' }
foreach ($name in @('ReportDesk.Web.dll','ReportDesk.Core.dll','Oracle.ManagedDataAccess.dll','System.ValueTuple.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package ('bin/' + $name)))) { throw ('Missing dependency: ' + $name) }
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $package 'bin') -Filter '*.dll') {
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($file.FullName)
    $attribute = @([Reflection.CustomAttributeData]::GetCustomAttributes($assembly) | Where-Object { $_.Constructor.DeclaringType.FullName -eq 'System.Runtime.Versioning.TargetFrameworkAttribute' })
    if ($attribute.Count -eq 0 -and $file.Name -notin @('ReportDesk.Web.dll','ReportDesk.Core.dll')) {
        # Some vendor assemblies omit TargetFrameworkAttribute. Verify their exact bytes
        # against the framework-specific runtime asset selected by NuGet instead.
        $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
        $targetKey = '.NETFramework,Version=v' + $FrameworkVersion
        if (-not $assets.targets.$targetKey) { $targetKey = 'net' + $FrameworkVersion.Replace('.', '') }
        $candidates = @(foreach ($library in $assets.targets.$targetKey.PSObject.Properties) {
            foreach ($asset in $library.Value.runtime.PSObject.Properties.Name) {
                if ($asset -and [IO.Path]::GetFileName($asset) -eq $file.Name) { @{ key = $library.Name; asset = $asset } }
            }
        })
        if ($candidates.Count -ne 1 -or $candidates[0].asset -notmatch '^lib/net(45|451|452|46|461|462)/') { throw ('Unverified framework runtime asset: ' + $file.Name) }
        $key = $candidates[0].key
        $runtimeAsset = $candidates[0].asset
        $verified = $false
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $source = Join-Path (Join-Path $folder $assets.libraries.$key.path) $runtimeAsset
            if ((Test-Path -LiteralPath $source) -and (Get-FileHash -LiteralPath $source).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { $verified = $true }
        }
        if (-not $verified) { throw ('Runtime asset hash mismatch: ' + $file.Name) }
        Write-Output ($file.Name + ': verified NuGet runtime asset hash ' + $runtimeAsset)
        continue
    }
    if ($attribute.Count -ne 1) { throw ('Missing target metadata: ' + $file.Name) }
    $target = [string]$attribute[0].ConstructorArguments[0].Value
    if ($target -notmatch '^\.NETFramework,Version=v([0-9.]+)$') { throw ('Unexpected target: ' + $file.Name + ' ' + $target) }
    if ([version]$Matches[1] -gt [version]$FrameworkVersion) { throw ('Incompatible target: ' + $file.Name + ' ' + $target) }
    if ($file.Name -in @('ReportDesk.Web.dll','ReportDesk.Core.dll') -and $target -ne ('.NETFramework,Version=v' + $FrameworkVersion)) { throw 'Application assembly target mismatch.' }
    Write-Output ($file.Name + ': ' + $target)
}
Write-Output ('PASS package: ' + $files.Count + ' hashes; configuration and assembly target metadata. Not a target-server runtime test.')
