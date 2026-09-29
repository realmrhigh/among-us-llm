using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.Inner.Objects.Systems.ShipStatus;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Crewmate bots run to fix the two sabotages that end the game: reactor meltdown and O2.
    ///     Two bots each take one of the two consoles; the game host decides when the system counts as repaired.
    /// </summary>
    internal sealed partial class BotAgent
    {
        private RepairDuty? _duty;
        private DateTime _sabotageReadyAt = DateTime.MaxValue;

        internal bool CanRepair(Game game)
        {
            var info = Client.MyInfo;
            return _inGame && !Finished && info is { IsDead: false, IsImpostor: false } && _meeting == null && game.ActiveMeeting == null;
        }

        /// <summary>
        ///     An impostor bot occasionally starts a reactor meltdown or an O2 failure, which the crew (bots included) must fix.
        /// </summary>
        /// <param name="game">The game.</param>
        /// <param name="info">Our player info.</param>
        /// <param name="options">Game options.</param>
        /// <param name="now">The time.</param>
        /// <returns>A task that finishes when the decision was made (and the sabotage sent).</returns>
        private async ValueTask MaybeSabotageAsync(Game game, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            if (!info.IsImpostor || info.IsDead || now < _sabotageReadyAt || _env.Config.ImpostorSabotageChance <= 0)
            {
                return;
            }

            _sabotageReadyAt = now + TimeSpan.FromSeconds(90 + (_rng.NextDouble() * 60));
            if (options.Map is not (MapTypes.Skeld or MapTypes.Dleks or MapTypes.MiraHQ) || FindSabotage(game) != null || game.ActiveMeeting != null)
            {
                return;
            }

            var crew = game.GameNet.GameData.Players.Values.Count(p => !p.IsImpostor && !p.IsDead && !p.Disconnected);
            if (crew < 3 || _rng.NextDouble() > _env.Config.ImpostorSabotageChance)
            {
                return;
            }

            var target = _rng.Next(2) == 0 ? SystemTypes.Reactor : SystemTypes.LifeSupp;
            _log.LogInformation("Sabotaging: {Target}", target);
            await Client.SendUpdateSystemAsync(SystemTypes.Sabotage, (byte)target);
        }

        private static ActiveSabotage? FindSabotage(Game game)
        {
            var ship = game.GameNet.ShipStatus;
            if (ship == null)
            {
                return null;
            }

            foreach (var (type, system) in ship.Systems)
            {
                if (system is ReactorSystemType { IsActive: true } reactor)
                {
                    return new ActiveSabotage(type, false, reactor.Countdown);
                }

                if (system is LifeSuppSystemType { IsActive: true } o2)
                {
                    return new ActiveSabotage(type, true, o2.Countdown);
                }
            }

            return null;
        }

        private async ValueTask<bool> TrySabotageDutyAsync(Game game, DateTime now)
        {
            var active = FindSabotage(game);
            if (active == null)
            {
                if (_duty != null)
                {
                    await FinishDutyAsync(game, now, "the sabotage is fixed");
                }

                return false;
            }

            var sabotage = active.Value;
            if (_map == null)
            {
                return false;
            }

            if (_duty != null && _duty.System != sabotage.System)
            {
                await FinishDutyAsync(game, now, "a different sabotage started");
            }

            if (_duty == null)
            {
                _duty = ChooseDuty(game, sabotage, now);
                if (_duty == null)
                {
                    return false;
                }

                _log.LogInformation("Rushing to fix the {What} (console {Console})", sabotage.IsO2 ? "O2" : "reactor", _duty.Console);
                GoTo(_duty.Position, $"rushing to fix the {(sabotage.IsO2 ? "O2" : "reactor")}");
            }

            var duty = _duty;
            if ((now - duty.Since).TotalSeconds > 75)
            {
                await FinishDutyAsync(game, now, "gave up");
                return false;
            }

            if (_mode == Mode.Walking)
            {
                return true;
            }

            if (!duty.Arrived)
            {
                if (Vector2.Distance(_pos, duty.Position) > 1.5f)
                {
                    GoTo(duty.Position, $"rushing to fix the {(sabotage.IsO2 ? "O2" : "reactor")}");
                    return true;
                }

                duty.Arrived = true;
                duty.ArrivedAt = now;
                _activity = sabotage.IsO2 ? "entering the O2 code" : "holding a reactor console";
                if (!sabotage.IsO2)
                {
                    await Client.SendUpdateSystemAsync(duty.System, (byte)(0x40 | duty.Console));
                }
            }

            if (sabotage.IsO2 && !duty.Confirmed && (now - duty.ArrivedAt).TotalSeconds >= 4 / Math.Max(0.1, _env.Config.TimeScale))
            {
                duty.Confirmed = true;
                await Client.SendUpdateSystemAsync(duty.System, (byte)(0x40 | duty.Console));
            }

            return true;
        }

        private async ValueTask FinishDutyAsync(Game game, DateTime now, string why)
        {
            var duty = _duty;
            _duty = null;
            Hub?.ReleaseConsoles(game.Code, this);
            if (duty == null)
            {
                return;
            }

            _log.LogInformation("Repair duty over: {Why}", why);
            if (duty.Arrived && !duty.IsO2)
            {
                await Client.SendUpdateSystemAsync(duty.System, (byte)(0x20 | duty.Console));
            }

            StopMoving();
            _idleUntil = now;
        }

        private RepairDuty? ChooseDuty(Game game, ActiveSabotage sabotage, DateTime now)
        {
            if (_map == null || Hub == null)
            {
                return null;
            }

            var candidates = Hub.AgentsOf(game.Code).Where(a => a.CanRepair(game)).ToList();
            if (!candidates.Contains(this))
            {
                return null;
            }

            foreach (var (id, position) in SabotageConsoles(sabotage))
            {
                var ranked = candidates
                    .OrderBy(a => _map.Nav.PathLength(a.Position, position))
                    .Take(2)
                    .ToList();
                if (ranked.Contains(this) && Hub.TryClaimConsole(game.Code, (int)sabotage.System, id, this))
                {
                    return new RepairDuty(sabotage.System, sabotage.IsO2, id, position, now);
                }
            }

            return null;
        }

        private IReadOnlyList<(int Id, Vector2 Position)> SabotageConsoles(ActiveSabotage sabotage)
        {
            var map = _map!;
            if (map.HasHandmadeGraph)
            {
                return sabotage.IsO2
                    ? new[] { (0, map.Nav["O2_C"].Position), (1, map.Nav["ADM_C"].Position) }
                    : new[] { (0, map.Nav["RX_MANI"].Position), (1, map.Nav["RX_START"].Position) };
            }

            var room = sabotage.System == SystemTypes.Laboratory ? "Laboratory" : (sabotage.IsO2 ? "LifeSupp" : "Reactor");
            var hub = map.RoomHub(room);
            return new[] { (0, hub + new Vector2(-1.2f, 0)), (1, hub + new Vector2(1.2f, 0)) };
        }

        internal readonly record struct ActiveSabotage(SystemTypes System, bool IsO2, float Countdown);

        private sealed class RepairDuty
        {
            public RepairDuty(SystemTypes system, bool isO2, int console, Vector2 position, DateTime since)
            {
                System = system;
                IsO2 = isO2;
                Console = console;
                Position = position;
                Since = since;
            }

            public SystemTypes System { get; }

            public bool IsO2 { get; }

            public int Console { get; }

            public Vector2 Position { get; }

            public DateTime Since { get; }

            public bool Arrived { get; set; }

            public DateTime ArrivedAt { get; set; }

            public bool Confirmed { get; set; }
        }
    }
}
