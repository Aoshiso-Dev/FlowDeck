$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'package-storage.ps1')
$testRoot=Join-Path (Split-Path -Parent $PSScriptRoot) ('storage-fixture-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$report=[Collections.Generic.List[string]]::new()
function Check([bool]$Condition,[string]$Label) { if(-not $Condition){throw $Label}; $report.Add('PASS '+$Label) }
foreach($name in 'package.ps1','package-service.ps1','package-storage.ps1') {
    $parseErrors=$null;$tokens=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name),[ref]$tokens,[ref]$parseErrors)
    Check ($parseErrors.Count -eq 0) ('PowerShell parses '+$name)
}
New-Item -ItemType Directory -Path (Join-Path $testRoot '.tools/python313'),(Join-Path $testRoot 'third-party/materials') -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $testRoot '.tools/python313/python.dat'),[byte[]]::new(1024))
[IO.File]::WriteAllBytes((Join-Path $testRoot 'third-party/materials/notice.dat'),[byte[]]::new(2048))
'{"directory":"third-party/materials"}'|Set-Content -LiteralPath (Join-Path $testRoot 'third-party/distribution-materials.bundle.json')
$normal=Get-FlowDeckPackagePlan -Root $testRoot -IncludeService -OpenRgb
$noZip=Get-FlowDeckPackagePlan -Root $testRoot -IncludeService -OpenRgb -SkipArchive
Check ($normal.ArchiveBytes -gt 0 -and $noZip.ArchiveBytes -eq 0) 'SkipArchive eliminates the estimated ZIP allocation'
Check ($normal.EstimatedAdditionalBytes - $noZip.EstimatedAdditionalBytes -eq $normal.ArchiveBytes) 'ZIP saving is accounted without reducing payload or reserve'
Check ($normal.RequiredFreeBytes - $normal.EstimatedAdditionalBytes -eq 5GB) 'Five GiB reserve remains after estimated work'
Check ($normal.AppBuildCache -eq (Get-FlowDeckPackagePlan -Root $testRoot).AppBuildCache) 'Repeated planning reuses the same app cache path'
Check ($normal.AppBuildCache -ne $normal.ServiceBuildCache) 'App and service caches are separate'
Check (-not (Test-Path -LiteralPath (Join-Path $testRoot '.tools/package-cache'))) 'Planning creates no build cache or output'
$low=$normal.PSObject.Copy();$low.AvailableBytes=$low.RequiredFreeBytes-1
$refused=$false;try {Assert-FlowDeckPackageSpace $low}catch{$refused=$_.Exception.Message -like 'Insufficient space*'}
Check $refused 'Insufficient capacity is rejected before work'
$exact=$normal.PSObject.Copy();$exact.AvailableBytes=$exact.RequiredFreeBytes
Assert-FlowDeckPackageSpace $exact
Check $true 'Exact estimated capacity plus reserve is accepted'
$outer=Enter-FlowDeckPackageLock $testRoot
try {
    $inner=Enter-FlowDeckPackageLock $testRoot
    try { Check $true 'App can invoke nested service packaging under the same lock' } finally {$inner.ReleaseMutex();$inner.Dispose()}
    $job=Start-Job -ScriptBlock {param($helper,$root) . $helper;try{$l=Enter-FlowDeckPackageLock $root;$l.ReleaseMutex();$l.Dispose();'unexpected'}catch{'blocked'}} -ArgumentList (Join-Path $PSScriptRoot 'package-storage.ps1'),$testRoot
    $job|Wait-Job -Timeout 15|Out-Null
    Check (($job|Receive-Job) -eq 'blocked') 'A second process cannot publish into shared caches'
} finally {$outer.ReleaseMutex();$outer.Dispose()}
$again=Enter-FlowDeckPackageLock $testRoot;$again.ReleaseMutex();$again.Dispose()
Check $true 'The lock is available after release'
$before=@(Get-ChildItem -LiteralPath (Split-Path -Parent $PSScriptRoot) -Recurse -File).Count
$appPlan=& (Join-Path $PSScriptRoot 'package.ps1') -OutputDirectory 'unused-test-package' -SkipArchive -PlanOnly
$servicePlan=& (Join-Path $PSScriptRoot 'package-service.ps1') -OutputDirectory 'unused-service-package' -PlanOnly
$after=@(Get-ChildItem -LiteralPath (Split-Path -Parent $PSScriptRoot) -Recurse -File).Count
Check ($before -eq $after -and -not $appPlan.ArchiveRequested -and -not $servicePlan.ArchiveRequested) 'Real script PlanOnly paths perform no build, copy, ZIP or file write'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.ps1') -Raw
Check (-not $source.Contains('package-build-app-')) 'App no longer creates GUID publish directories'
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-service.ps1') -Raw
Check (-not $source.Contains('package-build-service-')) 'Service no longer creates GUID publish directories'
foreach($name in 'package.ps1','package-service.ps1','package-storage.ps1') {
    $source=Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
    Check ($source -notmatch '(?i)Remove-Item|Directory\]::Delete|File\]::Delete') ('No automatic deletion added to '+$name)
}
$report|Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'package-storage-test-results.txt') -Encoding utf8
$report
