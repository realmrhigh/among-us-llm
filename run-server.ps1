# Build and start the Impostor server with the LLM bots (Windows).
#   .\run-server.cmd                          announce this PC's LAN address to game clients
#   .\run-server.cmd -PublicIp 192.168.1.20
#   .\run-server.cmd --LlmBots:BrainMode=Heuristic     any config key can be overridden like this
param(
    [string]$PublicIp
)

Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$sdks = @()
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = @(dotnet --list-sdks 2>$null | Where-Object { $_ -match '^8\.' })
}
if ($sdks.Count -eq 0) {
    Write-Host 'The .NET 8 SDK is missing. Install it with:'
    Write-Host '  winget install Microsoft.DotNet.SDK.8'
    Write-Host 'then open a NEW terminal window and run this script again.'
    exit 1
}

if (-not $PublicIp) {
    try {
        $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | Sort-Object RouteMetric | Select-Object -First 1
        $PublicIp = (Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop | Select-Object -First 1).IPAddress
    } catch {
        $PublicIp = '127.0.0.1'
    }
}
Write-Host "Announcing $PublicIp to game clients (override with -PublicIp ...)"

Write-Host 'Building...'
$log = dotnet publish impostor/src/Impostor.Server -c Release -o server --nologo -v q 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $log
    Write-Host 'The build failed, see the messages above.'
    exit 1
}

# .env in this folder (one level above server) is found automatically; only OPENROUTER_API_KEY is read from it.
Set-Location server
dotnet Impostor.Server.dll "--Server:PublicIp=$PublicIp" @args
exit $LASTEXITCODE
