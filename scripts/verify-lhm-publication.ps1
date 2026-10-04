param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ReportPath, [string]$CompiledLibraryPath)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskVendor = Join-Path $taskRoot 'third-party/LibreHardwareMonitor'
$taskOutput = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }))
if (-not $taskOutput.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation output must stay inside the workspace.' }
$taskSources = @(Get-ChildItem -LiteralPath $taskVendor -Recurse -File | Where-Object { [IO.Path]::GetRelativePath($taskVendor, $_.FullName) -notmatch '^(bin|obj)[\\/]' })
foreach ($taskFile in $taskSources) {
    $taskRelative = [IO.Path]::GetRelativePath($taskVendor, $taskFile.FullName)
    $taskPublished = Join-Path (Join-Path $taskOutput 'third-party/LibreHardwareMonitor') $taskRelative
    if (-not (Test-Path -LiteralPath $taskPublished) -or (Get-FileHash -LiteralPath $taskPublished -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash) {
        throw ('Source copy missing or changed: ' + $taskRelative)
    }
}
$taskLibrary = Join-Path $taskOutput 'LibreHardwareMonitorLib.dll'
$taskHash = (Get-FileHash -LiteralPath $taskLibrary -Algorithm SHA256).Hash
$taskCompiledLibrary = if ($CompiledLibraryPath) { [IO.Path]::GetFullPath($CompiledLibraryPath, $taskRoot) } else { Join-Path $taskVendor 'bin/Release/net10.0/win-x64/LibreHardwareMonitorLib.dll' }
if (-not $taskCompiledLibrary.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $taskCompiledLibrary -eq $taskLibrary) { throw 'Use a separate compiled library inside the workspace.' }
if ($taskHash -ne (Get-FileHash -LiteralPath $taskCompiledLibrary -Algorithm SHA256).Hash) { throw 'Published library differs from the compiled runtime library.' }
$taskUpstream = Get-Content -LiteralPath (Join-Path $taskVendor 'UPSTREAM.json') -Raw | ConvertFrom-Json
$taskResources = @($taskUpstream.files | Where-Object { $_.path -like 'Resources/PawnIo/*.bin' })
$taskContext = [Runtime.Loader.AssemblyLoadContext]::new('FlowDeck-published-library-audit', $true)
try {
    # Reading assembly metadata and embedded bytes does not initialize Computer or open a driver.
    $taskAssembly = $taskContext.LoadFromAssemblyPath($taskLibrary)
    $taskMarker = @($taskAssembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and $_.ConstructorArguments[0].Value -eq 'FlowDeck.LhmPatch' -and $_.ConstructorArguments[1].Value -eq '8' })
    if ($taskMarker.Count -ne 1) { throw 'Published library patch marker missing.' }
    if ($taskResources.Count -ne 12 -or $taskAssembly.GetManifestResourceNames().Length -ne 12) { throw 'Embedded resource set changed.' }
    foreach ($taskResource in $taskResources) {
        $taskStream = $taskAssembly.GetManifestResourceStream('LibreHardwareMonitor.' + $taskResource.path.Replace('/', '.'))
        if ($null -eq $taskStream) { throw ('Missing module: ' + $taskResource.path) }
        try { if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskStream)) -ne $taskResource.sha256) { throw ('Published module changed: ' + $taskResource.path) } }
        finally { $taskStream.Dispose() }
    }
} finally { $taskContext.Unload() }
$taskReport = [ordered]@{ timestamp = (Get-Date).ToString('o'); output = [IO.Path]::GetRelativePath($taskRoot, $taskOutput); compiledLibrary = [IO.Path]::GetRelativePath($taskRoot, $taskCompiledLibrary); publishedLibrarySha256 = $taskHash; patchMarker = '8'; sourceFilesVerified = $taskSources.Count; unchangedPawnIoResources = $taskResources.Count; hardwareOpened = $false }
if ($ReportPath) {
    $taskReportPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ReportPath)) { $ReportPath } else { Join-Path $taskRoot $ReportPath }))
    if (-not $taskReportPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Report must stay inside the workspace.' }
    $taskReport | ConvertTo-Json | Set-Content -LiteralPath $taskReportPath -Encoding utf8
}
$taskReport | ConvertTo-Json
