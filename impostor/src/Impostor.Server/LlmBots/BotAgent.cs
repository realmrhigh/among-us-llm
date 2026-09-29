using System;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.Customization;
using Impostor.Server.LlmBots.Brain;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Drives one bot: reacts to protocol events and, every tick, decides what the bot does next.
    /// </summary>
    internal sealed partial class BotAgent
    {
        private readonly BotEnvironment _env;
        private readonly ILogger _log;
        private static readonly string[] Personas =
        {
            "terse and to the point, few words",
            "friendly and chatty, likes to ask questions",
            "suspicious by nature, quick to point fingers but wants evidence",
            "calm and analytical, thinks out loud about who was where",
            "joker who keeps things light but still plays to win",
            "nervous and a bit scatterbrained, second-guesses themselves",
            "confident and bossy, tells everybody who to vote for",
            "quiet, only speaks when they have something concrete",
        };

        private static readonly string[] Greetings = { "hi!", "hey all", "yo", "ready when you are", "hello", "gl hf", "lets go", "sup" };

        private readonly Random _rng;
        private readonly HeuristicBrain _fallback;
        private bool _identitySent;
        private DateTime? _readyAt;
        private DateTime? _rejoinAt;
        private DateTime? _greetAt;
        private bool _sceneWanted;

        public BotAgent(BotEnvironment env, BotClient client, int seed)
        {
            _env = env;
            Client = client;
            _rng = new Random(seed);
            Hub = env.Hub;
            Persona = Personas[Math.Abs(seed) % Personas.Length];
            Brain = new HeuristicBrain(seed);
            _fallback = new HeuristicBrain(seed + 7);
            _log = env.LoggerFactory.CreateLogger($"Agent[{client.Name}]");
        }

        public BotClient Client { get; }

        public IBrain Brain { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the scene change handshake is done by someone else (the scripted host).
        /// </summary>
        public bool ManagedByHost { get; set; }

        public string Persona { get; }

        public bool Finished { get; private set; }

        public string? FinishReason { get; private set; }

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !Finished)
            {
                try
                {
                    foreach (var evt in Client.DrainInbox())
                    {
                        await HandleEventAsync(evt);
                    }

                    await TickAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Agent loop error");
                }

                try
                {
                    await Task.Delay(TickMilliseconds, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async ValueTask HandleEventAsync(BotEvent evt)
        {
            switch (evt.Kind)
            {
                case BotEventKind.Joined:
                    if (Client.Code != null)
                    {
                        Hub?.Register(Client.Code.Value, this);
                    }

                    _identitySent = false;
                    _rejoinAt = null;
                    _sceneWanted = !ManagedByHost;
                    break;

                case BotEventKind.GameStarted:
                    _readyAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(_rng.Next(200, 900));
                    OnGameStarting();
                    break;

                case BotEventKind.GameEnded:
                    _rejoinAt = DateTime.UtcNow + TimeSpan.FromSeconds(_env.Config.EndScreenSeconds + _rng.NextDouble());
                    OnGameEnded();
                    break;

                case BotEventKind.Removed when evt.A == Client.ClientId:
                case BotEventKind.Kicked when evt.A == Client.ClientId:
                case BotEventKind.Disconnected:
                    Finish(evt.Kind.ToString() + (evt.Text != null ? ": " + evt.Text : string.Empty));
                    break;
            }
        }

        public async ValueTask TickAsync()
        {
            var game = Client.Game;
            if (game == null || Finished)
            {
                return;
            }

            var now = DateTime.UtcNow;

            if (_sceneWanted)
            {
                _sceneWanted = false;
                await Client.SendSceneChangeAsync();
            }

            if (!_identitySent && Client.Me != null)
            {
                _identitySent = true;
                var color = Client.Client?.PreviousColor is { } previous && (int)previous >= 0 ? previous : (ColorType)_rng.Next(0, Enum.GetValues<ColorType>().Length);
                await Client.SetupIdentityAsync(color, (uint)_rng.Next(3, 60));

                if (!ManagedByHost && _rng.NextDouble() < 0.45)
                {
                    _greetAt = now + TimeSpan.FromSeconds(1.5 + (_rng.NextDouble() * 5));
                }
            }

            if (_greetAt != null && now >= _greetAt)
            {
                _greetAt = null;
                if (game.GameState == Impostor.Api.Innersloth.GameStates.NotStarted)
                {
                    await Client.SendChatAsync(Greetings[_rng.Next(Greetings.Length)]);
                }
            }

            if (_readyAt != null && now >= _readyAt)
            {
                _readyAt = null;
                await Client.SendReadyAsync();
            }

            if (_rejoinAt != null && now >= _rejoinAt && Client.Code != null)
            {
                _rejoinAt = null;
                await Client.JoinAsync(Client.Code.Value);
            }

            await TickLobbyAsync(game, now);
            await TickGameplayAsync(game, now);
        }

        public void Finish(string reason)
        {
            if (!Finished)
            {
                Finished = true;
                FinishReason = reason;

                _log.LogInformation("Finished: {Reason}", reason);
            }
        }

        private static int TickMilliseconds => 100;

        partial void OnGameStarting();

        partial void OnGameEnded();
    }
}
