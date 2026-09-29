# Among Us LLM bots — working notes (survives context compaction)

## Repository
- Single git repo at the project root; `impostor/` is a plain folder (the upstream clone's own .git was removed). Commit 1 is the unmodified upstream snapshot (Impostor/Impostor@b09c40b), commit 2 adds everything else, so `git diff <commit1> HEAD -- impostor/` shows exactly what was changed in upstream code (plus the new LlmBots folders and test projects).
- Windows: `run-server.cmd` / `selfplay.cmd` (thin wrappers around the .ps1 files; need `winget install Microsoft.DotNet.SDK.8`). `.github/workflows/ci.yml` builds and tests on Windows and Linux. The mock OpenRouter uses a plain TCP listener (HttpListener needs URL reservations on Windows).
- `.env` (the key) and `llmbots-logs/` are git-ignored.

## Environment
- .NET 8 SDK at ~/.dotnet (export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1)
- Impostor cloned to ./impostor (master b09c40b, supports Among Us 18.0 build 7238 PC / mobile via GameVersion 2026.7.15/16)
- No Among Us client on this Mac; real-client testing impossible overnight. Test in-process only.
- LLM: OpenRouter free models, key goes in ./.env as OPENROUTER_API_KEY (may be empty -> use mock/heuristic brain)

## Architecture decisions
- Impostor has NO fake-player support. Server = relay + validator. HOST CLIENT spawns all objects (SpawnFlag host-only) incl. each joiner's PlayerControl.
- Bots = in-process fake clients: `IHazelConnection` impl (SendAsync receives S2C msgs) + `ClientManager.RegisterConnectionAsync(...)` + `client.HandleMessageAsync(reader, type)` for C2S (pattern from src/Impostor.Tools.ServerReplay/Program.cs).
- Code lives INSIDE the Impostor.Server fork (internals needed), gated by config `LlmBots.Enabled`.
- Human hosts lobby with real client; bots join via HTTP endpoint. A scripted TEST HOST (in-process) is needed to exercise full games tonight.
- Brain in C# (HttpClient -> OpenRouter), mock brain + heuristic fallback.

## Progress log
- [00:40] dotnet installed, Impostor builds clean.
- [00:50] Protocol layer WORKS in-process: BotClient (fake client) + ScriptedHost (fake human host, in Impostor.LlmBots.Sim) pass lobby->start->roles/tasks tests (dotnet test src/Impostor.LlmBots.Tests).
  - Gotchas found: S2C GameDataTo has packed target id after game code; Rpc29SetTasks.Serialize is raw (use WriteBytesAndSize); simultaneous SceneChange from many clients races in Impostor (PlayerId dup) -> BotEnvironment.JoinGate serializes bot joins; host must mark itself ready.
  - Files: src/Impostor.Server/LlmBots/* (BotClient, BotConnection, BotAgent, BotEnvironment, LlmBotsConfig), partial-class shims Game.LlmBots.cs, InnerMeetingHud.LlmBots.cs, InnerCustomNetworkTransform.LlmBots.cs; AssemblyInfo InternalsVisibleTo for Sim/Tests/Selfplay.
  - Run tests: export PATH="$HOME/.dotnet:$PATH"; cd impostor/src; dotnet test Impostor.LlmBots.Tests
## TODO (ordered)
1. Map data + Skeld nav graph + movement + tasks (crew win by tasks test)
2. Impostor kills, bodies, reports, meetings (chat/vote), win/lose, rejoin next game
3. Brains: heuristic + LLM (OpenRouter free models, rate limiter, mock) 
4. BotManager + HTTP API (/llmbots/join?code=X&count=N) + Program.cs wiring + config.json + .env loading
5. Selfplay CLI, spectator page, README (how to connect real client: Windows PC + regionInfo.json; version pin 18.0 build 7238)
- [01:05] FULL GAME LOOP WORKS in-process with heuristic brains (tests GameplayTests: crew wins by tasks; impostor kills -> witness -> report -> meeting chat -> votes -> exile -> game end). Real Impostor validation passes with no anti-cheat flags.
  - Map layer: Maps/{BotMap,NavGraph,SkeldGraph,TaskPlanner}.cs, embedded JSON in LlmBots/MapData. Skeld+Dleks handmade graph; other maps auto graph (approximate).
  - Agent: BotAgent{,.Perception,.Gameplay,.Meeting}.cs; Brain/{MeetingContext,HeuristicBrain}.cs; GameEventHub (IEventListener) feeds WorldEvents.
  - Kill cooldown must be real time (server anticheat uses KillCooldown/2 wall clock). Sim uses LlmBotsConfig.TimeScale=6 for walking/tasks only.
  - .editorconfig in LlmBots silences StyleCop. Use tight grep on test output (compiler warnings are noisy).
- [01:40] LLM layer done+tested (Llm/OpenRouterClient, RequestBudget, ApiKey; Brain/{PromptBuilder,DecisionParser,LlmBrain}; MockOpenRouter in Sim). BotManager + endpoints (/llmbots, status, join, leave) + `!bots N` lobby chat command + LlmBotsService wired in Program.cs. RealServerTests boots the real host in-process and passes.
  - Public docs: README.md (root), run-server.sh, selfplay.sh, .env.example. Selfplay project written (src/Impostor.LlmBots.Selfplay/Program.cs).
  - Flaky test: TwoGamesInARow sometimes NREs in the test lambda (sim.Game null => host client got removed?) — investigating via scratch/flaky/loop.sh logs.
  - Design change: fewer LLM calls (Reply only when addressed; Final vote reuses earlier choice if chat unchanged), personas per bot, MaxRequestsPerMinute default 16.
  - Known gaps: no sabotage response (human impostor sabotage will not be fixed by bots), no vents, non-Skeld maps use auto nav graph.
- [02:00] Sabotage responders (reactor + O2): BotAgent.Sabotage.cs, ScriptedHost simulates the host side; SabotageTests pass. Join gate narrowed to the SceneChange call (fixed bots getting spawn-timeout kicked when several rejoined). SpawnTimeout raised to 6000 in config.json. Stress loop (scratch/stress.sh) of the full suite: 3/3 green before sabotage.
  - Startup OpenRouter self-check (BotManager.SelfCheckAsync), cross-site blocking on /llmbots API.

## STATE AT HANDOFF (2026-09-29 ~03:00)
- 54 tests green (`dotnet test` in impostor/src/Impostor.LlmBots.Tests, ~2.5 min); stress loop of the full suite repeatedly green.
- Demo: `./selfplay.sh --web 22123 --llm mock --speed 2` then open http://localhost:22123/llmbots (map + bot table + chat).
- Real server: `./run-server.sh` (needs .env with OPENROUTER_API_KEY for LLM; otherwise heuristics).
- NOT verified: real Among Us client (18.0 PC), real OpenRouter (no key at build time), HTTPS/region setup. See README section 9.
- Ideas not done: bot vents, bot-started sabotage, lights/comms fixes, better non-Skeld nav graphs (learn from human traces), Judge/other role abilities.
- Upstream files touched: Program.cs, Net/Client.cs (bot anti-cheat exemption), InnerShipStatus.cs (partial), Impostor.Server.csproj (embedded map JSON), config.json, Properties/AssemblyInfo.cs.

## Network / system access during the overnight build (for transparency)
- Installed the .NET 8 SDK into ~/.dotnet with Microsoft's dotnet-install.sh (no sudo). NuGet restores for Impostor's dependencies.
- `git clone --depth 1 https://github.com/Impostor/Impostor.git` into ./impostor.
- Read-only web lookups (docs, Among Us protocol write-ups) and one GET of OpenRouter's public model list (no key) to pick default free models / write tests.
- Started local servers on 127.0.0.1 ports 22023 / 22123 / 32123 / 32124 / 18100-19000 for testing; all stopped.
- The only secrets file is ./.env (empty placeholder I created). The app reads only OPENROUTER_API_KEY from it at runtime; I never printed or opened it.

## 2026-09-29 morning: real OpenRouter findings -> client redesign (done, 72 tests green)
- Live check after the redesign: `./selfplay.sh --probe --top 6` -> ling-3.0-flash-sante 2/2 valid (1.1 s), nemotron-3-super 2/2 (2.2 s), dots-3-note-preview 2/2 (2.3 s); gemma-4-31b/26b and qwen3.8 were 429 upstream at that moment. Full 7 bot games with `--llm openrouter`: 13 LLM calls, 0 failures, mostly ling-3.0-flash-sante at ~1.1 s; meetings read naturally and crew voted out the impostor.
- Live facts (key works, account has credits, is_free_tier false): `reasoning:{effort:low,exclude:true}` still let "thinking" models return empty/CoT content; `reasoning:{enabled:false}` fixes it (ling-3.0-flash-sante 1.0 s, gemma-4-26b-a4b-it 1.5 s, laguna-s-2.1 3.6 s valid JSON). Some models refuse (400 "Reasoning is mandatory", e.g. liquid/lfm-2.5-2.6b) or are agent-only (403, inkling). `openrouter/free` may route to a safety classifier ("User Safety: safe"). gemma-4-31b/qwen3.8/laguna-xs give 429 "temporarily rate-limited upstream" (shared pool), nemotron-3-super 503 -> these are model-level, not account-level.
- OpenRouterClient now: `reasoning.enabled=false` (retry without the param on other 400s mentioning it), per-model state (ok/fail/avg ms/rest-until/dead), failure classification (403/404/mandatory-reasoning -> dead; upstream 429/503/timeout/empty/out-of-format -> rest model with doubling backoff; per-minute 429 -> short global pause; per-day 429 -> global until midnight UTC), up to 4 failover attempts per call each with its own budget slot, proven-fast models first, `accept` validator (LlmBrain passes DecisionParser.LooksUsable), CalibrateAsync at startup (BotManager.SelfCheckAsync; stops after 3 working models), DescribeModels on the status endpoint. `openrouter/free` removed from defaults; "reasoning" models only penalised in ranking.
- DecisionParser.Salvage repairs cut-off / unbalanced / unescaped-quote JSON.
- Suggestion for the user (not tested, needs approval): the account has credits, so a cheap paid model (AllowPaidModels + Models) would be more reliable than the shared free pool.

## 2026-09-29 morning: model probe
- Added `./selfplay.sh --probe [--models a,b] [--top 6] [--rounds 1]` (Selfplay/ProbeModels.cs, OpenRouterClient.CompleteWithModelAsync/GetKeyInfoAsync). Verified against the mock only.
- User said the key was pasted, but ./.env was still the empty placeholder (mtime 00:26) and OPENROUTER_API_KEY was unset in the shell, so no real request has been made yet.
