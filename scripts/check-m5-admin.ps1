param([string]$Port = 'COM14')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
if (Get-Process -Name FlowDeck,NzxtControl -ErrorAction SilentlyContinue) { throw 'Exit the app normally before the read-only M5GO check.' }
$exe = Join-Path $root 'tools/FlowDeck.Inventory/bin/Release/net10.0-windows/FlowDeck.Inventory.exe'
& $exe --m5 $Port 1> (Join-Path $root 'artifacts/m5go-integration.json') 2> (Join-Path $root 'artifacts/m5go-integration-errors.txt')
exit $LASTEXITCODE
