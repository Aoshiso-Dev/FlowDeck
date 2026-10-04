$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'tools/FlowDeck.Inventory/bin/Release/net10.0-windows/FlowDeck.Inventory.exe'
& $exe 1> (Join-Path $root 'artifacts/temperatures-admin.json') 2> (Join-Path $root 'artifacts/temperatures-admin-errors.txt')
exit $LASTEXITCODE
