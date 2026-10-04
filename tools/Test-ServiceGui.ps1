param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ApplicationPath, [string]$ServicePath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskPipe = 'FlowDeck.Cooling.Test.' + [Guid]::NewGuid().ToString('N')
$taskServiceExe = if ($ServicePath) { (Resolve-Path -LiteralPath $ServicePath).Path } else { Join-Path $taskRoot 'src/FlowDeck.Service/bin/Release/net10.0-windows/FlowDeck.Service.exe' }
$taskGuiExe = if ($ApplicationPath) { (Resolve-Path -LiteralPath $ApplicationPath).Path } else { Join-Path $taskRoot 'src/FlowDeck.App/bin/Release/net10.0-windows/win-x64/FlowDeck.exe' }
$taskStart = [Diagnostics.ProcessStartInfo]::new($taskServiceExe)
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardInput = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
foreach ($taskArg in @('--demo-host', '--pipe', $taskPipe, '--data-dir', (Join-Path $taskOutput 'service'))) { $taskStart.ArgumentList.Add($taskArg) }
$taskService = [Diagnostics.Process]::Start($taskStart)
$taskErrors = $taskService.StandardError.ReadToEndAsync()
$taskGui = $null
try {
    $taskReady = $taskService.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(15)).GetAwaiter().GetResult()
    if (-not $taskReady) { throw 'Demo service did not become ready.' }
    $taskReady | Set-Content -LiteralPath (Join-Path $taskOutput 'service-ready.json') -Encoding utf8
    $taskReady
    $taskGuiDir = Join-Path $taskOutput 'gui'
    New-Item -ItemType Directory -Path $taskGuiDir | Out-Null
    $taskGui = Start-Process -FilePath $taskGuiExe -ArgumentList @('--demo', '--smoke', '--data-dir', ('"' + $taskGuiDir + '"'), '--demo-service-pipe', $taskPipe, '--demo-service-pid', $taskService.Id) -WindowStyle Hidden -PassThru
    'Demo GUI PID: ' + $taskGui.Id
    if (-not $taskGui.WaitForExit(120000)) { throw 'Demo GUI test timed out.' }
    $taskReport = Join-Path $taskGuiDir 'service-ui-smoke.txt'
    if (-not (Test-Path -LiteralPath $taskReport)) { throw 'Demo GUI produced no report.' }
    Get-Content -LiteralPath $taskReport
    if (Select-String -LiteralPath $taskReport -Pattern '^FAIL' -Quiet) { throw 'Demo GUI reported a failure.' }
} finally {
    if ($taskGui -and -not $taskGui.HasExited) { $taskGui.Kill($true); $taskGui.WaitForExit() }
    if (-not $taskService.HasExited) {
        $taskService.StandardInput.WriteLine('stop')
        $taskService.StandardInput.Flush()
        if (-not $taskService.WaitForExit(15000)) { $taskService.Kill($true); $taskService.WaitForExit() }
    }
    $taskErrors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $taskOutput 'service-errors.txt') -Encoding utf8
    $taskService.Dispose()
    if ($taskGui) { $taskGui.Dispose() }
}
