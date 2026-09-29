using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Server.LlmBots.Maps;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    internal sealed partial class BotAgent
    {
        private const float ReportRange = 4.0f;

        private readonly ConcurrentQueue<WorldEvent> _worldEvents = new();
        private readonly List<PlayerObservation> _visible = new();

        private MeetingStartedWorldEvent? _pendingMeetingEvent;

        public BotMemory Memory { get; } = new();

        internal List<(string Room, DateTime Since)> Route { get; } = new();

        internal IReadOnlyList<PlayerObservation> Visible => _visible;

        public void Observe(WorldEvent evt) => _worldEvents.Enqueue(evt);

        private static NormalGameOptions? OptionsOf(Game game) => game.Options as NormalGameOptions;

        private float VisionRadius(Game game, InnerPlayerInfo info)
        {
            var options = OptionsOf(game);
            var mod = options == null ? 1f : (info.IsImpostor ? options.ImpostorLightMod : options.CrewLightMod);
            return Math.Max(2.5f, 5.5f * Math.Max(0.5f, mod));
        }

        private bool CanSee(Vector2 from, Vector2 to, float radius)
        {
            var distance = Vector2.Distance(from, to);
            if (distance > radius)
            {
                return false;
            }

            if (distance <= 2.5f || _map == null)
            {
                return true;
            }

            // Walls are unknown, but a route that is much longer than the straight line means something is in between.
            return _map.Nav.PathLength(from, to) <= (1.9 * distance) + 1.5;
        }

        private string NameOf(Game game, byte playerId)
        {
            return game.GameNet.GameData.GetPlayerById(playerId)?.PlayerName ?? $"player {playerId}";
        }

        private void ProcessWorldEvents(Game game, InnerPlayerInfo me, DateTime now)
        {
            while (_worldEvents.TryDequeue(out var evt))
            {
                var myPos = evt.Snapshot.FirstOrDefault(p => p.Id == me.PlayerId).Position;
                var radius = VisionRadius(game, me);
                var amAlive = !me.IsDead;

                switch (evt)
                {
                    case MurderWorldEvent murder:
                    {
                        if (murder.KillerId == me.PlayerId || murder.VictimId == me.PlayerId || !amAlive)
                        {
                            break;
                        }

                        if (CanSee(myPos, murder.Position, radius))
                        {
                            var room = _map?.RoomAt(murder.Position) ?? "somewhere";
                            var killer = NameOf(game, murder.KillerId);
                            var victim = NameOf(game, murder.VictimId);
                            Memory.CaughtImpostors.Add(murder.KillerId);
                            Memory.KnownDead.Add(murder.VictimId);
                            Memory.AddSuspicion(murder.KillerId, 100);
                            Memory.Note(now, $"I SAW {killer} kill {victim} in {room}", murder.KillerId, 10);
                            _log.LogInformation("Witnessed {Killer} killing {Victim}", killer, victim);
                        }

                        break;
                    }

                    case VentWorldEvent vent:
                    {
                        if (vent.PlayerId == me.PlayerId || !amAlive)
                        {
                            break;
                        }

                        if (CanSee(myPos, vent.Position, radius))
                        {
                            var room = _map?.RoomAt(vent.Position) ?? "somewhere";
                            var who = NameOf(game, vent.PlayerId);
                            Memory.CaughtImpostors.Add(vent.PlayerId);
                            Memory.AddSuspicion(vent.PlayerId, 50);
                            Memory.Note(now, $"I SAW {who} {(vent.Entered ? "enter" : "exit")} a vent in {room}", vent.PlayerId, 9);
                        }

                        break;
                    }

                    case MeetingStartedWorldEvent started:
                        Memory.MeetingsHeld++;
                        Memory.KnownBodies.Clear();
                        Memory.Chat.Clear();
                        if (_meeting != null && _meeting.Started == null)
                        {
                            _meeting.Started = started;
                        }
                        else
                        {
                            _pendingMeetingEvent = started;
                        }

                        break;

                    case ChatWorldEvent chat:
                        Memory.Chat.Add(new ChatLine(chat.Time, chat.SenderId, NameOf(game, chat.SenderId), chat.Text));
                        if (Memory.Chat.Count > 60)
                        {
                            Memory.Chat.RemoveAt(0);
                        }

                        break;

                    case MeetingEndedWorldEvent ended:
                        OnMeetingResult(game, me, ended, now);
                        break;

                    case GameEndedWorldEvent:
                        Memory.Reset(now);
                        break;
                }
            }
        }

        private void OnMeetingResult(Game game, InnerPlayerInfo me, MeetingEndedWorldEvent ended, DateTime now)
        {
            var votes = string.Join(", ", ended.Votes.Select(v => $"{NameOf(game, v.Voter)}->{(v.Target == 253 ? "skip" : NameOf(game, v.Target))}"));
            string result;
            if (ended.ExiledId is { } id)
            {
                var info = game.GameNet.GameData.GetPlayerById(id);
                var confirm = OptionsOf(game)?.ConfirmImpostor ?? true;
                var role = confirm && info != null ? (info.IsImpostor ? "was an impostor" : "was NOT an impostor") : "role unknown";
                result = $"{NameOf(game, id)} was voted out ({role})";
                Memory.KnownDead.Add(id);
                if (confirm && info != null)
                {
                    Memory.ExiledWasImpostor[id] = info.IsImpostor;
                }
            }
            else
            {
                result = ended.Tie ? "the vote was tied, nobody ejected" : "nobody was ejected";
            }

            Memory.History.Add($"Meeting {Memory.MeetingsHeld}: {result}. Votes: {(votes.Length == 0 ? "none" : votes)}");
        }

        private void UpdateVision(Game game, InnerPlayerControl me, InnerPlayerInfo info, DateTime now)
        {
            _visible.Clear();
            if (_map == null)
            {
                return;
            }

            var radius = VisionRadius(game, info);
            var room = _map.RoomAt(_pos);
            if (Route.Count == 0 || Route[^1].Room != room)
            {
                Route.Add((room, now));
                if (Route.Count > 12)
                {
                    Route.RemoveAt(0);
                }
            }

            foreach (var player in game.Players)
            {
                if (player.Character is not InnerPlayerControl other || other == me || other.PlayerInfo is not { } otherInfo)
                {
                    continue;
                }

                if (otherInfo.IsDead || otherInfo.Disconnected)
                {
                    continue;
                }

                var position = other.NetworkTransform.Position;
                if (!CanSee(_pos, position, radius))
                {
                    continue;
                }

                var otherRoom = _map.RoomAt(position);
                _visible.Add(new PlayerObservation(otherInfo.PlayerId, otherInfo.PlayerName, position, otherRoom, Vector2.Distance(_pos, position), other));
                Memory.RecordSighting(new Sighting(otherInfo.PlayerId, otherRoom, position, now));
            }

            foreach (var body in Hub?.BodiesOf(game.Code) ?? Array.Empty<BodyRecord>())
            {
                if (Memory.KnownBodies.ContainsKey(body.VictimId) || body.VictimId == me.PlayerId)
                {
                    continue;
                }

                if (CanSee(_pos, body.Position, radius))
                {
                    Memory.KnownBodies[body.VictimId] = body;
                    Memory.KnownDead.Add(body.VictimId);
                    Memory.Note(now, $"I found the body of {NameOf(game, body.VictimId)} in {_map.RoomAt(body.Position)}", body.VictimId, 6);
                }
            }

            _ = room;
        }
    }

    internal readonly record struct PlayerObservation(byte Id, string Name, Vector2 Position, string Room, float Distance, InnerPlayerControl Control);
}
