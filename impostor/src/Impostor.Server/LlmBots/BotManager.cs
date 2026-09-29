using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.Customization;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Llm;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    internal sealed record BotSummary(string Name, string Game, string Activity, string Role, bool Alive, string Room, string Brain);

    internal sealed record BotCommandResult(bool Ok, string Message, int Added = 0);

    /// <summary>
    ///     Creates bots, keeps them running and takes them away again.
    /// </summary>
    internal sealed class BotManager : IDisposable
    {
        private readonly BotEnvironment _env;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, List<ManagedBot>> _bots = new();
        private readonly ConcurrentQueue<string> _recent = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly object _fileLock = new();
        private readonly Random _rng = new();
        private readonly SemaphoreSlim _addLock = new(1, 1);
        private readonly Timer _watchdog;
        private readonly Lazy<OpenRouterClient?> _llm;
        private string? _transcriptFile;
        private int _seed;

        public BotManager(BotEnvironment env)
        {
            _env = env;
            _logger = env.LoggerFactory.CreateLogger("LlmBots");
            _llm = new Lazy<OpenRouterClient?>(CreateLlmClient);
            _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public OpenRouterClient? Llm => _llm.Value;

        /// <summary>Gets the outcome of the check that runs at startup ("not checked", "ok ...", or the problem).</summary>
        public string LlmCheck { get; private set; } = "not checked";

        public IReadOnlyList<string> RecentLines => _recent.ToArray();

        public string BrainDescription
        {
            get
            {
                var mode = _env.Config.BrainMode;
                if (string.Equals(mode, "Heuristic", StringComparison.OrdinalIgnoreCase))
                {
                    return "heuristic (no LLM)";
                }

                var client = Llm;
                return client != null && client.HasKey ? $"LLM via OpenRouter (free models){(client.Available ? string.Empty : ", currently unavailable")}" : "heuristic (no OpenRouter key found)";
            }
        }

        public int ActiveBots => _bots.Values.Sum(l => Copy(l).Count(b => !b.Agent.Finished));

        public IReadOnlyList<BotSummary> List()
        {
            var result = new List<BotSummary>();
            foreach (var (code, bots) in _bots)
            {
                foreach (var bot in Copy(bots))
                {
                    var info = bot.Client.MyInfo;
                    var map = bot.Agent.InGame ? Maps.BotMap.Get((bot.Client.Game?.Options as Impostor.Api.Innersloth.GameOptions.NormalGameOptions)?.Map ?? MapTypes.Skeld) : null;
                    result.Add(new BotSummary(
                        bot.Client.Name,
                        GameCode.From(code).Code,
                        bot.Agent.Finished ? "left: " + bot.Agent.FinishReason : bot.Agent.Activity,
                        info?.RoleType?.ToString() ?? "-",
                        info != null && !info.IsDead,
                        map?.RoomAt(bot.Agent.Position) ?? "-",
                        bot.Agent.Brain.Name));
                }
            }

            return result;
        }

        /// <summary>
        ///     Everything the spectator map needs: where the players are in every game that has bots.
        /// </summary>
        /// <param name="spoilers">Whether to reveal roles.</param>
        /// <returns>An object that serializes to JSON.</returns>
        public object Snapshot(bool spoilers)
        {
            var games = new List<object>();
            var maps = new Dictionary<string, object>();

            foreach (var key in _bots.Keys.ToList())
            {
                var game = _env.GameManager.Find(GameCode.From(key));
                if (game == null)
                {
                    continue;
                }

                var options = game.Options as Impostor.Api.Innersloth.GameOptions.NormalGameOptions;
                var map = Maps.BotMap.Get(options?.Map ?? MapTypes.Skeld);
                if (!maps.ContainsKey(map.Name))
                {
                    maps[map.Name] = new
                    {
                        nodes = map.Nav.Nodes.Select(n => new object[] { Math.Round(n.Position.X, 2), Math.Round(n.Position.Y, 2), Maps.BotMap.DisplayName(n.Room) }).ToList(),
                        edges = map.Nav.Nodes.SelectMany(n => n.Edges.Where(e => e > n.Index).Select(e => new[] { n.Index, e })).ToList(),
                    };
                }

                var players = game.Players
                    .Where(p => p.Character != null)
                    .Select(p =>
                    {
                        var info = p.Character!.PlayerInfo;
                        var pos = p.Character.NetworkTransform.Position;
                        return new
                        {
                            name = info?.PlayerName ?? p.Client.Name,
                            color = info?.CurrentOutfit.Color.ToString() ?? "Red",
                            x = Math.Round(pos.X, 2),
                            y = Math.Round(pos.Y, 2),
                            alive = info == null || !info.IsDead,
                            bot = IsBot(p.Client.Id),
                            role = spoilers ? info?.RoleType?.ToString() : null,
                        };
                    })
                    .ToList();

                var bodies = _env.Hub.BodiesOf(game.Code)
                    .Select(b => new { x = Math.Round(b.Position.X, 2), y = Math.Round(b.Position.Y, 2), name = game.GameNet.GameData.GetPlayerById(b.VictimId)?.PlayerName ?? "?" })
                    .ToList();

                games.Add(new { code = game.Code.Code, state = game.GameState.ToString(), map = map.Name, players, bodies });
            }

            return new { games, maps };
        }

        public async Task<BotCommandResult> AddAsync(string codeText, int count)
        {
            GameCode code;
            try
            {
                code = GameCode.From(codeText.Trim().ToUpperInvariant());
            }
            catch (Exception)
            {
                return new BotCommandResult(false, $"'{codeText}' is not a valid game code.");
            }

            var game = _env.GameManager.Find(code);
            if (game == null)
            {
                return new BotCommandResult(false, $"No game with code {code.Code} exists on this server.");
            }

            return await AddAsync(game, count);
        }

        public async Task<BotCommandResult> AddAsync(Game game, int count)
        {
            await _addLock.WaitAsync();
            try
            {
                return await AddLockedAsync(game, count);
            }
            finally
            {
                _addLock.Release();
            }
        }

        private async Task<BotCommandResult> AddLockedAsync(Game game, int count)
        {
            if (game.GameState != GameStates.NotStarted)
            {
                return new BotCommandResult(false, "The game is already running. Bots can only join in the lobby.");
            }

            if (game.Options is not Impostor.Api.Innersloth.GameOptions.NormalGameOptions)
            {
                return new BotCommandResult(false, "Bots only support the normal game mode (not Hide and Seek).");
            }

            if (game.Host == null)
            {
                return new BotCommandResult(false, "The lobby has no host.");
            }

            count = Math.Max(1, count);
            var room = game.Options.MaxPlayers - game.PlayerCount;
            var globalRoom = _env.Config.MaxBots - ActiveBots;
            var allowed = Math.Min(count, Math.Min(room, globalRoom));
            if (allowed <= 0)
            {
                return new BotCommandResult(false, room <= 0 ? "The lobby is full." : $"The server already runs its limit of {_env.Config.MaxBots} bots.");
            }

            var version = game.Host.Client.GameVersion;
            var added = 0;
            for (var i = 0; i < allowed; i++)
            {
                var name = PickName(game);
                var client = new BotClient(_env, name);
                if (!await client.ConnectAsync(version))
                {
                    _logger.LogWarning("Bot {Name} could not connect (client version {Version} not supported?)", name, version);
                    break;
                }

                var seed = Interlocked.Increment(ref _seed) + Environment.TickCount;
                var agent = new BotAgent(_env, client, seed);
                agent.Brain = CreateBrain(agent, seed);
                agent.Transcript = Log;

                var bot = new ManagedBot(client, agent, CancellationTokenSource.CreateLinkedTokenSource(_cts.Token));
                var botList = _bots.GetOrAdd(game.Code.Value, _ => new List<ManagedBot>());
                lock (botList)
                {
                    botList.Add(bot);
                }

                bot.Loop = Task.Run(() => agent.RunAsync(bot.Cts.Token));

                await client.JoinAsync(game.Code);
                added++;
                await Task.Delay(350);
            }

            var message = added == 0 ? "No bot could join." : $"{added} bot{(added == 1 ? string.Empty : "s")} joining game {game.Code.Code}. Brain: {BrainDescription}.";
            _logger.LogInformation("{Message}", message);
            return new BotCommandResult(added > 0, message, added);
        }

        public async Task<int> RemoveAsync(GameCode? code)
        {
            var removed = 0;
            foreach (var key in _bots.Keys.ToList())
            {
                if (code != null && key != code.Value.Value)
                {
                    continue;
                }

                if (_bots.TryRemove(key, out var list))
                {
                    foreach (var bot in Copy(list))
                    {
                        await StopAsync(bot);
                        removed++;
                    }
                }
            }

            return removed;
        }

        /// <summary>
        ///     Tries a few models at startup, so a wrong key or a dead provider is noticed right away instead of in the first
        ///     meeting, and the client already knows which models are fast and reliable.
        /// </summary>
        /// <returns>A task that finishes when the check is done.</returns>
        public async Task SelfCheckAsync()
        {
            var client = Llm;
            if (client == null || !client.HasKey)
            {
                LlmCheck = "no key";
                return;
            }

            try
            {
                var (summary, usable) = await client.CalibrateAsync(_cts.Token);
                LlmCheck = (usable ? "ok: " : "problem: ") + summary;
                if (usable)
                {
                    _logger.LogInformation("OpenRouter check: {Summary}.", summary);
                }
                else
                {
                    _logger.LogWarning("OpenRouter check: {Summary}. Bots fall back to built-in heuristics until a model answers.", summary);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LlmCheck = "problem: " + ex.Message;
                _logger.LogWarning("OpenRouter check failed: {Message}. Bots will fall back to heuristics whenever the LLM is unavailable.", ex.Message);
            }
        }

        public bool IsBot(int clientId) => _bots.Values.Any(l => Copy(l).Any(b => b.Client.ClientId == clientId));

        public async Task<string> HandleChatCommandAsync(Game game, ClientPlayerRef sender, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "1";

            if (int.TryParse(verb, out var count))
            {
                return (await AddAsync(game, count)).Message;
            }

            switch (verb)
            {
                case "add":
                    return (await AddAsync(game, parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 1)).Message;
                case "off":
                case "leave":
                case "kick":
                case "remove":
                    var removed = await RemoveAsync(game.Code);
                    return $"Removed {removed} bot(s).";
                case "status":
                    return $"{ActiveBots} bots active. Brain: {BrainDescription}.";
                default:
                    return "Commands: !bots N (add N bots), !bots off (remove them), !bots status";
            }
        }

        public void Log(string line)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 300)
            {
                _recent.TryDequeue(out _);
            }

            if (!_env.Config.WriteTranscripts)
            {
                return;
            }

            try
            {
                lock (_fileLock)
                {
                    if (_transcriptFile == null)
                    {
                        Directory.CreateDirectory(_env.Config.TranscriptDirectory);
                        _transcriptFile = Path.Combine(_env.Config.TranscriptDirectory, $"meetings-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                    }

                    File.AppendAllText(_transcriptFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Could not write the transcript: {Message}", ex.Message);
            }
        }

        public void Dispose()
        {
            _watchdog.Dispose();
            _cts.Cancel();
            foreach (var list in _bots.Values)
            {
                foreach (var bot in Copy(list))
                {
                    bot.Cts.Cancel();
                }
            }

            if (_llm.IsValueCreated)
            {
                _llm.Value?.Dispose();
            }
        }

        private OpenRouterClient? CreateLlmClient()
        {
            if (string.Equals(_env.Config.BrainMode, "Heuristic", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var key = ApiKey.Find(_env.Config.ApiKeyVariable);
            if (key == null)
            {
                _logger.LogWarning(
                    "No OpenRouter API key found (looked for {Variable} in the environment and in .env files). Bots will play meetings with built-in heuristics. Put OPENROUTER_API_KEY=... in a .env file next to the server to enable the LLM.",
                    _env.Config.ApiKeyVariable);
            }

            return new OpenRouterClient(_env.Config, _env.LoggerFactory.CreateLogger("OpenRouter"), key);
        }

        private IBrain CreateBrain(BotAgent agent, int seed)
        {
            var heuristic = new HeuristicBrain(seed);
            var client = Llm;
            if (client == null || !client.HasKey)
            {
                return heuristic;
            }

            return new LlmBrain(client, heuristic, _env.LoggerFactory.CreateLogger("LlmBrain"), RawLog);
        }

        private void RawLog(string text)
        {
            if (!_env.Config.WriteTranscripts)
            {
                return;
            }

            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(_env.Config.TranscriptDirectory);
                    File.AppendAllText(Path.Combine(_env.Config.TranscriptDirectory, $"llm-raw-{DateTime.Now:yyyyMMdd}.log"), text + Environment.NewLine);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Could not write the LLM log: {Message}", ex.Message);
            }
        }

        private string PickName(Game game)
        {
            var used = new HashSet<string>(
                game.GameNet.GameData.Players.Values.Select(p => p.PlayerName).Concat(game.Players.Select(p => p.Client.Name)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var list in _bots.Values)
            {
                foreach (var b in Copy(list))
                {
                    used.Add(b.Client.Name);
                }
            }

            var pool = _env.Config.BotNames.Where(n => n.Length is > 0 and <= 10 && !used.Contains(n)).ToList();
            if (pool.Count > 0)
            {
                return pool[_rng.Next(pool.Count)];
            }

            for (var i = 1; ; i++)
            {
                var candidate = "Bot" + i;
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static List<ManagedBot> Copy(List<ManagedBot> list)
        {
            lock (list)
            {
                return list.ToList();
            }
        }

        private async Task StopAsync(ManagedBot bot)
        {
            try
            {
                bot.Agent.Finish("removed");
                bot.Cts.Cancel();
                await bot.Client.LeaveAsync();
                if (bot.Client.Code != null)
                {
                    _env.Hub.Unregister(bot.Client.Code.Value, bot.Agent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while removing a bot");
            }
        }

        private void Watch()
        {
            try
            {
                foreach (var (key, list) in _bots.ToList())
                {
                    var game = _env.GameManager.Find(GameCode.From(key));
                    var current = Copy(list);
                    var finished = current.Where(b => b.Agent.Finished || !b.Client.IsConnected).ToList();
                    foreach (var bot in finished)
                    {
                        lock (list)
                        {
                            list.Remove(bot);
                        }

                        _ = StopAsync(bot);
                    }

                    current = Copy(list);
                    if (current.Count == 0)
                    {
                        _bots.TryRemove(key, out _);
                        continue;
                    }

                    if (game == null)
                    {
                        _ = RemoveAsync(GameCode.From(key));
                        continue;
                    }

                    // A game without a human in charge cannot work: the host client does the game logic.
                    var host = game.Host;
                    var humans = game.Players.Count(p => !current.Any(b => b.Client.ClientId == p.Client.Id));
                    if (host == null || humans == 0 || current.Any(b => b.Client.ClientId == host.Client.Id))
                    {
                        _logger.LogInformation("No human host left in game {Code}, the bots leave.", game.Code.Code);
                        _ = RemoveAsync(game.Code);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bot watchdog failed");
            }
        }

        private sealed class ManagedBot
        {
            public ManagedBot(BotClient client, BotAgent agent, CancellationTokenSource cts)
            {
                Client = client;
                Agent = agent;
                Cts = cts;
            }

            public BotClient Client { get; }

            public BotAgent Agent { get; }

            public CancellationTokenSource Cts { get; }

            public Task? Loop { get; set; }
        }
    }

    /// <summary>
    ///     The player that typed a chat command.
    /// </summary>
    /// <param name="ClientId">Client id of the player.</param>
    /// <param name="IsHost">Whether the player hosts the lobby.</param>
    internal sealed record ClientPlayerRef(int ClientId, bool IsHost);
}
