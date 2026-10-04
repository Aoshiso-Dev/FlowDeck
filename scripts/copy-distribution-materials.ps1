param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory, $taskRoot)
if (-not $taskOutput.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload output must stay inside the workspace.' }
if (-not (Test-Path -LiteralPath $taskOutput -PathType Container)) { throw 'Publish the payload first.' }
$taskDefinition = Get-Content -LiteralPath (Join-Path $taskRoot 'third-party/distribution-materials.bundle.json') -Raw | ConvertFrom-Json
$taskSource = [IO.Path]::GetFullPath($taskDefinition.directory, $taskRoot)
if (-not $taskSource.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $taskDefinition.format -ne 1) { throw 'Invalid material bundle location.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskSource 'materials.json') -Algorithm SHA256).Hash -ne $taskDefinition.manifestSha256) { throw 'Reviewed material manifest changed.' }
$taskDestination = Join-Path $taskOutput 'third-party/Distribution'
if (Test-Path -LiteralPath $taskDestination) { throw 'Material output must be new.' }
$taskPython = Join-Path $taskRoot '.tools/python313/python.exe'
$taskVerifier = Join-Path $PSScriptRoot 'prepare-distribution-notices.py'
if ((Get-FileHash -LiteralPath $taskVerifier -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskSource 'recipes/prepare-distribution-notices.py') -Algorithm SHA256).Hash) { throw 'Regenerate materials for the changed verifier.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'third-party/distribution-materials.lock.json') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskSource 'metadata/input-lock.json') -Algorithm SHA256).Hash) { throw 'Regenerate materials for the changed input lock.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'third-party/distribution-native-materials.lock.json') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskSource 'metadata/native-input-lock.json') -Algorithm SHA256).Hash) { throw 'Regenerate materials for the changed native input lock.' }
$taskResumedLock = Join-Path $taskSource 'metadata/resumed-input-lock.json'
if (Test-Path -LiteralPath $taskResumedLock) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'third-party/distribution-resumed-materials.lock.json') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskResumedLock -Algorithm SHA256).Hash) { throw 'Regenerate materials for the changed resumed input lock.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'scripts/prepare-resumed-distribution-materials.py') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskSource 'recipes/resumed/prepare-resumed-distribution-materials.py') -Algorithm SHA256).Hash) { throw 'Regenerate materials for the changed extension recipe.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskRoot 'device-definitions/openrgb-helper-runtime.json') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskSource 'metadata/openrgb-helper-runtime.json') -Algorithm SHA256).Hash) { throw 'Dependency materials refer to a different OpenRGB helper.' }
}
# Isolated stdlib-only verification; no packaged modules or hardware are imported.
& $taskPython -I -B $taskVerifier $taskSource --verify | Out-Null
if ($LASTEXITCODE) { throw 'Material source verification failed.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
Copy-Item -LiteralPath $taskSource -Destination $taskDestination -Recurse
$taskResult = & $taskPython -I -B $taskVerifier $taskDestination --verify-payload $taskOutput
if ($LASTEXITCODE) { throw 'Published dependency/material matching failed.' }
$taskResult = $taskResult | ConvertFrom-Json
$taskResult.payload = '.'
$taskResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'third-party/distribution-input-verification.json') -Encoding utf8
$taskResult
