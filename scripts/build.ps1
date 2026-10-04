param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # Each project declares its locked runtime targets; test/tool projects remain portable.
    dotnet restore FlowDeck.sln --locked-mode --disable-parallel -m:1 -nodeReuse:false
    if ($LASTEXITCODE) { throw 'NuGet restore failed' }
    dotnet build FlowDeck.sln --no-restore -c Release -m:1 -nodeReuse:false
    if ($LASTEXITCODE) { throw 'Build failed' }
    if ($Test) {
        dotnet test tests/FlowDeck.Tests/FlowDeck.Tests.csproj --no-build -c Release --logger 'trx;LogFileName=core.trx' --results-directory artifacts/tests
        if ($LASTEXITCODE) { throw 'C# tests failed' }
        & '.tools/python313/python.exe' -X utf8 -m unittest discover -s tests/worker -v
        if ($LASTEXITCODE) { throw 'Python tests failed' }
    }
} finally { Pop-Location }
