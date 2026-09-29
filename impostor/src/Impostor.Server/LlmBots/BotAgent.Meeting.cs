using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Maps;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    internal sealed partial class BotAgent
    {
        private MeetingRun? _meeting;
        private DateTime? _meetingSeenAt;

        /// <summary>Gets a text log of what this bot said and decided, for transcripts.</summary>
        public Action<string>? Transcript { get; set; }

        private async ValueTask MeetingTickAsync(Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, InnerMeetingHud meeting, DateTime now)
        {
            if (_meeting == null || _meeting.NetId != meeting.NetId)
            {
                // The host may announce who reported before or after the meeting object shows up: give it a moment.
                _meetingSeenAt ??= now;
                if (_pendingMeetingEvent == null && (now - _meetingSeenAt.Value).TotalSeconds < 1.5)
                {
                    return;
                }

                _meetingSeenAt = null;
                await BeginMeetingAsync(game, me, info, options, meeting, now);
            }

            var run = _meeting!;
            if (info.IsDead)
            {
                return;
            }

            // Collect the answer of a brain that finished thinking.
            if (run.Pending is { IsCompleted: true } pending)
            {
                run.Pending = null;
                try
                {
                    var decision = await pending;
                    ApplyDecision(run, decision, now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Brain failed, using fallback");
                }
            }

            if (!run.OpeningStarted && now >= run.OpeningAt)
            {
                run.OpeningStarted = true;
                StartThinking(run, game, me, info, options, MeetingStage.Opening, now);
            }

            if (!run.ReplyStarted && now >= run.ReplyAt)
            {
                run.ReplyStarted = true;

                // Only spend a request when somebody said something that concerns us.
                if (SomethingToReplyTo(info, run))
                {
                    StartThinking(run, game, me, info, options, MeetingStage.Reply, now);
                }
            }

            if (!run.FinalStarted && now >= run.FinalThinkAt)
            {
                run.FinalStarted = true;

                // Keep the earlier choice when nothing new was said since we made it.
                if (run.Vote != null && Memory.Chat.Count <= run.ChatSeenAtDecision)
                {
                    run.FinalDone = true;
                }
                else
                {
                    StartThinking(run, game, me, info, options, MeetingStage.FinalVote, now);
                }
            }

            // Talk.
            if (run.Say.Count > 0 && now >= run.NextSayAt && now < run.Deadline && run.Spoken < _env.Config.MaxLinesPerMeeting)
            {
                var line = run.Say.Dequeue();
                run.Spoken++;
                run.NextSayAt = now + TimeSpan.FromSeconds(2.5 + (_rng.NextDouble() * 3));
                Transcript?.Invoke($"[{info.PlayerName}] {line}");
                await Client.SendChatAsync(line);
            }

            // Vote.
            if (!run.VoteCast && now >= run.VoteAt)
            {
                var ready = run.FinalDone || now >= run.VoteAt + TimeSpan.FromSeconds(4) || now >= run.Deadline - TimeSpan.FromSeconds(1);
                if (ready && DateTime.UtcNow < run.Deadline + TimeSpan.FromSeconds(1))
                {
                    await CastVoteAsync(game, info, run, now);
                }
            }
        }

        private async ValueTask BeginMeetingAsync(Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, InnerMeetingHud meeting, DateTime now)
        {
            StopMoving();
            _task = null;
            _duty = null;
            Hub?.ReleaseConsoles(game.Code, this);
            _reportingBody = null;
            _selfReportAt = null;

            var number = Memory.MeetingsHeld;
            var start = now;
            var anim = _env.Config.MeetingAnimationSeconds;
            var discussion = Math.Max(0, options.DiscussionTime);
            var voting = Math.Max(5, options.VotingTime);

            var opening = start + TimeSpan.FromSeconds(anim + 0.3 + (_rng.NextDouble() * 2));
            var deadline = start + TimeSpan.FromSeconds(anim + discussion + voting - 3);
            var voteAt = start + TimeSpan.FromSeconds(anim + discussion + 0.5 + (_rng.NextDouble() * 5));
            if (voteAt > deadline)
            {
                voteAt = deadline;
            }

            var finalThink = voteAt - TimeSpan.FromSeconds(4);
            if (finalThink < opening + TimeSpan.FromSeconds(1))
            {
                finalThink = opening + TimeSpan.FromSeconds(1);
            }

            var reply = opening + TimeSpan.FromTicks((finalThink - opening).Ticks / 2);

            _meeting = new MeetingRun
            {
                NetId = meeting.NetId,
                Start = start,
                Number = number,
                OpeningAt = opening,
                ReplyAt = reply,
                FinalThinkAt = finalThink,
                VoteAt = voteAt,
                Deadline = deadline,
                Cts = new CancellationTokenSource(),
                Started = _pendingMeetingEvent,
            };
            _pendingMeetingEvent = null;

            // Whoever was seen close to the body a short while ago becomes a little suspicious.
            if (_meeting.Started is { BodyPosition: { } bodyPosition, BodyId: { } bodyId })
            {
                foreach (var seen in Memory.LastSeen.Values)
                {
                    var age = (now - seen.Time).TotalSeconds;
                    if (seen.PlayerId != bodyId && seen.PlayerId != info.PlayerId && age < 60 && Vector2.Distance(seen.Position, bodyPosition) < 9)
                    {
                        Memory.AddSuspicion(seen.PlayerId, 3.5 - (age / 30));
                        Memory.Note(now, $"{NameOf(game, seen.PlayerId)} was near where the body was found ({BotMemory.Ago(now, seen.Time)})", seen.PlayerId, 3);
                    }
                }
            }

            var spot = game.GameNet.ShipStatus?.GetSpawnLocation(me, game.PlayerCount, false) ?? _map?.MeetingCenter ?? _pos;
            _pos = spot;
            _activity = "in a meeting";
            if (!info.IsDead)
            {
                await Client.SnapToAsync(spot);
            }

            _log.LogInformation("Meeting #{Number} begins ({Reason})", number, DescribeMeetingReason(game, _meeting.Started));
            Transcript?.Invoke($"--- Meeting {number}: {DescribeMeetingReason(game, _meeting.Started)} ---");
        }

        private async ValueTask EndMeetingAsync(Game game, InnerPlayerInfo info, NormalGameOptions options, DateTime now)
        {
            _meeting?.Cts.Cancel();
            _meeting = null;
            _meetingSeenAt = null;
            _task = null;
            StopMoving();
            _frozenUntil = now + Scaled(_env.Config.PostMeetingFreezeSeconds);
            _killReadyAt = now + TimeSpan.FromSeconds(Math.Max(1, options.KillCooldown));
            _idleUntil = _frozenUntil;
            _lastStepAt = now;
            Memory.KnownBodies.Clear();
            await ValueTask.CompletedTask;
        }

        private void StartThinking(MeetingRun run, Game game, InnerPlayerControl me, InnerPlayerInfo info, NormalGameOptions options, MeetingStage stage, DateTime now)
        {
            if (run.Pending != null)
            {
                if (stage != MeetingStage.FinalVote)
                {
                    return;
                }

                // The final vote matters more than an earlier line still being written.
                run.Cts.Cancel();
                run.Cts = new CancellationTokenSource();
            }

            var context = BuildContext(game, info, options, run, stage, now);
            var runCts = run.Cts;
            var limit = stage switch
            {
                MeetingStage.Opening => Math.Clamp((run.VoteAt - now).TotalSeconds - 4, 4, 15),
                MeetingStage.Reply => Math.Clamp((run.VoteAt - now).TotalSeconds - 3, 3, 10),
                _ => Math.Clamp((run.Deadline - now).TotalSeconds - 3, 3, 12),
            };
            var brain = Brain;
            run.Pending = Task.Run(async () =>
            {
                // A slow model must not leave the bot silent: after the limit the built-in heuristics answer instead.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(limit));
                try
                {
                    return await brain.DecideAsync(context, timeout.Token);
                }
                catch (OperationCanceledException) when (!runCts.IsCancellationRequested)
                {
                    _log.LogWarning("{Brain} was too slow for the {Stage} step, using heuristics", brain.Name, stage);
                    return _fallback.Decide(context) with { Source = "heuristic (model too slow)" };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("{Brain} failed ({Message}), falling back to heuristics", brain.Name, ex.Message);
                    return _fallback.Decide(context);
                }
            });
            run.PendingStage = stage;
        }

        private void ApplyDecision(MeetingRun run, MeetingDecision decision, DateTime now)
        {
            var stage = run.PendingStage;
            if (decision.Vote != null)
            {
                run.Vote = decision.Vote;
            }

            foreach (var line in decision.Say)
            {
                var clean = CleanLine(line);
                if (clean.Length == 0 || run.SaidLines.Contains(clean))
                {
                    continue;
                }

                run.SaidLines.Add(clean);
                run.Say.Enqueue(clean);
            }

            if (stage == MeetingStage.Opening)
            {
                run.ChatSeenAtOpening = Memory.Chat.Count;
            }

            run.ChatSeenAtDecision = Memory.Chat.Count;

            if (stage == MeetingStage.FinalVote)
            {
                run.FinalDone = true;
            }

            if (run.NextSayAt < now)
            {
                run.NextSayAt = now + TimeSpan.FromMilliseconds(400 + _rng.Next(1500));
            }
        }

        private async ValueTask CastVoteAsync(Game game, InnerPlayerInfo info, MeetingRun run, DateTime now)
        {
            run.VoteCast = true;
            byte choice = 253;

            if (run.Vote == null && game.Options is NormalGameOptions voteOptions)
            {
                // The model never answered in time: decide with the built-in rules.
                run.Vote = _fallback.Decide(BuildContext(game, info, voteOptions, run, MeetingStage.FinalVote, now)).Vote;
            }

            var target = run.Vote;
            if (target != null && !string.Equals(target, "skip", StringComparison.OrdinalIgnoreCase))
            {
                var found = game.GameNet.GameData.Players.Values.FirstOrDefault(p => string.Equals(p.PlayerName, target, StringComparison.OrdinalIgnoreCase) && !p.IsDead && p.PlayerId != info.PlayerId);
                found ??= game.GameNet.GameData.Players.Values.FirstOrDefault(p => !p.IsDead && p.PlayerId != info.PlayerId && target.Contains(p.PlayerName, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    choice = found.PlayerId;
                }
            }

            var label = choice == 253 ? "skip" : NameOf(game, choice);
            _log.LogInformation("Votes {Target}", label);
            Transcript?.Invoke($"[{info.PlayerName}] votes {label}");

            if (info.IsImpostor && choice != 253 && game.GameNet.GameData.GetPlayerById(choice) is { IsImpostor: true })
            {
                // Never vote out a teammate.
                choice = 253;
            }

            await Client.CastVoteAsync(choice);
        }

        private bool SomethingToReplyTo(InnerPlayerInfo info, MeetingRun run)
        {
            foreach (var line in Memory.Chat.Skip(run.ChatSeenAtOpening))
            {
                if (line.SenderId == info.PlayerId)
                {
                    continue;
                }

                var text = line.Text.ToLowerInvariant();
                if (text.Contains(info.PlayerName.ToLowerInvariant()) || text.Contains(info.CurrentOutfit.Color.ToString().ToLowerInvariant()))
                {
                    return true;
                }

                if (text.Contains("sus") || text.Contains("vent") || text.Contains("saw ") || text.Contains("kill") || text.Contains('?'))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CleanLine(string text)
        {
            var t = text.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"');
            if (t.Length > 100)
            {
                var cut = t.LastIndexOf(' ', 99);
                t = t.Substring(0, cut > 40 ? cut : 100);
            }

            return t;
        }

        private string DescribeMeetingReason(Game game, MeetingStartedWorldEvent? started)
        {
            if (started == null)
            {
                return "meeting called";
            }

            var reporter = started.ReporterId is { } r ? NameOf(game, r) : "someone";
            if (started.BodyId is { } b && b != byte.MaxValue)
            {
                var room = started.BodyPosition is { } p && _map != null ? _map.RoomAt(p) : "unknown";
                return $"{reporter} found {NameOf(game, b)}'s body in {room}";
            }

            return $"{reporter} called an emergency meeting";
        }

        private MeetingContext BuildContext(Game game, InnerPlayerInfo info, NormalGameOptions options, MeetingRun run, MeetingStage stage, DateTime now)
        {
            var confirmAlive = game.GameNet.GameData.Players.Values.ToList();
            var briefs = confirmAlive
                .Select(p => new PlayerBrief(
                    p.PlayerId,
                    p.PlayerName,
                    p.CurrentOutfit.Color.ToString(),
                    !p.IsDead && !p.Disconnected,
                    p.PlayerId == info.PlayerId,
                    info.IsImpostor && p.IsImpostor && p.PlayerId != info.PlayerId))
                .ToList();

            var me = briefs.First(b => b.IsMe);
            PlayerBrief? reporter = null;
            PlayerBrief? body = null;
            string? bodyRoom = null;
            var reason = "A meeting was called.";
            if (run.Started is { } started)
            {
                reporter = started.ReporterId is { } r ? briefs.FirstOrDefault(b => b.Id == r) : null;
                body = started.BodyId is { } bi && bi != byte.MaxValue ? briefs.FirstOrDefault(b => b.Id == bi) : null;
                bodyRoom = started.BodyPosition is { } bp && _map != null ? _map.RoomAt(bp) : null;
                reason = DescribeMeetingReason(game, started) + ".";
            }

            var observations = Memory.Notes
                .OrderBy(n => n.Time)
                .Select(n => $"{BotMemory.Ago(now, n.Time)}: {n.Text}")
                .TakeLast(14)
                .ToList();

            var sightings = Memory.Timeline
                .OrderBy(t => t.Time)
                .Select(t => $"{NameOf(game, t.PlayerId)} in {t.Room} ({BotMemory.Ago(now, t.Time)})")
                .TakeLast(14)
                .ToList();

            var secrets = new List<string>();
            if (info.IsImpostor)
            {
                var mates = briefs.Where(b => b.IsTeammate).Select(b => b.Name).ToList();
                secrets.Add(mates.Count == 0 ? "You are the only impostor." : "Your impostor teammates: " + string.Join(", ", mates) + ".");
                foreach (var kill in Memory.MyKills)
                {
                    secrets.Add($"You killed {NameOf(game, kill.Victim)} in {kill.Room} ({BotMemory.Ago(now, kill.Time)}).");
                }
            }

            var done = info.Tasks.Count(t => t.Complete);
            var tasks = new List<string>();
            if (!info.IsImpostor)
            {
                tasks.Add($"You completed {done} of {info.Tasks.Count} tasks.");
            }

            if (_map != null)
            {
                var doing = info.Tasks
                    .Where(t => t.Task != null && _map.Tasks.TryGetValue(t.Task.Id, out _))
                    .Select(t => (Spec: _map.Tasks[t.Task!.Id], t.Complete))
                    .Select(x => $"{TaskPlanner.Name(x.Spec.TaskType)} in {(x.Spec.Consoles.Count > 0 ? BotMap.DisplayName(x.Spec.Consoles[0].Room) : "various places")}{(x.Complete ? " (done)" : string.Empty)}")
                    .ToList();
                tasks.AddRange(doing);
            }

            return new MeetingContext
            {
                MapName = _map?.Name ?? "The Skeld",
                MeetingNumber = run.Number,
                Stage = stage,
                Me = me,
                IAmImpostor = info.IsImpostor,
                Players = briefs,
                Reason = reason,
                BodyRoom = bodyRoom,
                Reporter = reporter,
                Body = body,
                Observations = observations,
                Sightings = sightings,
                SecretFacts = secrets,
                MyTasks = tasks,
                MyRoute = Route.Where(r => r.Room != "-").Select(r => $"{r.Room} ({BotMemory.Ago(now, r.Since)})").TakeLast(6).ToList(),
                MyRouteRooms = Route.Select(r => r.Room).TakeLast(4).ToList(),
                History = Memory.History.ToList(),
                Chat = Memory.Chat.ToList(),
                Suspicion = new Dictionary<byte, double>(Memory.Suspicion),
                Caught = new HashSet<byte>(Memory.CaughtImpostors),
                SecondsLeft = Math.Max(0, (run.Deadline - now).TotalSeconds),
                MaxLines = Math.Max(1, _env.Config.MaxLinesPerMeeting - run.Spoken),
                Persona = Persona,
            };
        }

        private sealed class MeetingRun
        {
            public uint NetId { get; init; }

            public DateTime Start { get; init; }

            public int Number { get; init; }

            public DateTime OpeningAt { get; init; }

            public DateTime ReplyAt { get; init; }

            public DateTime FinalThinkAt { get; init; }

            public DateTime VoteAt { get; init; }

            public DateTime Deadline { get; init; }

            public CancellationTokenSource Cts { get; set; } = new();

            public MeetingStartedWorldEvent? Started { get; set; }

            public bool OpeningStarted { get; set; }

            public bool ReplyStarted { get; set; }

            public bool FinalStarted { get; set; }

            public bool FinalDone { get; set; }

            public bool VoteCast { get; set; }

            public int Spoken { get; set; }

            public int ChatSeenAtOpening { get; set; }

            public int ChatSeenAtDecision { get; set; }

            public Task<MeetingDecision>? Pending { get; set; }

            public MeetingStage PendingStage { get; set; }

            public string? Vote { get; set; }

            public Queue<string> Say { get; } = new();

            public HashSet<string> SaidLines { get; } = new();

            public DateTime NextSayAt { get; set; }
        }
    }
}
