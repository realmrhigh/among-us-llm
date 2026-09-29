#!/bin/bash
# Watch bots play a full game against each other, no Among Us client needed.
#   ./selfplay.sh [--players 8] [--games 2] [--speed 4] [--map skeld|mira|polus|dleks|airship|fungle]
#                 [--llm off|mock|openrouter] [--impostors 1] [--seed 1] [--verbose]
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec dotnet run --project impostor/src/Impostor.LlmBots.Selfplay -c Release -v q -- "$@"
