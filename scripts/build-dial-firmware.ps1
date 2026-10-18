param(
    [string]$PlatformIoPython,
    [ValidatePattern('^[D-Z]$')][string]$DriveLetter = 'N',
    [string]$Environment = 'jc3636k518',
    [int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$PlatformIoPython) { $PlatformIoPython = Join-Path $root '.tools/platformio/Scripts/python.exe' }
if (!(Test-Path -LiteralPath $PlatformIoPython -PathType Leaf)) { throw 'PlatformIO Python environment not found. See firmware/dial/README.md.' }
if ($Environment -ne 'jc3636k518') { throw 'Only the verified JC3636K518 build configuration is supported.' }
if ($Jobs -lt 1 -or $Jobs -gt 16) { throw 'Jobs must be between 1 and 16.' }
if (Test-Path -LiteralPath ($DriveLetter + ':\')) { throw 'Choose an unused drive letter D-Z.' }
# GCC used by Arduino-ESP32 2.x needs an ASCII-only include path.
# This script builds only. It never opens a serial port or uploads firmware.
$previousCoreDir = $env:PLATFORMIO_CORE_DIR
$mapped = $false
try {
    subst ($DriveLetter + ':') $root
    if ($LASTEXITCODE) { throw 'Temporary build drive failed.' }
    $mapped = $true
    $env:PLATFORMIO_CORE_DIR = $DriveLetter + ':\.tools\pio-core'
    & $PlatformIoPython -m platformio run --project-dir ($DriveLetter + ':\firmware\dial') --environment $Environment --jobs $Jobs
    if ($LASTEXITCODE) { throw 'Dial firmware build failed.' }
    $build = Join-Path $root ('firmware/dial/.pio/build/' + $Environment)
    Get-Item -LiteralPath (Join-Path $build 'firmware.bin'),(Join-Path $build 'bootloader.bin'),(Join-Path $build 'partitions.bin') |
        ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 }
} finally {
    if ($mapped) { subst ($DriveLetter + ':') /D }
    $env:PLATFORMIO_CORE_DIR = $previousCoreDir
}
