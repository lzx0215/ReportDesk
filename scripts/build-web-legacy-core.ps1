param(
    [Parameter(Mandatory=$true)][string]$BaselineBin,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$baselinePath = (Resolve-Path -LiteralPath $BaselineBin).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Output must be a new directory.' }
$corePath = Join-Path $baselinePath 'ReportDesk.Core.dll'
if ((Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash -ne '6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7') {
    throw 'Legacy Core baseline mismatch. No build was started.'
}
$intermediate = Join-Path $projectRoot ('artifacts\legacy-core-build\' + [Guid]::NewGuid().ToString('N') + '\obj\')
Push-Location $projectRoot
try {
    & dotnet build 'src\ReportDesk.Web\ReportDesk.Web.csproj' -c Release -p:ReportDeskWebTargetFramework=net462 "-p:ReportDeskLegacyCoreDirectory=$baselinePath" "-p:BaseIntermediateOutputPath=$intermediate" -o $destination --force --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Legacy Core Web build failed.' }
    foreach ($dependency in Get-ChildItem -LiteralPath $baselinePath -Filter '*.dll' -File | Where-Object { $_.Name -ne 'ReportDesk.Web.dll' }) {
        $compiledDependency = Join-Path $destination $dependency.Name
        if ((Get-FileHash -LiteralPath $compiledDependency -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $dependency.FullName -Algorithm SHA256).Hash) {
            throw ('Dependency changed unexpectedly: ' + $dependency.Name)
        }
    }
    Write-Output 'PASS: built Web hotfix against the exact deployed legacy Core; all baseline dependencies unchanged.'
    Write-Output 'Only ReportDesk.Web.dll is a deployment payload. This command did not change IIS or any server file.'
} finally { Pop-Location }
