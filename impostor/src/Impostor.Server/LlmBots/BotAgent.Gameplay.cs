using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api;
using Impostor.Api.Innersloth;
using Impostor.Api.Net.Inner;
using Impostor.Api.Net.Messages.Rpcs;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Server.LlmBots.Maps;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    internal sealed partial class BotAgent
    {
        private enum Mode
        {
            Idle,
            Walking,
            Working,
        }

        private readonly List<Vector2> _path = new();
        private readonly HashSet<uint> _fakeDone = new();
        private BotMap? _map;
        private Vector2 _pos;
        private bool _inGame;
        private Mode _mode;
        private int _pathIndex;
        private Func<ValueTask>? _onArrive;
        private Func<ValueTask>? _onWorkDone;
        private DateTime _workUntil;
        private DateTime _lastStepAt = DateTime.UtcNow;
        private DateTime _killReadyAt;
        private DateTime _frozenUntil;
        private DateTime _idleUntil;
        private DateTime _reportStartedAt;
        private DateTime? _selfReportAt;
        private byte? _selfReportVictim;
        private byte? _reportingBody;
        private int _emergenciesUsed;
        private TaskRun? _task;
        private string _activity = "idle";
        private bool _scanning;
        private byte _scannerCount;

        /// <summary>Gets or sets the hub that tells this agent what happens in the game.</summary>
        public GameEventHub? Hub { get; set; }

        /// <summary>Gets a short description of what the bot is doing, for status pages.</summary>
        public string Activity => _activity;

        public Vector2 Position => _pos;

        public bool InGame => _inGame;

        private float Speed(NormalGameOptions options) => 2.5f * options.PlayerSpeedMod * (float)_env.Config.TimeScale;

        private TimeSpan Scaled(double seconds) => TimeSpan.FromSeconds(seconds / Math.Max(0.01, _env.Config.TimeScale));

        partial void OnGameStarting()
        {
        }

        partial void OnGameEnded()
        {
            _inGame = false;
            _mode = Mode.Idle;
            _task = null;
            _meeting = null;
        }

        private async ValueTask TickGameplayAsync(Game game, DateTime now)
        {
            var me = Client.Me;
            var info = Client.MyInfo;
            if (me == null || info == null)
            {
                return;
            }

            if (game.GameState != GameStates.Started || info.RoleType == null)
            {
                if (_inGame && game.GameState != GameStates.Starting)
                {
                    _inGame = false;
                }

                return;
            }

            if (game.Options is not NormalGameOptions options)
            {
                return;
            }

            if (!_inGame)
            {
                await BeginGameAsync(game, me, info, options, now);
            }

            ProcessWorldEvents(game, info, now);

            if (_scanning && _mode != Mode.Working)
            {
                await EndVisualAsync();
            }

            var meeting = game.ActiveMeeting;
            if (meeting != null)
            {
                await MeetingTickAsync(game, me, info, options, meeting, now);
                return;
            }

            if (_meeting != null)
            {
                await EndMeetingAsync(game, info, options, now);
            }

            if (now < _frozenUntil)
            {
                return;
            }

            var dt = (float)Math.Min(0.5, (now - _lastStepAt).TotalSeconds);
            _lastStepAt = now;

            if (!info.IsDead)
            {
                UpdateVision(game, me, info, now);
            }

            if (info.IsDead && info.IsImpostor)
            {
                _activity = "haunting (dead impostor)";
                return;
            }

            if (!info.IsDead)
            {
                if (_selfReportAt != null && now >= _selfReportAt && _selfReportVictim is { } victim)
                {
                    _selfReportAt = null;
                    _selfReportVictim = null;
                    _log.LogInformation("Self-reporting the body of {Victim}", NameOf(game, victim));
                    await Client.ReportBodyAsync(victim);
                    Memory.HandledBodies.Add(victim);
                    return;
                }

                if (info.IsImpostor && await TryKillAsync(game, me, info, options, now))
                {
                    return;
                }

                if (info.IsImpostor)
                {
                    await MaybeSabotageAsync(game, info, options, now);
                }

                if (await TryReportBodyAsync(game, info, now))
                {
                    await AdvanceAsync(dt, now, options);
                    return;
                }

                if (!info.IsImpostor && await TrySabotageDutyAsync(game, now))
                {
                    await AdvanceAsync(dt, now, options);
                    return;
                }

                if (!info.IsImpostor && await TryEmergencyAsync(game, info, options, now))
                {
                    await AdvanceAsync(dt, now, options);
                    return;
                }
            }

            await AdvanceAsync(dt, now, options);

            if (_mode == Mode.Idle && now >= _idleUntil)
            {
                await ChooseNextActivityAsync(game, me, info, options, now);
            }
        }

        private async ValueTask BeginGameAsync(Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            _map = BotMap.Get(options.Map);
            while (_worldEvents.TryDequeue(out _))
            {
                // Chat from the lobby and other old news is not part of this game.
            }

            Memory.Reset(now);
            Route.Clear();
            _fakeDone.Clear();
            _task = null;
            _mode = Mode.Idle;
            _meeting = null;
            _emergenciesUsed = 0;
            _reportingBody = null;
            _selfReportAt = null;
            _lastStepAt = now;
            _idleUntil = now;

            var spawn = game.GameNet.ShipStatus?.GetSpawnLocation(me, game.PlayerCount, true) ?? _map.SpawnCenter;
            _pos = spawn;
            await Client.SnapToAsync(spawn);

            _killReadyAt = now + TimeSpan.FromSeconds(Math.Max(1, options.KillCooldown));
            _sabotageReadyAt = now + Scaled(45);
            _frozenUntil = now + Scaled(3);
            _inGame = true;

            _log.LogInformation(
                "Game begins: I am {Role} on {Map} at {Room}, {Tasks} tasks",
                info.RoleType,
                _map.Name,
                _map.RoomAt(spawn),
                info.Tasks.Count);
        }

        // ---------------------------------------------------------------- movement

        private void GoTo(Vector2 destination, string activity, Func<ValueTask>? onArrive = null)
        {
            if (_map == null)
            {
                return;
            }

            _path.Clear();
            _path.AddRange(_map.Nav.FindPath(_pos, destination));
            _pathIndex = 1;
            _mode = Mode.Walking;
            _onArrive = onArrive;
            _activity = activity;
        }

        private void StartWork(double seconds, string activity, Func<ValueTask>? onDone)
        {
            _mode = Mode.Working;
            _workUntil = DateTime.UtcNow + Scaled(seconds * (0.85 + (_rng.NextDouble() * 0.3)));
            _onWorkDone = onDone;
            _activity = activity;
        }

        private void StopMoving()
        {
            _mode = Mode.Idle;
            _onArrive = null;
            _onWorkDone = null;
        }

        private async ValueTask AdvanceAsync(float dt, DateTime now, NormalGameOptions options)
        {
            if (_mode == Mode.Walking)
            {
                var budget = Speed(options) * dt;
                var moved = false;
                while (budget > 0 && _pathIndex < _path.Count)
                {
                    var target = _path[_pathIndex];
                    var distance = Vector2.Distance(_pos, target);
                    if (distance <= budget)
                    {
                        _pos = target;
                        budget -= distance;
                        _pathIndex++;
                        moved = moved || distance > 0;
                    }
                    else
                    {
                        _pos += Vector2.Normalize(target - _pos) * budget;
                        budget = 0;
                        moved = true;
                    }
                }

                if (moved)
                {
                    await Client.SendPositionAsync(_pos);
                }

                if (_pathIndex >= _path.Count)
                {
                    _mode = Mode.Idle;
                    var callback = _onArrive;
                    _onArrive = null;
                    if (callback != null)
                    {
                        await callback();
                    }
                }
            }
            else if (_mode == Mode.Working && now >= _workUntil)
            {
                _mode = Mode.Idle;
                var callback = _onWorkDone;
                _onWorkDone = null;
                if (callback != null)
                {
                    await callback();
                }
            }
        }

        // ---------------------------------------------------------------- tasks and idling

        private async ValueTask ChooseNextActivityAsync(Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            if (_map == null)
            {
                return;
            }

            if (info.IsDead && !info.IsImpostor && !options.GhostsDoTasks)
            {
                _activity = "haunting (ghost, no ghost tasks)";
                _idleUntil = now + TimeSpan.FromSeconds(5);
                return;
            }

            if (info.IsImpostor && now >= _killReadyAt && TryStartHunting(game, info, now))
            {
                return;
            }

            var pending = info.Tasks.Where(t => (info.IsImpostor ? !_fakeDone.Contains(t.Id) : !t.Complete) && t.Task != null).ToList();
            if (pending.Count > 0)
            {
                var chosen = pending
                    .Select(t => (Task: t, Cost: TaskCost(t) * (0.7 + (_rng.NextDouble() * 0.6))))
                    .OrderBy(x => x.Cost)
                    .First().Task;
                StartTask(chosen, info);
                return;
            }

            // Nothing left to do: hang around somewhere.
            var rooms = _map.Rooms.ToList();
            var room = rooms[_rng.Next(rooms.Count)];
            var hub = _map.RoomHub(room);
            GoTo(hub, $"wandering to {BotMap.DisplayName(room)}", () =>
            {
                _idleUntil = DateTime.UtcNow + Scaled(3 + (_rng.NextDouble() * 6));
                _activity = $"standing in {BotMap.DisplayName(room)}";
                return default;
            });
        }

        private double TaskCost(TaskInfo task)
        {
            if (_map == null || task.Task == null || !_map.Tasks.TryGetValue(task.Task.Id, out var spec))
            {
                return 1000;
            }

            var first = spec.Consoles.Count > 0 ? spec.Consoles.Min(c => Vector2.Distance(_pos, c.Position)) : 20;
            return first;
        }

        private void StartTask(TaskInfo task, InnerPlayerInfo info)
        {
            if (_map == null || task.Task == null || !_map.Tasks.TryGetValue(task.Task.Id, out var spec))
            {
                _fakeDone.Add(task.Id);
                return;
            }

            var stops = TaskPlanner.Plan(_map, spec, _rng);
            _task = new TaskRun(task, stops);
            GoToNextStop(info);
        }

        private void GoToNextStop(InnerPlayerInfo info)
        {
            var run = _task;
            if (run == null || run.Index >= run.Stops.Count)
            {
                return;
            }

            var stop = run.Stops[run.Index];
            GoTo(stop.Position, $"walking to {BotMap.DisplayName(stop.Room)} for {stop.Label}", async () =>
            {
                await BeginVisualAsync(info, run);
                StartWork(stop.Seconds, $"doing {stop.Label} in {BotMap.DisplayName(stop.Room)}", async () =>
                {
                    await EndVisualAsync();
                    run.Index++;
                    if (run.Index >= run.Stops.Count)
                    {
                        await FinishTaskAsync(info, run);
                    }
                    else
                    {
                        GoToNextStop(info);
                    }
                });
            });
        }

        /// <summary>
        ///     Crewmates doing a visual task show the animation other players can see (the MedBay scan, the asteroids),
        ///     so watching humans can vouch for them. Impostors cannot, just like in the real game.
        /// </summary>
        /// <param name="info">Our player info.</param>
        /// <param name="run">The task being done.</param>
        /// <returns>A task that finishes when the animation was started.</returns>
        private async ValueTask BeginVisualAsync(InnerPlayerInfo info, TaskRun run)
        {
            var me = Client.Me;
            var type = run.Info.Task?.Type;
            if (me == null || type == null || info.IsImpostor || info.IsDead || Client.Game?.Options is not NormalGameOptions { VisualTasks: true })
            {
                return;
            }

            if (type == TaskTypes.SubmitScan)
            {
                _scanning = true;
                _scannerCount++;
                await Client.SendRpcAsync(me.NetId, RpcCalls.SetScanner, w => Rpc15SetScanner.Serialize(w, true, _scannerCount));
            }
            else if (type == TaskTypes.ClearAsteroids)
            {
                await Client.SendRpcAsync(me.NetId, RpcCalls.PlayAnimation, w => Rpc00PlayAnimation.Serialize(w, TaskTypes.ClearAsteroids));
            }
        }

        private async ValueTask EndVisualAsync()
        {
            var me = Client.Me;
            if (_scanning && me != null)
            {
                _scanning = false;
                await Client.SendRpcAsync(me.NetId, RpcCalls.SetScanner, w => Rpc15SetScanner.Serialize(w, false, _scannerCount));
            }
        }

        private async ValueTask FinishTaskAsync(InnerPlayerInfo info, TaskRun run)
        {
            _task = null;
            if (info.IsImpostor)
            {
                _fakeDone.Add(run.Info.Id);
                _idleUntil = DateTime.UtcNow + Scaled(1 + (_rng.NextDouble() * 3));
                return;
            }

            await Client.CompleteTaskAsync(run.Info.Id);
            _idleUntil = DateTime.UtcNow + Scaled(0.5 + (_rng.NextDouble() * 1.5));
            _log.LogDebug("Completed task {Task}", run.Stops[0].Label);
        }

        // ---------------------------------------------------------------- bodies and emergencies

        private async ValueTask<bool> TryReportBodyAsync(Game game, InnerPlayerInfo info, DateTime now)
        {
            foreach (var body in Memory.KnownBodies.Values.ToList())
            {
                if (Memory.HandledBodies.Contains(body.VictimId))
                {
                    continue;
                }

                if (info.IsImpostor && _rng.NextDouble() > 0.35)
                {
                    // Walk on as if nothing happened.
                    Memory.HandledBodies.Add(body.VictimId);
                    continue;
                }

                var distance = Vector2.Distance(_pos, body.Position);
                if (distance <= ReportRange)
                {
                    _log.LogInformation("Reporting the body of {Victim} in {Room}", NameOf(game, body.VictimId), _map?.RoomAt(body.Position));
                    Memory.HandledBodies.Add(body.VictimId);
                    _reportingBody = null;
                    StopMoving();
                    await Client.ReportBodyAsync(body.VictimId);
                    return true;
                }

                if (_reportingBody != body.VictimId)
                {
                    _reportingBody = body.VictimId;
                    _reportStartedAt = now;
                    GoTo(body.Position, $"running to report {NameOf(game, body.VictimId)}'s body");
                }
                else if ((now - _reportStartedAt).TotalSeconds > 40 / Math.Max(0.1, _env.Config.TimeScale))
                {
                    Memory.HandledBodies.Add(body.VictimId);
                    _reportingBody = null;
                }

                return true;
            }

            return false;
        }

        private async ValueTask<bool> TryEmergencyAsync(Game game, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            if (_emergenciesUsed >= options.NumEmergencyMeetings || _map == null)
            {
                return false;
            }

            var culprits = Memory.CaughtImpostors
                .Where(id => !Memory.KnownDead.Contains(id) && game.GameNet.GameData.GetPlayerById(id) is { IsDead: false })
                .ToList();
            if (culprits.Count == 0)
            {
                return false;
            }

            var culprit = culprits[0];
            var button = _map.EmergencyButton;
            if (Vector2.Distance(_pos, button) <= 2.2f)
            {
                _emergenciesUsed++;
                _log.LogInformation("Calling an emergency meeting about {Who}", NameOf(game, culprit));
                StopMoving();
                await Client.ReportBodyAsync(byte.MaxValue);
                return true;
            }

            if (_activity != "running to the emergency button")
            {
                GoTo(button, "running to the emergency button");
            }

            return true;
        }

        // ---------------------------------------------------------------- killing

        private static float KillRange(NormalGameOptions options)
        {
            return options.KillDistance switch
            {
                KillDistances.Short => 1.0f,
                KillDistances.Long => 2.3f,
                _ => 1.7f,
            };
        }

        private bool IsTeammate(InnerPlayerInfo me, byte id, Game game)
        {
            var other = game.GameNet.GameData.GetPlayerById(id);
            return me.IsImpostor && other != null && other.IsImpostor;
        }

        private bool TryStartHunting(Game game, InnerPlayerInfo info, DateTime now)
        {
            if (_map == null)
            {
                return false;
            }

            // Head for the last known position of a crewmate, or somewhere random.
            var prey = Memory.LastSeen.Values
                .Where(s => !Memory.KnownDead.Contains(s.PlayerId) && !IsTeammate(info, s.PlayerId, game) && game.GameNet.GameData.GetPlayerById(s.PlayerId) is { IsDead: false })
                .OrderByDescending(s => s.Time)
                .FirstOrDefault();

            Vector2 destination;
            string label;
            if (prey != null && (now - prey.Time).TotalSeconds < 40 && Vector2.Distance(prey.Position, _pos) > 4)
            {
                destination = prey.Position;
                label = $"hunting near {prey.Room}";
            }
            else
            {
                var rooms = _map.Rooms.ToList();
                var room = rooms[_rng.Next(rooms.Count)];
                destination = _map.RoomHub(room);
                label = $"looking for prey in {BotMap.DisplayName(room)}";
            }

            GoTo(destination, label, () =>
            {
                _idleUntil = DateTime.UtcNow + Scaled(1 + (_rng.NextDouble() * 3));
                return default;
            });
            return true;
        }

        private async ValueTask<bool> TryKillAsync(Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            if (now < _killReadyAt || _map == null)
            {
                return false;
            }

            var prey = _visible.Where(v => !IsTeammate(info, v.Id, game)).OrderBy(v => v.Distance).ToList();
            if (prey.Count == 0)
            {
                return false;
            }

            // Only strike when nobody else is looking.
            var target = prey[0];
            var witnesses = prey.Count - 1;
            if (witnesses > 0)
            {
                return false;
            }

            var range = KillRange(options);
            if (target.Distance > range)
            {
                GoTo(target.Position, $"stalking {target.Name}");
                return false;
            }

            var room = _map.RoomAt(target.Position);

            // One kill at a time per game, and only while both sides are still what we think they are.
            var killLock = Hub?.KillLock(game.Code);
            if (killLock != null)
            {
                await killLock.WaitAsync();
            }

            try
            {
                if (game.GameState != GameStates.Started || game.ActiveMeeting != null || info.IsDead || target.Control.PlayerInfo is not { IsDead: false, IsImpostor: false })
                {
                    return false;
                }

                _log.LogInformation("Killing {Victim} in {Room}", target.Name, room);
                StopMoving();
                await Client.CheckMurderAsync(target.Control);
            }
            finally
            {
                killLock?.Release();
            }

            _killReadyAt = now + TimeSpan.FromSeconds(Math.Max(1, options.KillCooldown));
            Memory.MyKills.Add((target.Id, room, now));
            Memory.HandledBodies.Add(target.Id);
            Memory.Note(now, $"I killed {target.Name} in {room}", target.Id, 10);

            // The killer ends up on top of the victim.
            _pos = target.Position;
            await Client.SendPositionAsync(_pos);

            if (_rng.NextDouble() < 0.2)
            {
                _selfReportAt = now + Scaled(1.2);
                _selfReportVictim = target.Id;
                Memory.HandledBodies.Remove(target.Id);
            }
            else
            {
                var far = _map.Rooms.OrderByDescending(r => Vector2.Distance(_map.RoomHub(r), _pos)).Take(4).OrderBy(_ => _rng.Next()).First();
                GoTo(_map.RoomHub(far), $"leaving the scene towards {BotMap.DisplayName(far)}");
            }

            return true;
        }

        private sealed class TaskRun
        {
            public TaskRun(TaskInfo info, List<TaskStop> stops)
            {
                Info = info;
                Stops = stops;
            }

            public TaskInfo Info { get; }

            public List<TaskStop> Stops { get; }

            public int Index { get; set; }
        }
    }
}
