# Watch bots play a full game against each other, no Among Us client needed (Windows).
#   .\selfplay.cmd [--players 8] [--games 2] [--speed 4] [--map skeld|mira|polus|dleks|airship|fungle]
#                  [--llm off|mock|openrouter] [--impostors 1] [--seed 1] [--verbose]
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'The .NET 8 SDK is missing. Install it with:  winget install Microsoft.DotNet.SDK.8'
    Write-Host 'then open a NEW terminal window and run this script again.'
    exit 1
}

dotnet run --project impostor/src/Impostor.LlmBots.Selfplay -c Release -v q -- @args
exit $LASTEXITCODE
