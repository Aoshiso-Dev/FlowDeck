# Packaging planning only. No cleanup, retention pruning, or existing package replacement.
function Get-FlowDeckDirectoryBytes {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return [long]0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction Stop | Measure-Object -Property Length -Sum).Sum
    return [long]$sum
}
function Get-FlowDeckPackagePlan {
    param([Parameter(Mandatory)][string]$Root, [switch]$ServiceOnly, [switch]$IncludeService,
        [switch]$OpenRgb, [switch]$SkipArchive, [switch]$FirmwareOnly, [string]$ExistingOutput)
    $rootPath = [IO.Path]::GetFullPath($Root)
    # Length metadata only. Publishing still performs all existing input/hash verification.
    $materials = [long]0
    $definition = Join-Path $rootPath 'third-party/distribution-materials.bundle.json'
    if (Test-Path -LiteralPath $definition -PathType Leaf) {
        $bundle = Get-Content -LiteralPath $definition -Raw | ConvertFrom-Json
        $location = [IO.Path]::GetFullPath($bundle.directory, $rootPath)
        if (-not $location.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Material estimate must stay inside the repository.' }
        $materials = Get-FlowDeckDirectoryBytes $location
    }
    $python = Get-FlowDeckDirectoryBytes (Join-Path $rootPath '.tools/python313')
    # Runtime/publish overhead plus measured Python and legal/material payload. Conservative estimate, not a reservation.
    $unit = [long](512MB + $materials + $python)
    $payloads = if ($ServiceOnly) { 1 } elseif ($IncludeService) { 2 } else { 1 }
    $expanded = [long]($payloads * $unit)
    $intermediate = [long]($payloads * 512MB)
    if ($OpenRgb) { $intermediate += $unit; $expanded += 256MB }
    if ($FirmwareOnly) { $expanded = 64MB; $intermediate = 0 }
    $zip = [long]0
    if (-not $ServiceOnly -and -not $SkipArchive) {
        $zip = if ($FirmwareOnly) { (Get-FlowDeckDirectoryBytes $ExistingOutput) + 64MB } else { $expanded }
        $zip = [long]([Math]::Ceiling($zip * 1.02) + 64MB)
    }
    $additional = [long]($expanded + $intermediate + $zip)
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($rootPath))
    $available = [long]$drive.AvailableFreeSpace
    [pscustomobject]@{
        Root = $rootPath; Drive = $drive.Name; AvailableBytes = $available
        EstimatedAdditionalBytes = $additional; ReserveBytes = [long]5GB
        RequiredFreeBytes = [long]($additional + 5GB); EnoughSpace = ($available -ge $additional + 5GB)
        ExpandedBytes = $expanded; IntermediateBytes = $intermediate; ArchiveBytes = $zip
        AppBuildCache = Join-Path $rootPath '.tools/package-cache/app'
        ServiceBuildCache = Join-Path $rootPath '.tools/package-cache/service'
        ArchiveRequested = (-not $ServiceOnly -and -not $SkipArchive)
        ExistingDataDeleted = $false
    }
}
function Assert-FlowDeckPackageSpace {
    param([Parameter(Mandatory)]$Plan)
    if ($Plan.AvailableBytes -lt $Plan.RequiredFreeBytes) {
        throw ('Insufficient space on {0}: {1:N2} GiB free; estimated {2:N2} GiB additional plus {3:N2} GiB reserve. No build started. Use -SkipArchive or review storage; nothing is deleted automatically.' -f
            $Plan.Drive,($Plan.AvailableBytes/1GB),($Plan.EstimatedAdditionalBytes/1GB),($Plan.ReserveBytes/1GB))
    }
}
function Enter-FlowDeckPackageLock {
    param([Parameter(Mandatory)][string]$Root)
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Root).TrimEnd('\').ToUpperInvariant())))
    $mutex = [Threading.Mutex]::new($false, ('Local\FlowDeck.Packaging.' + $key))
    try {
        try { $entered = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $entered = $true }
        if (-not $entered) { throw 'Another FlowDeck packaging process is using this repository. No shared cache is changed.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}
