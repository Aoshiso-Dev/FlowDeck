param([Parameter(Mandatory)][string]$Address, [Parameter(Mandatory)][string]$Peer)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'tools/FlowDeck.Inventory/bin/Release/net10.0-windows/FlowDeck.Inventory.exe'
$rule = 'FlowDeck Temporary TLS Diagnostic'
try {
    New-NetFirewallRule -Name $rule -DisplayName $rule -Direction Inbound -Action Allow -Enabled True `
        -Program $exe -Protocol TCP -LocalAddress $Address -LocalPort 47631 -RemoteAddress $Peer -Profile Private | Out-Null
    & $exe --m5-tls $Address $Peer (Join-Path $env:LOCALAPPDATA 'FlowDeck/hardware') *> (Join-Path $root 'artifacts/m5go-tls-diagnostic.log')
} finally {
    Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
