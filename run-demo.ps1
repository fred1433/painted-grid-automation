# Rebuilds everything and replays the scenarios on this Windows machine.
# Needs the .NET 8 SDK. Uses the real desktop: do not touch the mouse or keyboard while it runs (about 15 minutes).
# Usage: ./run-demo.ps1            all scenarios
#        ./run-demo.ps1 -Only 03,14 scenarios whose name starts with 03 or 14
param([string]$Only = "")
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
./tools/fetch-tessdata.ps1
dotnet build src/Ledger.App/Ledger.App.csproj -c Release -o out/app
dotnet build src/LedgerMcp/LedgerMcp.csproj -c Release -o out/mcp
dotnet build src/LedgerOperator/LedgerOperator.csproj -c Release -o out/op
dotnet build tests/Scenarios/Scenarios.csproj -c Release -o out/scenarios
$args = @('--app', 'out/app/LedgerApp.exe', '--server', 'out/mcp/ledger-mcp.exe', '--operator', 'out/op/ledger-operator.exe', '--tessdata', 'tessdata', '--out', 'proof-local', '--work', 'work-local', '--repo', '.')
if ($Only -ne "") { $args += @('--only', $Only) }
& ./out/scenarios/scenarios.exe @args
Write-Host "Results: proof-local/summary.md"
