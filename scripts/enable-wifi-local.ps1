param([int]$InterfaceIndex = 29)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = (Resolve-Path -LiteralPath (Join-Path $root 'dist/FlowDeck/FlowDeck.exe')).Path
$before = Get-NetConnectionProfile -InterfaceIndex $InterfaceIndex
Set-NetConnectionProfile -InterfaceIndex $InterfaceIndex -NetworkCategory Private
foreach ($entry in @(@('TLS','TCP',47631), @('Discovery','UDP',47632))) {
    $name = 'FlowDeck M5GO ' + $entry[0]
    Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Allow -Enabled True `
        -Program $exe -Protocol $entry[1] -LocalPort $entry[2] -RemoteAddress LocalSubnet -Profile Private | Out-Null
}
[pscustomobject]@{
    Before = $before | Select-Object Name,InterfaceIndex,NetworkCategory
    After = Get-NetConnectionProfile -InterfaceIndex $InterfaceIndex | Select-Object Name,InterfaceIndex,NetworkCategory
    Rules = Get-NetFirewallRule -Name 'FlowDeck M5GO TLS','FlowDeck M5GO Discovery' | Select-Object Name,Enabled,Profile,Direction,Action
    Program = $exe
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'artifacts/wifi-network-setup.json') -Encoding utf8
