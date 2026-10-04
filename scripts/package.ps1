param([switch]$FirmwareOnly, [switch]$IncludeService, [string]$OutputDirectory = 'dist/FlowDeck', [string]$ArchivePath = 'dist/FlowDeck-win-x64.zip', [string]$OpenRgbArchive, [switch]$SkipArchive, [switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'package-storage.ps1')
$packageLock = $null
Push-Location $root
try {
    if ($FirmwareOnly -and $IncludeService) { throw 'Service packaging requires a fresh complete package.' }
    if ($OpenRgbArchive -and (-not $IncludeService -or $FirmwareOnly)) { throw 'Optional OpenRGB requires a fresh package with IncludeService.' }
    $destination = [IO.Path]::GetFullPath($OutputDirectory, $root)
    $archive = [IO.Path]::GetFullPath($ArchivePath, $root)
    if (!$destination.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        !$archive.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package outputs must remain inside the repository.' }
    if ($archive.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $archive -eq $destination) { throw 'The ZIP must be outside its payload directory.' }
    if (!$FirmwareOnly -and ((Test-Path -LiteralPath $destination) -or (!$SkipArchive -and (Test-Path -LiteralPath $archive)))) { throw 'Use fresh application package and ZIP paths; existing packages are retained.' }
    if ($OpenRgbArchive -and -not (Test-Path -LiteralPath ([IO.Path]::GetFullPath($OpenRgbArchive, $root)) -PathType Leaf)) { throw 'The optional OpenRGB archive is missing.' }
    $running = Get-Process FlowDeck,NzxtControl -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetDirectoryName($_.Path) -eq $destination }
    if ($running -and !$FirmwareOnly) { throw 'Exit the application normally before replacing its package.' }
    $plan = Get-FlowDeckPackagePlan -Root $root -IncludeService:$IncludeService -OpenRgb:([bool]$OpenRgbArchive) -SkipArchive:$SkipArchive -FirmwareOnly:$FirmwareOnly -ExistingOutput $destination
    if ($PlanOnly) { $plan; return }
    $packageLock = Enter-FlowDeckPackageLock $root
    Assert-FlowDeckPackageSpace $plan
    if (!$FirmwareOnly) {
        # Keep self-contained runtime files and generated sources out of the normal development bin/obj.
        $publishArtifacts = $plan.AppBuildCache
        dotnet publish src/FlowDeck.App/FlowDeck.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path $publishArtifacts -p:RestoreLockedMode=true -m:1 -nodeReuse:false -o $destination
        if ($LASTEXITCODE) { throw 'Publish failed' }
        & (Join-Path $PSScriptRoot 'verify-lhm-publication.ps1') -OutputDirectory $destination -CompiledLibraryPath (Join-Path $publishArtifacts 'bin/LibreHardwareMonitorLib/release_win-x64/LibreHardwareMonitorLib.dll') | Out-Null
        $runtime = Join-Path $root '.tools/python313'
        if (!(Test-Path (Join-Path $runtime 'python.exe'))) { throw 'Run bootstrap-python.ps1 first' }
        New-Item -ItemType Directory -Force -Path (Join-Path $destination 'runtime/python') | Out-Null
        Copy-Item -Path (Join-Path $runtime '*') -Destination (Join-Path $destination 'runtime/python') -Recurse -Force
        $materials = & (Join-Path $PSScriptRoot 'copy-distribution-materials.ps1') -OutputDirectory $destination
    } elseif (!(Test-Path -LiteralPath (Join-Path $destination 'FlowDeck.exe'))) { throw 'Publish the app before updating firmware only.' }
    if (!$FirmwareOnly) {
        Copy-Item -LiteralPath 'README.md','THIRD-PARTY-NOTICES.md' -Destination $destination -Force
        Copy-Item -LiteralPath 'worker/requirements.lock' -Destination (Join-Path $destination 'worker') -Force
        Copy-Item -LiteralPath 'docs' -Destination $destination -Recurse -Force
    }
    $firmware = Join-Path $destination 'firmware'
    New-Item -ItemType Directory -Force -Path $firmware | Out-Null
    Copy-Item -LiteralPath 'firmware/m5go/.pio/build/m5go/firmware.bin','firmware/m5go/.pio/build/m5go/bootloader.bin','firmware/m5go/.pio/build/m5go/partitions.bin' -Destination $firmware -Force
    Copy-Item -LiteralPath '.tools/pio-core/packages/framework-arduinoespressif32/tools/partitions/boot_app0.bin' -Destination $firmware -Force
    Copy-Item -LiteralPath 'firmware/m5go/README.md' -Destination $firmware -Force
    if (!$FirmwareOnly) {
        Set-Content -LiteralPath (Join-Path $destination 'Demo.cmd') -Encoding ascii -Value @('@echo off','start "" "%~dp0FlowDeck.exe" --devices-preview --demo --data-dir "%LOCALAPPDATA%\FlowDeck\devices-demo"')
        Set-Content -LiteralPath (Join-Path $destination 'Start.cmd') -Encoding ascii -Value @('@echo off','start "" "%~dp0FlowDeck.exe" --devices')
        # Kept as a non-elevating launcher for existing shortcuts. Service setup has its own deployment tool.
        Set-Content -LiteralPath (Join-Path $destination 'Start-Admin.cmd') -Encoding ascii -Value @('@echo off','start "" "%~dp0FlowDeck.exe" --devices')
        if ($IncludeService) {
            & (Join-Path $PSScriptRoot 'package-service.ps1') -OutputDirectory ([IO.Path]::GetRelativePath($root, (Join-Path $destination 'service-setup'))) -OpenRgbArchive $OpenRgbArchive
        }
        $appRelease = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $destination 'FlowDeck.exe')).ProductVersion
        $serviceRelease = $null
        if ($IncludeService) {
            $serviceRelease = (Get-Content -LiteralPath (Join-Path $destination 'service-setup/service-package.json') -Raw | ConvertFrom-Json).release
            if ($appRelease -ne $serviceRelease) { throw 'Application and bundled service release versions must match.' }
        }
        [ordered]@{ format = 1; product = 'FlowDeck'; release = $appRelease; status = 'development'; runtime = 'devices-v2';
            legacySettingsMigration = $false; serviceIncluded = [bool]$IncludeService; serviceRelease = $serviceRelease;
            openRgbIncluded = [bool]$OpenRgbArchive; hardwareAcceptanceComplete = $false;
            distributionMaterialsSha256 = $materials.sha256; publicDistributionReady = $false } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'PACKAGE-INFO.json') -Encoding utf8
    }
    $files = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object FullName -ne (Join-Path $destination 'SHA256SUMS.txt')
    $lines = foreach ($file in $files) { (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetRelativePath($destination,$file.FullName) }
    Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Value $lines -Encoding utf8
    if (-not $SkipArchive) {
        # Recheck actual output size before creating the second full distribution artifact.
        $zipPlan = Get-FlowDeckPackagePlan -Root $root -FirmwareOnly -ExistingOutput $destination
        Assert-FlowDeckPackageSpace $zipPlan
        Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -Force
    }
} finally { if ($packageLock) { $packageLock.ReleaseMutex(); $packageLock.Dispose() }; Pop-Location }
