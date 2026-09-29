using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api.Events;
using Impostor.Api.Events.Meeting;
using Impostor.Api.Events.Player;
using Impostor.Api.Games;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Listens to what happens on the server and tells the bots that are playing in that game.
    /// </summary>
    internal sealed class GameEventHub : IEventListener
    {
        private readonly ILogger<GameEventHub> _logger;
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<BotAgent, byte>> _agents = new();
        private readonly ConcurrentDictionary<int, List<BodyRecord>> _bodies = new();
        private readonly ConcurrentDictionary<(int Code, int System, int Console), BotAgent> _claims = new();
        private readonly ConcurrentDictionary<int, System.Threading.SemaphoreSlim> _killLocks = new();

        public GameEventHub(ILogger<GameEventHub> logger)
        {
            _logger = logger;
        }

        public void Register(GameCode code, BotAgent agent)
        {
            _agents.GetOrAdd(code.Value, _ => new ConcurrentDictionary<BotAgent, byte>())[agent] = 0;
        }

        public void Unregister(GameCode code, BotAgent agent)
        {
            if (_agents.TryGetValue(code.Value, out var set))
            {
                set.TryRemove(agent, out _);
            }
        }

        /// <summary>
        ///     Gets the lock that makes kills in one game happen one at a time, so two impostor bots never both
        ///     "kill" the same victim.
        /// </summary>
        /// <param name="code">The game.</param>
        /// <returns>The lock of that game.</returns>
        public System.Threading.SemaphoreSlim KillLock(GameCode code) => _killLocks.GetOrAdd(code.Value, _ => new System.Threading.SemaphoreSlim(1, 1));

        public IReadOnlyList<BotAgent> AgentsOf(GameCode code)
        {
            return _agents.TryGetValue(code.Value, out var set) ? set.Keys.ToList() : new List<BotAgent>();
        }

        /// <summary>
        ///     Claims a repair console for a bot; the first bot to ask gets it.
        /// </summary>
        /// <param name="code">The game.</param>
        /// <param name="system">The sabotaged system.</param>
        /// <param name="console">The console number.</param>
        /// <param name="agent">The bot that wants it.</param>
        /// <returns>True when the console is now this bot's.</returns>
        public bool TryClaimConsole(GameCode code, int system, int console, BotAgent agent)
        {
            var owner = _claims.GetOrAdd((code.Value, system, console), agent);
            return owner == agent;
        }

        public void ReleaseConsole(GameCode code, int system, int console, BotAgent agent)
        {
            var key = (code.Value, system, console);
            if (_claims.TryGetValue(key, out var owner) && owner == agent)
            {
                _claims.TryRemove(key, out _);
            }
        }

        public void ReleaseConsoles(GameCode code, BotAgent agent)
        {
            foreach (var pair in _claims.Where(c => c.Key.Code == code.Value && c.Value == agent).ToList())
            {
                _claims.TryRemove(pair.Key, out _);
            }
        }

        public IReadOnlyList<BodyRecord> BodiesOf(GameCode code)
        {
            if (_bodies.TryGetValue(code.Value, out var list))
            {
                lock (list)
                {
                    return list.ToList();
                }
            }

            return Array.Empty<BodyRecord>();
        }

        [EventListener]
        public void OnMurder(IPlayerMurderEvent e)
        {
            if (e.Result.HasFlag(Impostor.Api.Innersloth.MurderResultFlags.FailedProtected) || e.Result.HasFlag(Impostor.Api.Innersloth.MurderResultFlags.FailedError))
            {
                return;
            }

            var victim = Info(e.Victim);
            var killer = Info(e.PlayerControl);
            if (victim == null || killer == null)
            {
                return;
            }

            var position = (e.Victim as InnerPlayerControl)?.NetworkTransform.Position ?? Vector2.Zero;
            var bodies = _bodies.GetOrAdd(e.Game.Code.Value, _ => new List<BodyRecord>());
            lock (bodies)
            {
                bodies.Add(new BodyRecord(victim.PlayerId, position, DateTime.UtcNow));
            }

            Dispatch(e.Game, snap => new MurderWorldEvent(DateTime.UtcNow, snap, killer.PlayerId, victim.PlayerId, position));
        }

        [EventListener]
        public void OnEnterVent(IPlayerEnterVentEvent e)
        {
            if (Info(e.PlayerControl) is { } info)
            {
                Dispatch(e.Game, snap => new VentWorldEvent(DateTime.UtcNow, snap, info.PlayerId, true, e.Vent.Name, e.Vent.Position));
            }
        }

        [EventListener]
        public void OnExitVent(IPlayerExitVentEvent e)
        {
            if (Info(e.PlayerControl) is { } info)
            {
                Dispatch(e.Game, snap => new VentWorldEvent(DateTime.UtcNow, snap, info.PlayerId, false, e.Vent.Name, e.Vent.Position));
            }
        }

        /// <summary>
        ///     Gets or sets the handler of "!bots ..." chat commands typed by a lobby host. Returns the reply.
        /// </summary>
        public Func<Game, ClientPlayerRef, string, Task<string>>? ChatCommand { get; set; }

        [EventListener]
        public void OnChat(IPlayerChatEvent e)
        {
            if (ChatCommand != null && e.Game is Game game && game.GameState == Impostor.Api.Innersloth.GameStates.NotStarted && e.Message.StartsWith("!bots", StringComparison.OrdinalIgnoreCase))
            {
                if (e.ClientPlayer.IsHost)
                {
                    e.IsCancelled = true;
                    var sender = new ClientPlayerRef(e.ClientPlayer.Client.Id, true);
                    _ = Task.Run(async () => await RunCommandAsync(game, sender, e.Message));
                }

                return;
            }

            if (Info(e.PlayerControl) is { } info)
            {
                Dispatch(e.Game, snap => new ChatWorldEvent(DateTime.UtcNow, snap, info.PlayerId, e.Message));
            }
        }

        private async Task RunCommandAsync(Game game, ClientPlayerRef sender, string message)
        {
            try
            {
                var reply = await ChatCommand!(game, sender, message);
                if (game.Host?.Character is { } hostControl)
                {
                    // Only the host sees the answer. Prefer a bot as the speaker, else the host's own character.
                    var speaker = game.Players.Select(p => p.Character).FirstOrDefault(c => c != null && c != hostControl) ?? hostControl;
                    await ((InnerPlayerControl)speaker).SendChatToPlayerAsync(reply, hostControl);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat command failed");
            }
        }

        [EventListener]
        public void OnStartMeeting(IPlayerStartMeetingEvent e)
        {
            var reporter = Info(e.PlayerControl)?.PlayerId;
            var body = e.Body == null ? null : Info(e.Body)?.PlayerId;
            Vector2? bodyPosition = null;

            if (_bodies.TryGetValue(e.Game.Code.Value, out var bodies))
            {
                lock (bodies)
                {
                    var record = bodies.FirstOrDefault(b => b.VictimId == body);
                    if (record != null)
                    {
                        bodyPosition = record.Position;
                    }

                    bodies.Clear();
                }
            }

            Dispatch(e.Game, snap => new MeetingStartedWorldEvent(DateTime.UtcNow, snap, reporter, body, bodyPosition));
        }

        [EventListener]
        public void OnMeetingEnded(IMeetingEndedEvent e)
        {
            var votes = new List<(byte, byte)>();
            if (e.MeetingHud is InnerMeetingHud hud)
            {
                foreach (var area in hud.VoteAreas)
                {
                    if (area.IsDead || !area.DidVote)
                    {
                        continue;
                    }

                    var target = area.VoteType == VoteType.Player ? area.VotedForId : (byte)253;
                    votes.Add((area.TargetPlayer.PlayerId, target));
                }
            }

            var exiled = e.Exiled == null ? null : Info(e.Exiled)?.PlayerId;
            Dispatch(e.Game, snap => new MeetingEndedWorldEvent(DateTime.UtcNow, snap, exiled, e.IsTie, votes));
        }

        [EventListener]
        public void OnGameStarted(IGameStartedEvent e)
        {
            _bodies.TryRemove(e.Game.Code.Value, out _);
            Dispatch(e.Game, snap => new GameStartedWorldEvent(DateTime.UtcNow, snap));
        }

        [EventListener]
        public void OnGameEnded(IGameEndedEvent e)
        {
            _bodies.TryRemove(e.Game.Code.Value, out _);
            Dispatch(e.Game, snap => new GameEndedWorldEvent(DateTime.UtcNow, snap, e.GameOverReason.ToString()));
        }

        [EventListener]
        public void OnGameDestroyed(IGameDestroyedEvent e)
        {
            _agents.TryRemove(e.Game.Code.Value, out _);
            _bodies.TryRemove(e.Game.Code.Value, out _);
            _killLocks.TryRemove(e.Game.Code.Value, out _);
        }

        private static InnerPlayerInfo? Info(Impostor.Api.Net.Inner.Objects.IInnerPlayerControl? control) => (control as InnerPlayerControl)?.PlayerInfo;

        private static IReadOnlyList<PlayerPos> Snapshot(IGame game)
        {
            var result = new List<PlayerPos>();
            foreach (var player in game.Players)
            {
                if (player.Character is InnerPlayerControl control && control.PlayerInfo is { } info)
                {
                    result.Add(new PlayerPos(info.PlayerId, control.NetworkTransform.Position, info.IsDead));
                }
            }

            return result;
        }

        private void Dispatch(IGame game, Func<IReadOnlyList<PlayerPos>, WorldEvent> make)
        {
            if (!_agents.TryGetValue(game.Code.Value, out var agents) || agents.IsEmpty)
            {
                return;
            }

            try
            {
                var evt = make(Snapshot(game));
                foreach (var agent in agents.Keys)
                {
                    agent.Observe(evt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch a world event");
            }
        }
    }
}
