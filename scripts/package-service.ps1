param([Parameter(Mandatory)][string]$OutputDirectory, [string]$OpenRgbArchive, [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-storage.ps1')
$servicePackageLock = $null
try {
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory, $taskRoot)
if (-not $taskOutput.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must stay inside the workspace.' }
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a fresh service package output directory.' }
$taskBaseOutput = $taskOutput
if ($OpenRgbArchive) {
    $taskOpenRgbArchive = [IO.Path]::GetFullPath($OpenRgbArchive, $taskRoot)
    if (-not (Test-Path -LiteralPath $taskOpenRgbArchive -PathType Leaf)) { throw 'The optional OpenRGB archive is missing.' }
    # Keep the intermediate package outside an enclosing application distribution.
    $taskBaseOutput = Join-Path $taskRoot ('artifacts/service-package-base-' + [Guid]::NewGuid().ToString('N'))
}
$taskPayload = Join-Path $taskBaseOutput 'payload'
$taskPlan = Get-FlowDeckPackagePlan -Root $taskRoot -ServiceOnly -OpenRgb:([bool]$OpenRgbArchive)
if ($PlanOnly) { $taskPlan; return }
$servicePackageLock = Enter-FlowDeckPackageLock $taskRoot
Assert-FlowDeckPackageSpace $taskPlan
$taskPublishArtifacts = $taskPlan.ServiceBuildCache
dotnet publish (Join-Path $taskRoot 'src/FlowDeck.Service/FlowDeck.Service.csproj') -c Release -r win-x64 --self-contained true --artifacts-path $taskPublishArtifacts -p:RestoreLockedMode=true -m:1 -nodeReuse:false -o $taskPayload
if ($LASTEXITCODE) { throw 'Service publish failed.' }
& (Join-Path $PSScriptRoot 'verify-lhm-publication.ps1') -OutputDirectory $taskPayload -CompiledLibraryPath (Join-Path $taskPublishArtifacts 'bin/LibreHardwareMonitorLib/release_win-x64/LibreHardwareMonitorLib.dll') | Out-Null
$taskPython = Join-Path $taskRoot '.tools/python313'
if (-not (Test-Path -LiteralPath (Join-Path $taskPython 'python.exe'))) { throw 'The pinned Python runtime is missing.' }
New-Item -ItemType Directory -Path (Join-Path $taskPayload 'runtime/python') -Force | Out-Null
Copy-Item -Path (Join-Path $taskPython '*') -Destination (Join-Path $taskPayload 'runtime/python') -Recurse
New-Item -ItemType Directory -Path (Join-Path $taskPayload 'worker') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'worker/hub_worker.py'),(Join-Path $taskRoot 'worker/requirements.lock') -Destination (Join-Path $taskPayload 'worker')
& (Join-Path $PSScriptRoot 'copy-distribution-materials.ps1') -OutputDirectory $taskPayload | Out-Null
$taskExe = Join-Path $taskPayload 'FlowDeck.Service.exe'
$taskProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).ProductVersion
# Informational versions append the Git identity; the deployment contract uses System.Version.
$taskVersion = ([Version]($taskProductVersion.Split('+', 2)[0])).ToString()
& $taskExe --package-manifest $taskBaseOutput $taskVersion
if ($LASTEXITCODE) { throw 'Service package verification failed.' }
if ($OpenRgbArchive) {
    $taskReviewedHash = (Get-FileHash -LiteralPath (Join-Path $taskBaseOutput 'service-package.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    & $taskExe --package-openrgb $taskBaseOutput $taskReviewedHash $taskOpenRgbArchive $taskOutput
    if ($LASTEXITCODE) { throw 'Optional OpenRGB service package preparation failed. The base package is retained.' }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/サービス導入確認.md'),(Join-Path $taskRoot 'docs/汎用版_サービス導入画面.md'),(Join-Path $taskRoot 'docs/OpenRGB_任意サービス配布.md'),(Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
} finally { if ($servicePackageLock) { $servicePackageLock.ReleaseMutex(); $servicePackageLock.Dispose() } }
