using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.Customization;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Api.Innersloth.Maps;
using Impostor.Api.Net;
using Impostor.Api.Net.Inner;
using Impostor.Api.Net.Messages;
using Impostor.Api.Net.Messages.C2S;
using Impostor.Api.Net.Messages.Rpcs;
using Impostor.Hazel.Abstractions;
using Impostor.Server.LlmBots;
using Impostor.Server.Net.Inner;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.LlmBots.Sim
{
    internal enum HostPhase
    {
        Connecting,
        Lobby,
        WaitingReady,
        Playing,
        Meeting,
        Ended,
    }

    /// <summary>
    ///     Stands in for a human host running the real Among Us client: it spawns the objects a host spawns,
    ///     assigns roles and tasks, runs meetings and ends the game. Everything is sent as protocol messages.
    /// </summary>
    internal sealed class ScriptedHost
    {
        private static readonly GameVersion HostVersion = new(2026, 7, 15); // Among Us 18.0 on PC

        private readonly BotEnvironment _env;
        private readonly ILogger _log;
        private readonly Random _rng;
        private readonly ConcurrentDictionary<int, PlayerObjects> _players = new();
        private readonly ConcurrentDictionary<int, byte> _ready = new();
        private readonly List<uint> _globalNetIds = new();
        private readonly ConcurrentQueue<(int Reporter, byte Body)> _reports = new();
        private readonly int _minPlayers;
        private uint _nextNetId = 1;
        private bool _startRequested;
        private DateTime _phaseSince = DateTime.UtcNow;
        private uint _meetingNetId;
        private DateTime _meetingStart;
        private int _meetingCount;
        private int _emergenciesUsed;
        private uint _shipNetId;
        private SabotageState? _sabotage;
        private (SystemTypes System, double Seconds)? _pendingSabotage;
        private DateTime _lastSabotageTick = DateTime.UtcNow;

        public ScriptedHost(BotEnvironment env, NormalGameOptions options, string name = "Host", int seed = 1, int minPlayers = 2)
        {
            _env = env;
            _rng = new Random(seed);
            _minPlayers = minPlayers;
            Options = options;
            Client = new BotClient(env, name);
            Client.Connection.ExemptFromAntiCheat = false;
            _log = env.LoggerFactory.CreateLogger("ScriptedHost");
        }

        public BotClient Client { get; }

        public NormalGameOptions Options { get; }

        public HostPhase Phase { get; private set; } = HostPhase.Connecting;

        public GameCode Code { get; private set; }

        public Game? Game => Client.Game;

        public GameOverReason? LastResult { get; private set; }

        /// <summary>Gets every chat line the host client received (lobby and meetings).</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> ChatLog { get; } = new();

        public int GamesFinished { get; private set; }

        public int Meetings => _meetingCount;

        /// <summary>
        ///     Gets or sets the hook that runs the host's own character (a bot agent).
        /// </summary>
        public Func<BotEvent, ValueTask>? AgentEvent { get; set; }

        public Func<ValueTask>? AgentTick { get; set; }

        public Action<GameOverReason>? GameOver { get; set; }

        public async Task<GameCode> CreateLobbyAsync()
        {
            if (!await Client.ConnectAsync(HostVersion))
            {
                throw new InvalidOperationException("Host could not connect");
            }

            await Client.SendRootAsync(w => Message00HostGameC2S.Serialize(w, Options, CrossplayFlags.All, GameFilterOptions.CreateDefault()));

            var events = Client.DrainInbox();
            var hosted = events.FirstOrDefault(e => e.Kind == BotEventKind.HostedGame);
            if (hosted.Kind != BotEventKind.HostedGame)
            {
                throw new InvalidOperationException("Server did not answer HostGame");
            }

            Code = GameCode.From(hosted.A);
            await Client.JoinAsync(Code);
            foreach (var evt in Client.DrainInbox())
            {
                await HandleEventAsync(evt);
            }

            Phase = HostPhase.Lobby;
            return Code;
        }

        /// <summary>
        ///     The host's own player presses the emergency button.
        /// </summary>
        public void RequestEmergency() => _reports.Enqueue((Client.ClientId, byte.MaxValue));

        /// <summary>Gets or sets a value indicating whether the meeting object is spawned before the StartMeeting announcement.</summary>
        public bool AnnounceAfterSpawn { get; set; }

        /// <summary>Gets or sets task ids every player gets instead of a random selection (for tests).</summary>
        public int[]? ForcedTasks { get; set; }

        /// <summary>Gets a value indicating whether the last meeting ended because everybody had voted (not because time ran out).</summary>
        public bool LastMeetingAllVoted { get; private set; }

        /// <summary>Gets the number of scanner RPCs the host client saw.</summary>
        public int ScannerRpcs { get; private set; }

        /// <summary>Gets the number of play-animation RPCs the host client saw.</summary>
        public int AnimationRpcs { get; private set; }

        /// <summary>Gets the number of sabotages started by impostors.</summary>
        public int SabotagesStarted { get; private set; }

        /// <summary>Gets a value indicating whether a sabotage was repaired by the players.</summary>
        public bool SabotageFixed { get; private set; }

        /// <summary>
        ///     A human impostor sabotaged the reactor or O2: the host starts the timer.
        /// </summary>
        /// <param name="system">Reactor or LifeSupp.</param>
        /// <param name="seconds">Time until the crew loses.</param>
        public void StartSabotage(SystemTypes system, double seconds) => _pendingSabotage = (system, seconds);

        /// <summary>
        ///     The human pressed the Start button.
        /// </summary>
        public void RequestStart() => _startRequested = true;

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && Client.IsConnected)
            {
                try
                {
                    foreach (var evt in Client.DrainInbox())
                    {
                        await HandleEventAsync(evt);
                    }

                    await TickAsync();

                    if (AgentTick != null)
                    {
                        await AgentTick();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Host loop error");
                }

                try
                {
                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static byte SkipVote => 253;

        private async ValueTask HandleEventAsync(BotEvent evt)
        {
            if (AgentEvent != null)
            {
                await AgentEvent(evt);
            }

            switch (evt.Kind)
            {
                case BotEventKind.Joined:
                    await OnHostJoinedAsync();
                    break;

                case BotEventKind.SceneChanged when evt.A != Client.ClientId:
                    await SpawnCharacterAsync(evt.A);
                    break;

                case BotEventKind.ClientReady:
                    _ready[evt.A] = 1;
                    break;

                case BotEventKind.Rpc:
                    await HandleRpcAsync(evt);
                    break;

                case BotEventKind.Removed when evt.A != Client.ClientId:
                    _players.TryRemove(evt.A, out _);
                    break;
            }
        }

        private async ValueTask OnHostJoinedAsync()
        {
            _players.Clear();
            _ready.Clear();
            _globalNetIds.Clear();
            Phase = HostPhase.Lobby;
            _phaseSince = DateTime.UtcNow;

            await Client.SendSceneChangeAsync();

            _globalNetIds.Add(await SpawnAsync(2, -2, 0, (w => { })));
            _globalNetIds.Add(await SpawnAsync(10, -2, 0, (w => { })));
            _globalNetIds.Add(await SpawnAsync(12, -2, 0, (w => w.Write((byte)0))));

            await SpawnCharacterAsync(Client.ClientId);
        }

        private async ValueTask<uint> SpawnAsync(uint objectId, int owner, byte flags, params Action<IMessageWriter>[] components)
        {
            var first = _nextNetId;
            var ids = components.Select(_ => _nextNetId++).ToArray();
            await SendSpawnAsync(objectId, owner, flags, ids, components);
            return first;
        }

        private async ValueTask SendSpawnAsync(uint objectId, int owner, byte flags, uint[] ids, Action<IMessageWriter>[] data)
        {
            await Client.SendGameDataAsync(w =>
            {
                w.StartMessage(GameDataTag.SpawnFlag);
                w.WritePacked(objectId);
                w.WritePacked(owner);
                w.Write(flags);
                w.WritePacked(ids.Length);
                for (var i = 0; i < ids.Length; i++)
                {
                    w.WritePacked(ids[i]);
                    w.StartMessage(1);
                    data[i](w);
                    w.EndMessage();
                }

                w.EndMessage();
            });
        }

        private async ValueTask SpawnCharacterAsync(int clientId)
        {
            var game = Game;
            if (game == null || _players.ContainsKey(clientId))
            {
                return;
            }

            if (!game.GameNet.GameData.PlayersByClientId.TryGetValue(clientId, out var info))
            {
                _log.LogWarning("No PlayerInfo for client {Client} yet", clientId);
                return;
            }

            var objs = new PlayerObjects
            {
                ClientId = clientId,
                PlayerId = info.PlayerId,
                ControlNetId = _nextNetId++,
                PhysicsNetId = _nextNetId++,
                CntNetId = _nextNetId++,
            };
            _players[clientId] = objs;

            var lobbySpawn = new Vector2(-1.5f + (info.PlayerId * 0.3f), -1.3f);
            await SendSpawnAsync(
                4,
                clientId,
                (byte)SpawnFlags.IsClientCharacter,
                new[] { objs.ControlNetId, objs.PhysicsNetId, objs.CntNetId },
                new Action<IMessageWriter>[]
                {
                    w =>
                    {
                        w.Write(true);
                        w.Write(info.PlayerId);
                    },
                    _ => { },
                    w =>
                    {
                        w.Write((ushort)0);
                        w.Write(lobbySpawn);
                    },
                });
        }

        private async ValueTask HandleRpcAsync(BotEvent evt)
        {
            var netId = (uint)evt.A;
            var call = (RpcCalls)evt.B;
            var owner = _players.Values.FirstOrDefault(p => p.ControlNetId == netId);
            var game = Game;
            if (game == null)
            {
                return;
            }

            using var reader = _env.ReaderPool.Get();
            var payload = evt.Data ?? Array.Empty<byte>();
            reader.Update(payload);

            switch (call)
            {
                case RpcCalls.CheckName when owner != null:
                {
                    var name = reader.ReadString();
                    var unique = name;
                    var i = 1;
                    while (game.GameNet.GameData.Players.Values.Any(p => p.ClientId != owner.ClientId && p.PlayerName == unique))
                    {
                        unique = $"{name} {i++}";
                    }

                    var info = game.GameNet.GameData.PlayersByClientId[owner.ClientId];
                    await Client.SendRpcAsync(owner.ControlNetId, RpcCalls.SetName, w => Rpc06SetName.Serialize(w, info.NetId, unique));
                    break;
                }

                case RpcCalls.CheckColor when owner != null:
                {
                    var requested = reader.ReadByte();
                    var count = Enum.GetValues<ColorType>().Length;
                    var color = requested;
                    for (var i = 0; i < count; i++)
                    {
                        var candidate = (byte)((requested + i) % count);
                        var used = game.GameNet.GameData.Players.Values.Any(p => p.ClientId != owner.ClientId && p.Controller != null && (byte)p.CurrentOutfit.Color == candidate);
                        if (!used)
                        {
                            color = candidate;
                            break;
                        }
                    }

                    var info = game.GameNet.GameData.PlayersByClientId[owner.ClientId];
                    await Client.SendRpcAsync(owner.ControlNetId, RpcCalls.SetColor, w => Rpc08SetColor.Serialize(w, info.NetId, (ColorType)color));
                    break;
                }

                case RpcCalls.SendChat:
                    ChatLog.Enqueue(reader.ReadString());
                    break;

                case RpcCalls.SetScanner:
                    ScannerRpcs++;
                    break;

                case RpcCalls.PlayAnimation:
                    AnimationRpcs++;
                    break;

                case RpcCalls.UpdateSystem when netId == _shipNetId && _sabotage == null && _pendingSabotage == null:
                {
                    var system = (SystemTypes)reader.ReadByte();
                    var playerNetId = reader.ReadPackedUInt32();
                    reader.ReadUInt16();
                    var amount = reader.ReadByte();
                    var who = _players.Values.FirstOrDefault(p => p.ControlNetId == playerNetId);
                    if (system == SystemTypes.Sabotage && who is { Role: RoleTypes.Impostor } && ((SystemTypes)amount is SystemTypes.Reactor or SystemTypes.LifeSupp))
                    {
                        SabotagesStarted++;
                        _pendingSabotage = ((SystemTypes)amount, 60);
                    }

                    break;
                }

                case RpcCalls.UpdateSystem when netId == _shipNetId && _sabotage != null:
                {
                    var system = (SystemTypes)reader.ReadByte();
                    var playerNetId = reader.ReadPackedUInt32();
                    reader.ReadUInt16();
                    var amount = reader.ReadByte();
                    var who = _players.Values.FirstOrDefault(p => p.ControlNetId == playerNetId);
                    if (who != null && system == _sabotage.System)
                    {
                        var console = (byte)(amount & 3);
                        if (_sabotage.IsO2)
                        {
                            if ((amount & 0x40) != 0)
                            {
                                _sabotage.Done.Add(console);
                            }
                        }
                        else if ((amount & 0x40) != 0)
                        {
                            _sabotage.Pairs.Add((who.PlayerId, console));
                        }
                        else if ((amount & 0x20) != 0)
                        {
                            _sabotage.Pairs.Remove((who.PlayerId, console));
                        }
                    }

                    break;
                }

                case RpcCalls.ReportDeadBody when owner != null:
                    _reports.Enqueue((owner.ClientId, reader.ReadByte()));
                    break;
            }
        }

        private async ValueTask TickAsync()
        {
            var game = Game;
            if (game == null)
            {
                return;
            }

            switch (Phase)
            {
                case HostPhase.Lobby:
                    if (_startRequested && game.PlayerCount >= _minPlayers && game.Players.All(p => p.Character != null))
                    {
                        _startRequested = false;
                        await Client.SendRootAsync(w =>
                        {
                            w.StartMessage(MessageFlags.StartGame);
                            w.Write(Code.Value);
                            w.EndMessage();
                        });
                        _ready.Clear();
                        _ready[Client.ClientId] = 1;
                        Phase = HostPhase.WaitingReady;
                        _phaseSince = DateTime.UtcNow;
                    }

                    break;

                case HostPhase.WaitingReady:
                {
                    var everyoneReady = game.Players.All(p => _ready.ContainsKey(p.Client.Id));
                    if (everyoneReady || DateTime.UtcNow - _phaseSince > TimeSpan.FromSeconds(8))
                    {
                        await BeginGameAsync(game);
                    }

                    break;
                }

                case HostPhase.Playing:
                    await TickPlayingAsync(game);
                    break;

                case HostPhase.Meeting:
                    await TickMeetingAsync(game);
                    break;
            }
        }

        private async ValueTask BeginGameAsync(Game game)
        {
            _emergenciesUsed = 0;

            var shipObjectId = Options.Map switch
            {
                MapTypes.Skeld => 0u,
                MapTypes.MiraHQ => 5u,
                MapTypes.Polus => 6u,
                MapTypes.Dleks => 7u,
                MapTypes.Airship => 8u,
                MapTypes.Fungle => 13u,
                _ => 0u,
            };

            _shipNetId = await SpawnAsync(shipObjectId, -2, 0, (w => { }));
            _globalNetIds.Add(_shipNetId);

            var players = _players.Values.OrderBy(p => p.PlayerId).ToList();
            var impostorCount = Math.Max(1, Math.Min(Options.NumImpostors, (players.Count - 1) / 2));
            var impostors = players.OrderBy(_ => _rng.Next()).Take(impostorCount).Select(p => p.ClientId).ToHashSet();

            var map = MapData.Maps[Options.Map];
            var common = map.Tasks.Values.Where(t => t.Category == TaskCategories.CommonTask).OrderBy(_ => _rng.Next()).Take(Options.NumCommonTasks).ToList();

            foreach (var p in players)
            {
                p.Role = impostors.Contains(p.ClientId) ? RoleTypes.Impostor : RoleTypes.Crewmate;
                await Client.SendRpcAsync(p.ControlNetId, RpcCalls.SetRole, w => Rpc44SetRole.Serialize(w, p.Role, true));
            }

            foreach (var p in players)
            {
                var tasks = new List<byte>();
                if (ForcedTasks != null)
                {
                    tasks.AddRange(ForcedTasks.Select(t => (byte)t));
                }
                else
                {
                tasks.AddRange(common.Select(t => (byte)t.Id));
                tasks.AddRange(map.Tasks.Values.Where(t => t.Category == TaskCategories.LongTask).OrderBy(_ => _rng.Next()).Take(Options.NumLongTasks).Select(t => (byte)t.Id));
                tasks.AddRange(map.Tasks.Values.Where(t => t.Category == TaskCategories.ShortTask).OrderBy(_ => _rng.Next()).Take(Options.NumShortTasks).Select(t => (byte)t.Id));
                }

                var info = game.GameNet.GameData.PlayersByClientId[p.ClientId];
                await Client.SendRpcAsync(info.NetId, RpcCalls.SetTasks, w => w.WriteBytesAndSize(tasks.ToArray()));
            }

            Phase = HostPhase.Playing;
            _phaseSince = DateTime.UtcNow;
            _log.LogInformation("Game started: {Players} players, impostors: {Impostors}", players.Count, string.Join(", ", players.Where(p => p.Role == RoleTypes.Impostor).Select(p => game.GameNet.GameData.PlayersByClientId[p.ClientId].PlayerName)));
        }

        private async ValueTask TickPlayingAsync(Game game)
        {
            if (game.GameState != GameStates.Started)
            {
                return;
            }

            if (await CheckWinAsync(game))
            {
                return;
            }

            if (await TickSabotageAsync(game))
            {
                return;
            }

            while (_reports.TryDequeue(out var report))
            {
                if (_players.TryGetValue(report.Reporter, out var reporter) && await TryStartMeetingAsync(game, reporter, report.Body))
                {
                    return;
                }
            }
        }

        private async ValueTask<bool> TryStartMeetingAsync(Game game, PlayerObjects reporter, byte body)
        {
            if (game.ActiveMeeting != null)
            {
                return false;
            }

            var reporterInfo = game.GameNet.GameData.PlayersByClientId[reporter.ClientId];
            if (reporterInfo.IsDead)
            {
                return false;
            }

            if (body == byte.MaxValue)
            {
                if (_emergenciesUsed >= Options.NumEmergencyMeetings)
                {
                    return false;
                }

                _emergenciesUsed++;
            }
            else if (game.GameNet.GameData.GetPlayerById(body)?.IsDead != true)
            {
                _log.LogWarning("Report of body {Body} rejected: not dead", body);
                return false;
            }

            _meetingCount++;
            if (!AnnounceAfterSpawn)
            {
                await Client.SendRpcAsync(reporter.ControlNetId, RpcCalls.StartMeeting, w => Rpc14StartMeeting.Serialize(w, body));
            }

            _meetingNetId = _nextNetId++;
            var states = game.GameNet.GameData.Players.Values.OrderBy(p => p.Controller?.NetId).ToList();
            await SendSpawnAsync(
                1,
                -2,
                0,
                new[] { _meetingNetId },
                new Action<IMessageWriter>[]
                {
                    w =>
                    {
                        w.WritePacked((uint)states.Count);
                        foreach (var s in states)
                        {
                            w.StartMessage(s.PlayerId);
                            w.Write(s.IsDead || s.Disconnected ? (byte)252 : (byte)255);
                            w.Write(s.PlayerId == reporterInfo.PlayerId);
                            w.EndMessage();
                        }

                        w.WritePacked(0);
                    },
                });

            if (AnnounceAfterSpawn)
            {
                await Task.Delay(250);
                await Client.SendRpcAsync(reporter.ControlNetId, RpcCalls.StartMeeting, w => Rpc14StartMeeting.Serialize(w, body));
            }

            _meetingStart = DateTime.UtcNow;
            Phase = HostPhase.Meeting;
            _phaseSince = DateTime.UtcNow;
            _log.LogInformation("Meeting #{Count} started by {Reporter} ({Kind})", _meetingCount, reporterInfo.PlayerName, body == byte.MaxValue ? "emergency" : "body of " + game.GameNet.GameData.GetPlayerById(body)?.PlayerName);
            return true;
        }

        private async ValueTask TickMeetingAsync(Game game)
        {
            var meeting = game.ActiveMeeting;
            if (meeting == null)
            {
                Phase = HostPhase.Playing;
                return;
            }

            var areas = meeting.VoteAreas;
            var allVoted = areas.Count > 0 && areas.All(a => a.IsDead || a.DidVote);
            var limit = TimeSpan.FromSeconds(_env.Config.MeetingAnimationSeconds + Options.DiscussionTime + Options.VotingTime + 2);
            if (!allVoted && DateTime.UtcNow - _meetingStart < limit)
            {
                return;
            }

            LastMeetingAllVoted = allVoted;
            await FinishMeetingAsync(game, meeting);
        }

        private async ValueTask FinishMeetingAsync(Game game, InnerMeetingHud meeting)
        {
            var tally = new Dictionary<byte, int>();
            var votes = new List<(byte Voter, byte Choice)>();
            foreach (var area in meeting.VoteAreas)
            {
                if (area.IsDead)
                {
                    continue;
                }

                var choice = area.DidVote && area.VoteType != Impostor.Api.Events.Player.VoteType.Missed ? area.VotedForId : (byte)254;
                votes.Add((area.TargetPlayer.PlayerId, choice));
                if (choice < 200 || choice == SkipVote)
                {
                    tally[choice] = tally.GetValueOrDefault(choice) + 1;
                }
            }

            byte exiled = byte.MaxValue;
            var tie = true;
            if (tally.Count > 0)
            {
                var top = tally.Values.Max();
                var leaders = tally.Where(kv => kv.Value == top).ToList();
                if (leaders.Count == 1)
                {
                    tie = false;
                    if (leaders[0].Key != SkipVote)
                    {
                        exiled = leaders[0].Key;
                    }
                }
            }

            await Client.SendRpcAsync(_meetingNetId, RpcCalls.VotingComplete, w =>
            {
                w.WritePacked(votes.Count);
                foreach (var (voter, choice) in votes)
                {
                    w.StartMessage(voter);
                    w.Write(choice);
                    w.EndMessage();
                }

                w.Write(exiled);
                w.Write(tie);
                w.Write(false);
                w.Write((ushort)0);
            });

            if (exiled != byte.MaxValue && game.GameNet.GameData.GetPlayerById(exiled) is { } victim && victim.Controller != null)
            {
                var ghost = victim.IsImpostor ? RoleTypes.ImpostorGhost : RoleTypes.CrewmateGhost;
                _log.LogInformation("Exiled {Player} ({Role})", victim.PlayerName, victim.RoleType);
                await Client.SendRpcAsync(victim.Controller.NetId, RpcCalls.SetRole, w => Rpc44SetRole.Serialize(w, ghost, true));
            }
            else
            {
                _log.LogInformation("Nobody was exiled ({Reason})", tie ? "tie or skip" : "no votes");
            }

            await Client.SendRpcAsync(_meetingNetId, RpcCalls.CloseMeeting, w => { });
            await Client.SendGameDataAsync(w =>
            {
                w.StartMessage(GameDataTag.DespawnFlag);
                w.WritePacked(_meetingNetId);
                w.EndMessage();
            });

            Phase = HostPhase.Playing;
            _phaseSince = DateTime.UtcNow;
            await CheckWinAsync(game);
        }

        private async ValueTask SendSabotageStateAsync(SabotageState state, float countdown)
        {
            await Client.SendGameDataAsync(w =>
            {
                w.StartMessage(GameDataTag.DataFlag);
                w.WritePacked(_shipNetId);
                w.StartMessage((byte)state.System);
                w.Write(countdown);
                if (state.IsO2)
                {
                    w.WritePacked(state.Done.Count);
                    foreach (var id in state.Done)
                    {
                        w.WritePacked(id);
                    }
                }
                else
                {
                    w.WritePacked(state.Pairs.Count);
                    foreach (var (player, console) in state.Pairs)
                    {
                        w.Write(player);
                        w.Write(console);
                    }
                }

                w.EndMessage();
                w.EndMessage();
            });
        }

        private async ValueTask<bool> TickSabotageAsync(Game game)
        {
            var now = DateTime.UtcNow;
            var dt = (now - _lastSabotageTick).TotalSeconds;
            _lastSabotageTick = now;

            if (_pendingSabotage is { } pending && _sabotage == null)
            {
                _pendingSabotage = null;
                _sabotage = new SabotageState { System = pending.System, IsO2 = pending.System == SystemTypes.LifeSupp, Countdown = pending.Seconds };
                _log.LogInformation("Sabotage started: {System}, {Seconds}s on the clock", pending.System, pending.Seconds);
                await SendSabotageStateAsync(_sabotage, (float)_sabotage.Countdown);
                return false;
            }

            var sabotage = _sabotage;
            if (sabotage == null)
            {
                return false;
            }

            sabotage.Countdown -= dt;
            var fixedNow = sabotage.IsO2
                ? sabotage.Done.Contains(0) && sabotage.Done.Contains(1)
                : sabotage.Pairs.Any(a => a.Console == 0) && sabotage.Pairs.Any(b => b.Console == 1) && sabotage.Pairs.Select(p => p.Player).Distinct().Count() >= 2;

            if (fixedNow)
            {
                _log.LogInformation("Sabotage fixed with {Seconds:0.0}s left", sabotage.Countdown);
                sabotage.Pairs.Clear();
                sabotage.Done.Clear();
                await SendSabotageStateAsync(sabotage, 10000f);
                _sabotage = null;
                SabotageFixed = true;
                return false;
            }

            if (sabotage.Countdown <= 0)
            {
                _sabotage = null;
                await EndGameAsync(game, GameOverReason.ImpostorsBySabotage);
                return true;
            }

            if ((now - sabotage.LastSend).TotalMilliseconds >= 100)
            {
                sabotage.LastSend = now;
                await SendSabotageStateAsync(sabotage, (float)sabotage.Countdown);
            }

            return false;
        }

        private async ValueTask<bool> CheckWinAsync(Game game)
        {
            var infos = _players.Keys
                .Select(id => game.GameNet.GameData.PlayersByClientId.GetValueOrDefault(id))
                .Where(i => i != null && !i.Disconnected)
                .Select(i => i!)
                .ToList();

            if (infos.Count == 0 || infos.Any(i => i.RoleType == null))
            {
                return false;
            }

            var impostorsAlive = infos.Count(i => i.IsImpostor && !i.IsDead);
            var crewAlive = infos.Count(i => !i.IsImpostor && !i.IsDead);
            var crew = infos.Where(i => !i.IsImpostor).ToList();

            GameOverReason? result = null;
            if (impostorsAlive == 0)
            {
                result = GameOverReason.CrewmatesByVote;
            }
            else if (impostorsAlive >= crewAlive)
            {
                result = game.ActiveMeeting != null || Phase == HostPhase.Meeting ? GameOverReason.ImpostorsByVote : GameOverReason.ImpostorsByKill;
            }
            else if (crew.Count > 0 && crew.All(c => c.Tasks.Count > 0 && c.Tasks.All(t => t.Complete)))
            {
                result = GameOverReason.CrewmatesByTask;
            }

            if (result == null)
            {
                return false;
            }

            await EndGameAsync(game, result.Value);
            return true;
        }

        public async ValueTask EndGameAsync(Game game, GameOverReason reason)
        {
            _log.LogInformation("Game over: {Reason}", reason);
            LastResult = reason;
            GamesFinished++;

            await Client.SendRootAsync(w =>
            {
                w.StartMessage(MessageFlags.EndGame);
                w.Write(Code.Value);
                w.Write((byte)reason);
                w.Write(false);
                w.EndMessage();
            });

            // A real host client only removes its own character and the shared objects; every other client removes its own
            // character itself when it leaves the game over screen. Doing the same here keeps the tests honest.
            var netIds = _players.Values.Where(p => p.ClientId == Client.ClientId).SelectMany(p => new[] { p.ControlNetId, p.PhysicsNetId, p.CntNetId }).Concat(_globalNetIds).Concat(new[] { _meetingNetId }).Where(n => n != 0).ToList();
            await Client.SendGameDataAsync(w =>
            {
                foreach (var n in netIds)
                {
                    w.StartMessage(GameDataTag.DespawnFlag);
                    w.WritePacked(n);
                    w.EndMessage();
                }
            });

            _players.Clear();
            _globalNetIds.Clear();
            _meetingNetId = 0;
            Phase = HostPhase.Ended;
            _phaseSince = DateTime.UtcNow;
            GameOver?.Invoke(reason);
        }

        private sealed class SabotageState
        {
            public SystemTypes System { get; init; }

            public bool IsO2 { get; init; }

            public double Countdown { get; set; }

            public HashSet<(byte Player, byte Console)> Pairs { get; } = new();

            public HashSet<int> Done { get; } = new();

            public DateTime LastSend { get; set; }
        }

        private sealed class PlayerObjects
        {
            public int ClientId { get; init; }

            public byte PlayerId { get; init; }

            public uint ControlNetId { get; init; }

            public uint PhysicsNetId { get; init; }

            public uint CntNetId { get; init; }

            public RoleTypes Role { get; set; }
        }
    }
}
