param([string]$PlatformIoPython, [string]$DriveLetter = 'N', [string]$UploadPort)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$PlatformIoPython) { $PlatformIoPython = Join-Path $root '.tools/platformio/Scripts/python.exe' }
if (!(Test-Path -LiteralPath $PlatformIoPython)) { throw 'PlatformIO Python environment not found. See README.md.' }
if ($DriveLetter -notmatch '^[D-Z]$' -or (Test-Path ($DriveLetter + ':\'))) { throw 'Choose an unused drive letter D-Z.' }
if ($UploadPort -and $UploadPort -notmatch '^COM[1-9][0-9]*$') { throw 'Specify the confirmed M5GO port, for example COM14.' }
# ESP32 GCC 8 cannot handle Unicode include paths. The drive is temporary and no files are moved.
$previousCoreDir = $env:PLATFORMIO_CORE_DIR
try {
    subst ($DriveLetter + ':') $root
    if ($LASTEXITCODE) { throw 'Temporary build drive failed' }
    $env:PLATFORMIO_CORE_DIR = $DriveLetter + ':\.tools\pio-core'
    $buildArgs = @('-m', 'platformio', 'run', '--project-dir', ($DriveLetter + ':\firmware\m5go'))
    if ($UploadPort) { $buildArgs += @('--target', 'upload', '--upload-port', $UploadPort) }
    & $PlatformIoPython @buildArgs
    if ($LASTEXITCODE) { throw 'Firmware build failed' }
} finally {
    subst ($DriveLetter + ':') /D
    $env:PLATFORMIO_CORE_DIR = $previousCoreDir
}
