param([Parameter(Mandatory=$true)][string]$SourceDirectory, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[Reflection.Assembly]::LoadFrom((Join-Path $repo 'artifacts/host/ReportDesk.Core.dll')) | Out-Null
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$scan = [ReportDesk.Core.ReportImporter]::ImportFolder($SourceDirectory, [Threading.CancellationToken]::None, $null)
$locations = [ReportDesk.Core.ReportLocations]::Load((Join-Path $repo 'report-locations.xml'))
$all = @($scan.Reports) + @($scan.IncompleteReports)
$messages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($r in $all) { foreach ($message in $r.Issues) { [void]$messages.Add($message) } }
$rows = @($all | ForEach-Object {
    $r = $_
    $advice = @($r.Issues | ForEach-Object { [ReportDesk.Core.AdaptationGuidance]::For($_) } | Sort-Object Code -Unique)
    [pscustomobject]@{ id=$r.Id; name=$r.Name; path=$r.SourcePath; hash=$r.SourceHash; classification=$(if ([ReportDesk.Core.ReportClassification]::IsStandalone($r)) {'Report'} else {'Incomplete'}); status=$r.Status; pending=($r.Issues.Count -gt 0); issues=@($r.Issues); sources=@($r.Queries | ForEach-Object { [pscustomobject]@{name=$_.Name;issues=@([ReportDesk.Core.ReportReadiness]::IssuesFor($r,$_))} }); codes=@($advice.Code); guidance=$advice; locations=@($locations.For($r)) }
})
$families = @($rows.guidance | Group-Object Code | ForEach-Object {
    $code=$_.Name; $matching=@($rows | Where-Object {$_.codes -contains $code}); $g=$_.Group[0]
    [pscustomobject]@{ code=$code; title=$g.Title; reports=$matching.Count; visibleReports=@($matching | Where-Object {$_.classification -eq 'Report'}).Count; action=$g.Action; examples=@($matching | Select-Object -First 3 -ExpandProperty name) }
} | Sort-Object reports -Descending)
$excluded = @($scan.Inventory.Layouts | ForEach-Object { [pscustomobject]@{path=$_;kind='Layout';reason='打印版式，作为关联文件保留'} }) + @($scan.Inventory.OtherFiles | ForEach-Object { [pscustomobject]@{path=$_;kind='OtherXml';reason='根节点不是受支持的报表查询定义；作为其他配置排除，不能据此认定文件无用'} }) + @($scan.IncompleteReports | ForEach-Object { [pscustomobject]@{path=$_.SourcePath;kind='Incomplete';reason='没有报表查询 SQL，可能依赖 HIS 窗口提供数据'} })
$audit = [ordered]@{ source=(Resolve-Path $SourceDirectory).Path; generated=(Get-Date -Format s); scanned=$scan.Inventory.ScannedXml; queryDefinitions=$all.Count; reports=$scan.Reports.Count; ready=@($scan.Reports | Where-Object {$_.Issues.Count -eq 0}).Count; pending=@($scan.Reports | Where-Object {$_.Issues.Count -gt 0}).Count; incomplete=$scan.IncompleteReports.Count; layouts=$scan.Inventory.Layouts.Count; otherXml=$scan.Inventory.OtherXml; distinctMessages=$messages.Count; families=$families; warnings=@($scan.Errors); rows=$rows; excluded=$excluded }
$audit | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputDirectory 'audit.json') -Encoding utf8
[pscustomobject]$audit | Select-Object scanned,queryDefinitions,reports,ready,pending,incomplete,layouts,otherXml,distinctMessages | Format-List
$families | Select-Object title,reports,visibleReports | Format-Table
