# Among Us with LLM players

A modified [Impostor](https://github.com/Impostor/Impostor) server (open source Among Us server) that can put
**AI players** into a lobby. You host a normal Among Us game with the real game client, type `!bots 4` in the
lobby chat, and four bots join. They walk the map, do tasks, and, as Impostors, kill. In meetings they talk and vote,
and a language model (free OpenRouter models) writes what they say.

```
You (real Among Us client) ──┐
Friends (real clients)  ─────┼──►  Impostor server  ◄── LLM bots (run inside the server process)
                             │      (this folder)             │
                             │                                └── OpenRouter (free models) for meeting chat & votes
```

> **Status:** built and tested overnight without a real Among Us client (none on this Mac). The whole game loop runs
> against the real Impostor server code, with a scripted stand-in for the human host. The real client is untested:
> see [section 9](#9-untested-with-a-real-client-and-known-limitations) for what to try first.

---------------------------------------------------------------------------------------------------

## 1. Quick start

Get the code (on Windows, Git comes from `winget install Git.Git`; the repository is private, so Git opens a browser window to sign in to GitHub on the first clone):

```
git clone https://github.com/realmrhigh/among-us-llm.git
cd among-us-llm
```

The simplest setup is to run **the server and the game on the same PC**: on Windows that is the machine with your Among Us client.

### Windows

1. Install the .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`, then open a **new** terminal window.
2. Create your key file: `copy .env.example .env`, open `.env` in Notepad, paste your OpenRouter key after `OPENROUTER_API_KEY=` and save. (`.env` is git-ignored and only that one variable is ever read.)
3. Start the server: `run-server.cmd` (the first build takes a minute or two). When Windows Firewall asks, allow access on private networks.
4. Point the game at it (section 2), then host a game and type `!bots 4` (section 3).

```bat
run-server.cmd                                  rem announces this PC's LAN address; or: run-server.cmd -PublicIp 192.168.1.20
run-server.cmd --LlmBots:BrainMode=Heuristic    rem any config key can be overridden like this
selfplay.cmd --llm openrouter --players 6       rem watch bots play with real free models, no game client needed
selfplay.cmd --probe                            rem send two sample meeting prompts to the top free models and compare them
dotnet test impostor\src\Impostor.LlmBots.Tests rem the test suite
```

No key? The bots still play, using built-in rules for meeting chat and votes instead of a language model.

### macOS / Linux

```bash
# 1. paste your OpenRouter key after OPENROUTER_API_KEY= in the file  .env  (copy .env.example to .env first)
./run-server.sh                 # 2. builds and starts the server (needs the .NET 8 SDK, see below)
```

Watch a whole game without any Among Us client (handy to see the bots think):

```bash
./selfplay.sh                          # 7 bots, heuristic brains
./selfplay.sh --llm mock               # bots talk to a fake LLM server (tests the LLM plumbing offline)
./selfplay.sh --llm openrouter --players 6   # bots talk to real free OpenRouter models (needs the key)
./selfplay.sh --web 22123 --llm mock --speed 2   # same, but on the real server: watch the map at http://localhost:22123/llmbots
./selfplay.sh --probe                  # sends two sample meeting prompts to the top free models and compares them (about 12 requests)
```

`./run-server.sh` looks for `dotnet` in `~/.dotnet` first. If you do not have the SDK:
`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0` (installs to `~/.dotnet`, no sudo).

---------------------------------------------------------------------------------------------------

## 2. Connecting the real game

Among Us has no native macOS build, so the game client runs on a **Windows PC (Steam / Epic)**. Run the server on that same PC
(simplest), or on another machine on the same network (then use that machine's LAN address wherever this section says "the
server's address"). Mobile clients cannot use custom servers without modification.

**Client version.** The server is pinned to what Impostor supports. At the time of writing that is
**Among Us 18.0 (build 7238) on PC**, plus the 17.x builds listed in
`impostor/src/Impostor.Server/Net/Manager/CompatibilityManager.cs`. Bots copy the version of the lobby host, so
whatever your client is, the bots match it, as long as Impostor supports it.

**Region file.** Open <https://impostor.github.io/Impostor/>, enter the server's address, download `regionInfo.json`
and put it in `%AppData%\..\LocalLow\Innersloth\Among Us\` on the Windows PC (Impostor's website explains it per OS).

**HTTPS is required.** Since Among Us 16.0.5 the game only talks to the matchmaker over HTTPS with a valid
certificate. Easiest options (see `impostor/docs/Http-server.md`):

- Cloudflare quick tunnel on the server machine (Windows: `winget install Cloudflare.cloudflared`):
  `cloudflared tunnel --url http://localhost:22023` gives you an `https://<random>.trycloudflare.com` address to put in the
  region file. The game traffic itself is UDP on port 22023 and goes straight to the server machine, so start the server with
  its LAN address: `PUBLIC_IP=192.168.x.x ./run-server.sh` on macOS/Linux; on Windows `run-server.cmd` picks the LAN address
  itself (or `run-server.cmd -PublicIp 192.168.x.x`). The tunnel address changes every time you restart `cloudflared`, so
  regenerate the region file then.
- Or Caddy/nginx with a real domain and certificate.

Quick check: open `http://<the server's address>:22023` in a browser on the Windows PC; if it does not load there, the game will not connect either.

The UDP port 22023 must be reachable from the Windows PC (same PC or same LAN is fine). The first time the server starts, macOS or
Windows asks whether `dotnet` may accept incoming network connections: choose **Allow** (on Windows: private networks), otherwise other machines cannot reach it.

---------------------------------------------------------------------------------------------------

## 3. Playing with bots

1. In Among Us choose *Online → Create Game* (with your custom region selected). Choose the **normal** game mode.
2. In the lobby, type in the chat: **`!bots 4`** (only the lobby host can). Four bots join within a couple of seconds.
   - `!bots off` removes them, `!bots status` shows what is running.
3. Start the game as usual. Impostor count, map, speed, kill cooldown and meeting times are whatever you configured in
   the lobby; the bots read them.

You can also control bots without the game: open <http://localhost:22023/llmbots> on the server machine (join by game code,
see what every bot is doing, read the meeting chat), or use

```bash
curl -X POST "http://localhost:22023/llmbots/join?code=ABCDEF&count=3"
curl -X POST "http://localhost:22023/llmbots/leave"
```

The control API only accepts requests from the server machine itself, unless you set `LlmBots:ControlToken` and pass `?token=`.

**Rules of thumb**

- Bots can only join in the lobby, up to the lobby's player limit (and `LlmBots:MaxBots`).
- **You must be the host.** The host's game client runs the game rules; if the host leaves, the bots leave too.
- Bots use free chat text. If a client is set to *quick chat only*, it will not see their lines.

### What the bots do

- **In the lobby** they occasionally say hi and shuffle around a little (`LobbyIdleMovement`).
- **Crewmates** walk to their task locations, spend time there and complete the tasks. They report bodies they see,
  remember whom they saw where, and call an emergency meeting if they *saw* a kill or a vent. When a reactor meltdown
  or O2 sabotage starts, the two nearest crew bots run to the two consoles and repair it. Crew bots also show the visible
  task animations (the MedBay scan, shooting asteroids) so a watching human can vouch for them, and impostor bots cannot.
- **Impostors** roam and stalk, only kill when nobody else can see, respect the kill cooldown, sometimes report
  their own kill or walk away from it, and fake tasks. Now and then (`ImpostorSabotageChance`) they start a reactor meltdown or O2
  failure on The Skeld, Dleks and MIRA HQ, which the crew has to fix.
- **Meetings**: an opening statement, a reaction to what others said, and a final vote. Crewmates only state what they
  saw. Impostors deflect and bluff but never confess and never vote a teammate. A human's chat lines are part of what
  they read, so they answer your accusations.
- **Walls.** With wall data, bots plan routes around walls and furniture. The data is read from your own
  game install and is not part of this repository: run `pip install UnityPy` then `python tools/extract_collision.py` once
  (it writes the git-ignored `collision-data/` folder). Without it, bots fall back to the hand made waypoint graph and may cut corners.
  The Skeld and Dleks always use it; MIRA HQ, Polus, the Airship and the Fungle use it only when `selfplay --checkmaps` style
  fit testing passes (consoles, doors, vents and spawn standing on free ground), otherwise the server logs why and keeps the waypoints.
  Run `selfplay.cmd --checkmaps` (`./selfplay.sh --checkmaps`) after extracting to see, per ship, which points sit in walls and which routes fail.
- Not implemented: bots never use vents and only start reactor/O2 sabotages; they ignore lights, comms and doors; no shapeshifter/scientist/engineer roles, Hide & Seek, or mods.

---------------------------------------------------------------------------------------------------

## 4. The language model

Meetings are where the LLM plays: each bot makes an opening request per meeting, plus a reaction only when someone
addressed it, plus a final vote request only if the chat changed its mind: about **1–3 requests per bot per meeting**.
At startup the server tries the best few free models with a sample meeting prompt (watch for `OpenRouter check:` in the
server log), so a wrong key or a dead provider shows up immediately and the first meeting already knows which models are up and fast.
The brain is chosen by `LlmBots:BrainMode` in `impostor/src/Impostor.Server/config.json`:

| BrainMode | Meaning |
|-----------|---------|
| `Auto` (default) | LLM if a key is found, otherwise built-in heuristics |
| `Heuristic` | never call an LLM |
| `Llm` | same as Auto (kept for clarity) |

**Free models only.** Unless you set `LlmBots:AllowPaidModels`, only `:free` models are used. The `openrouter/free`
router is deliberately *not* used: it can hand the prompt to a safety classifier, which answers "User Safety: safe".
At startup the server asks OpenRouter which free text models exist, ranks them (instruct/chat models first, larger
first, no "code"/"safety"/"image" models) and uses the best few.
You can pin models: `"Models": ["google/gemma-4-26b-a4b-it:free", "google/gemma-4-31b-it:free"]`.

**Free models are shared and flaky, so the client is defensive.** Tested live, most free models are "thinking" models
that burn the whole answer on hidden reasoning unless told not to, and the busy ones return `429 temporarily rate-limited
upstream` or `503 overloaded` many times a day. The server therefore:

- sends `reasoning: {enabled: false}` with every request (a model that insists on thinking is dropped for the run),
- asks the best model first, and on a 429/503/timeout/empty or out-of-format answer rests *that model* (60 s, doubling
  while it keeps failing) and immediately tries the next one, up to 4 in one call; models that proved fast are asked first,
- repairs slightly broken JSON from small models (missing brace, stray quotes, cut off) before giving up on an answer.

**Rate limits are the catch.** OpenRouter's free tier is limited (about 20 requests/minute, and a low daily cap unless you
have bought some credits; check their current docs). The server therefore:

- keeps a shared budget (`MaxRequestsPerMinute`, default 16) for **all** bots, with the last slots reserved for votes,
- falls back to the heuristic brain instantly whenever the LLM is slow, rate limited, down, or answers nonsense,
- pauses briefly when OpenRouter says the account's per-minute allowance is used up, and stops asking for the rest of the day
  when it reports the daily limit is used up (it logs a warning). Adding a few credits to the OpenRouter account raises the free daily cap.

So a game never stalls because of the LLM, but with the free tier expect fewer, shorter LLM statements in big lobbies.
The status page (<http://localhost:22023/llmbots>, JSON at `/llmbots/status`) lists every model with its successes, failures, average speed and whether it is resting.

**Where the key is read from:** the environment variable `OPENROUTER_API_KEY`, else a `.env` file in the working
directory or one of the folders above it. Only that one variable is read. Nothing else on the machine is looked at.

The server console hides the bots' gameplay lines (who kills whom) so it does not spoil the game; set
`Serilog:MinimumLevel:Override:Agent` to `Information` in `config.json` to see them.

Logs: `llmbots-logs/meetings-*.txt` (chat and votes) and `llmbots-logs/llm-raw-*.log` (every prompt and answer, useful to tune
prompts; it never contains the key).

---------------------------------------------------------------------------------------------------

## 5. Configuration (`LlmBots` section of `config.json`)

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | switch the whole thing off |
| `BrainMode` | `Auto` | see above |
| `Models` | `[]` | fixed model list (empty = discover free models) |
| `MaxRequestsPerMinute` | `16` | shared LLM budget |
| `MaxRequestsPerRun` | `0` | hard cap of LLM requests per server run (0 = none) |
| `RequestTimeoutSeconds` | `25` | per request |
| `MaxLinesPerMeeting` | `3` | chat lines per bot per meeting |
| `MaxBots` | `12` | bots at once |
| `ImpostorSabotageChance` | `0.2` | chance an impostor bot starts a reactor/O2 sabotage when it gets the chance (0 = never) |
| `ChatCommands` | `true` | `!bots` in lobby chat |
| `LobbyIdleMovement` | `true` | bots shuffle around a little in the lobby |
| `ControlToken` | `""` | allow `/llmbots` API from other machines with `?token=` |
| `WriteTranscripts`, `TranscriptDirectory` | `true`, `llmbots-logs` | logs |
| `BotNames` | list | names (max 10 characters) |
| `MeetingAnimationSeconds`, `EndScreenSeconds`, `PostMeetingFreezeSeconds`, `TimeScale` | 9.9, 6, 5, 1 | pacing (only tests change these) |

---------------------------------------------------------------------------------------------------

## 6. How it works

Impostor is a relay plus rule checker. The **host's game client** spawns every player object, assigns roles and
tasks, and runs meetings. The bots are therefore *fake clients that live inside the server process*: each one has an
in-process connection object and sends the same protocol messages a real client would (join, scene change, spawn
handshake, movement, `CompleteTask`, `CheckMurder`, `ReportDeadBody`, `SendChat`, `CastVote`, ...), directly into
Impostor's message handler. So Impostor's own anti-cheat validates every bot action, and real clients see bots as
normal players. Because the bots run in the server, their "senses" read the game state directly, filtered by a
vision model (radius plus a walkability test, so no seeing through walls).

```
impostor/src/Impostor.Server/LlmBots/
  BotClient.cs        protocol: an in-process fake game client
  BotAgent*.cs        behaviour: perception, walking, tasks, kills, reports, meetings
  Maps/               map data, hand made Skeld waypoint graph, A* pathfinding, task planner
  Brain/              MeetingContext, HeuristicBrain, LlmBrain, prompts, answer parser
  Llm/                OpenRouter client (free models), request budget, .env reader
  BotManager.cs, LlmBotsEndpoints.cs, LlmBotsService.cs   joining, HTTP API, chat commands, startup wiring
impostor/src/Impostor.LlmBots.Sim/       in-process server + a scripted stand-in for a human host + mock OpenRouter
impostor/src/Impostor.LlmBots.Tests/     xUnit tests
impostor/src/Impostor.LlmBots.Selfplay/  the demo (./selfplay.sh)
```

Bots are exempt from Impostor's anti-cheat kicks: if a bot ever sends something the anti-cheat rejects (for example two impostor
bots picking the same victim), the message is dropped and a warning is logged instead of banning the bot. Kills are serialized per
game so that does not normally happen.

Small changes to upstream Impostor: `LlmBots` wiring in `Program.cs` (plus `CreateHostBuilder` made `internal` for tests),
`InternalsVisibleTo` lines in `Properties/AssemblyInfo.cs`, a `LlmBots` block and a longer `SpawnTimeout` in `config.json`, the bot
exemption in `Net/Client.cs`, `partial` on `InnerShipStatus`, and a few tiny `partial` files that expose internals
(`Game.LlmBots.cs`, `InnerMeetingHud.LlmBots.cs`, `InnerCustomNetworkTransform.LlmBots.cs`, `InnerShipStatus.LlmBots.cs`).
Impostor is GPLv3, so this fork is too.

---------------------------------------------------------------------------------------------------

## 7. Testing

```bash
export PATH="$HOME/.dotnet:$PATH"        # macOS/Linux with the SDK in ~/.dotnet; on Windows dotnet is already on PATH
cd impostor/src && dotnet test Impostor.LlmBots.Tests
```

The suite takes about 3 minutes and includes real-time waits (kill cooldowns, meeting timers), so run it on an otherwise idle machine.
The repository's CI runs it on Windows and Linux for every push.

Covers: map graphs, task planning, lobby flow, game start, a crew win by tasks, kills/reports/meetings/votes,
two games in a row (rejoin), bots using an LLM through a mock OpenRouter, the OpenRouter client (rate limits, key
rejection, model failover), answer parsing, and the *real* server host with HTTP endpoints and the `!bots` chat command.

The scripted host in `Impostor.LlmBots.Sim/ScriptedHost.cs` imitates what a real host client does. It is written from
Impostor's own parsers, so it matches what Impostor accepts, but it is not the real client.

---------------------------------------------------------------------------------------------------

## 8. Troubleshooting

| Symptom | Try |
|---------|-----|
| `!bots 4` does nothing | You must be the **host** of a lobby in normal mode, not yet started. Check the server log for `LLM bots are ready`. Or use <http://localhost:22023/llmbots>. |
| Client says the version is unsupported / too new | Impostor only supports certain builds. For a small hotfix you can start with `./run-server.sh --Compatibility:AllowFutureGameVersions=true`. |
| Client cannot connect at all | HTTPS/tunnel and region file (section 2); UDP 22023 reachable; `PUBLIC_IP` is the Mac's LAN address. |
| Bots leave immediately | They leave when no human is in the lobby or the host left. Keep the host in the game. |
| Bots talk in canned lines | No key found or the LLM is rate limited. Open the status page: it shows the brain, the startup check and call counts. |
| A bot vanished mid game | Look for `Bot ... sent something the anti cheat rejects` in the log; the bot is kept, only that message was dropped. |

---------------------------------------------------------------------------------------------------

## 9. Untested with a real client, and known limitations

Try these first when you connect a real client, in this order:

1. Bots appear in the lobby with the right names/colors (`!bots 2`).
2. Bots walk around after the game starts; kills show up; a dead body can be reported by you.
3. Meeting chat lines appear; bots vote; the exile result is correct.

Known limitations:

- Walking paths are a hand made approximation of The Skeld (and its mirror, Dleks). Other maps use an automatic graph
  (straight lines between task consoles), so bots can walk through walls there. Bots do not collide with walls anyway
  on your screen, they are just interpolated positions.
- No vents, only reactor/O2 sabotage by bots, no special roles. Bots are Crewmate or Impostor. Sabotage repairs use the message layout
  documented by the Among Us protocol write-ups and Impostor's parser, but could not be checked against a real host.
- Task steps are simplified: a bot stands at the task's console(s) for a plausible time, then completes it.
- Real clients enforce timing in ways I could not check without one (e.g. voting only after discussion ends; bots wait for it).
- If a real client's version is newer than Impostor supports, neither the client nor the bots can join.
- Free LLM models can be slow or repetitive; bots fall back to canned lines when they time out.
