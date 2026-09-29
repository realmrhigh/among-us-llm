#!/bin/bash
# Build and start the Impostor server with the LLM bots.
#   ./run-server.sh                     announce this Mac's LAN address to clients
#   PUBLIC_IP=192.168.1.20 ./run-server.sh
#   ./run-server.sh --LlmBots:BrainMode=Heuristic     any config key can be overridden like this
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET 8 SDK is missing. Install it with:"
  echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0"
  exit 1
fi

PUBLIC_IP="${PUBLIC_IP:-$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo 127.0.0.1)}"
echo "Announcing $PUBLIC_IP to game clients (override with PUBLIC_IP=...)"

echo "Building..."
if ! BUILD_LOG=$(dotnet publish impostor/src/Impostor.Server -c Release -o server --nologo -v q 2>&1); then
  echo "$BUILD_LOG"
  echo "The build failed, see the messages above."
  exit 1
fi
cd server
# .env in the folder above is found automatically (only OPENROUTER_API_KEY is read from it).
exec dotnet Impostor.Server.dll --Server:PublicIp="$PUBLIC_IP" "$@"
