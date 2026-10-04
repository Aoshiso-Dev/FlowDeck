param([string]$OutputDirectory = 'artifacts/generalization-device-external-ui')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$hostDirectory = Join-Path $output 'service'
$guiDirectory = Join-Path $output 'gui'
$serviceExe = Join-Path $repository 'src/FlowDeck.Service/bin/Release/net10.0-windows/FlowDeck.Service.exe'
$appExe = Join-Path $repository 'src/FlowDeck.App/bin/Release/net10.0-windows/win-x64/FlowDeck.exe'
$pipe = 'FlowDeck.Devices.Demo.' + [Guid]::NewGuid().ToString('N')
$hostProcess = $null
$guiProcess = $null
$errorsTask = $null
$completed = $false
try {
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($serviceExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--devices-demo-host','--pipe',$pipe,'--data-dir',$hostDirectory)) { $start.ArgumentList.Add($argument) }
    $hostProcess = [Diagnostics.Process]::Start($start)
    $errorsTask = $hostProcess.StandardError.ReadToEndAsync()
    $line = $hostProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
    if ($null -eq $line) { throw $errorsTask.GetAwaiter().GetResult() }
    $ready = $line | ConvertFrom-Json
    if (!$ready.ready -or !$ready.demo -or $ready.processId -ne $hostProcess.Id -or $ready.pipe -ne $pipe) { throw 'Demo service readiness does not match the launched process.' }
    $guiProcess = Start-Process -FilePath $appExe -ArgumentList '--devices-preview','--demo','--smoke','--devices-pipe',$pipe,'--server-pid',([string]$hostProcess.Id),'--data-dir',('"' + $guiDirectory + '"') -WindowStyle Hidden -PassThru
    if (!$guiProcess.WaitForExit(60000)) { throw 'The external GUI test did not finish within 60 seconds.' }
    $guiProcess.Refresh()
    if ($guiProcess.ExitCode -ne 0) { throw "External GUI test failed: $($guiProcess.ExitCode)" }
    $report = Get-Content -LiteralPath (Join-Path $guiDirectory 'device-external-workspace-smoke.txt')
    if ($report.Where({$_ -like 'FAIL *'}).Count -gt 0 -or $report.Where({$_ -like 'PASS *'}).Count -ne 5) { throw 'External GUI test report is incomplete.' }
    $shellReport = Get-Content -LiteralPath (Join-Path $guiDirectory 'device-shell-smoke.txt')
    if ($shellReport.Where({$_ -like 'FAIL *'}).Count -gt 0 -or $shellReport.Where({$_ -like 'PASS *'}).Count -ne 3) { throw 'Tray/window shell test report is incomplete.' }
    $hostProcess.StandardInput.WriteLine('stop')
    $hostProcess.StandardInput.Flush()
    if (!$hostProcess.WaitForExit(15000)) { throw 'The demo service did not stop after the GUI released its session.' }
    if ($hostProcess.ExitCode -ne 0) { throw $errorsTask.GetAwaiter().GetResult() }
    $pointer = Get-Content -LiteralPath (Join-Path $hostDirectory 'configuration/current.json') -Raw | ConvertFrom-Json
    if ($pointer.id -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid demo configuration generation.' }
    $dashboard = Get-Content -LiteralPath (Join-Path $hostDirectory "configuration/generations/$($pointer.id)/dashboard.json") -Raw | ConvertFrom-Json
    if (!$dashboard.items.Where({$_.name -eq '別プロセスのラジエーター' -and $_.displayMaximum -eq 4500}).Count) { throw 'The external GUI settings were not persisted by the service.' }
    $result = [ordered]@{ serviceProcessId=$hostProcess.Id; guiProcessId=$guiProcess.Id; separateProcesses=($hostProcess.Id -ne $guiProcess.Id); guiChecks=5; shellChecks=3; guiExit=$guiProcess.ExitCode; serviceExit=$hostProcess.ExitCode; configurationGeneration=$pointer.id; persistedDisplay=$true; demo=$true }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'process-smoke.json') -Encoding utf8
    $report
    $shellReport
    $result | ConvertTo-Json -Compress
    $completed = $true
}
finally {
    if ($null -ne $guiProcess) {
        if (!$guiProcess.HasExited) { $guiProcess.Kill($true); $guiProcess.WaitForExit(10000) | Out-Null }
        $guiProcess.Dispose()
    }
    if ($null -ne $hostProcess) {
        if (!$hostProcess.HasExited) {
            try { $hostProcess.StandardInput.WriteLine('stop'); $hostProcess.StandardInput.Flush() } catch { }
            if (!$hostProcess.WaitForExit(15000)) { $hostProcess.Kill($true); $hostProcess.WaitForExit(10000) | Out-Null }
        }
        if ($null -ne $errorsTask -and $errorsTask.IsCompletedSuccessfully) {
            $errors = $errorsTask.GetAwaiter().GetResult()
            if ($errors) { [IO.File]::WriteAllText((Join-Path $output 'service-errors.txt'), $errors) }
        }
        $hostProcess.Dispose()
    }
}
if (!$completed) { exit 1 }
