using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Impostor.Server.LlmBots.Brain
{
    /// <summary>
    ///     Plays meetings with rules and templates. No network needed. Also the fallback when an LLM is unavailable.
    /// </summary>
    internal sealed class HeuristicBrain : IBrain
    {
        private readonly Random _rng;

        public HeuristicBrain(int seed)
        {
            _rng = new Random(seed);
        }

        public string Name => "heuristic";

        public ValueTask<MeetingDecision> DecideAsync(MeetingContext ctx, CancellationToken cancellationToken)
        {
            return new ValueTask<MeetingDecision>(Decide(ctx));
        }

        public MeetingDecision Decide(MeetingContext ctx)
        {
            lock (_rng)
            {
                return DecideLocked(ctx);
            }
        }

        private MeetingDecision DecideLocked(MeetingContext ctx)
        {
            var say = new List<string>();
            var vote = ChooseVote(ctx);

            switch (ctx.Stage)
            {
                case MeetingStage.Opening:
                    say.AddRange(Opening(ctx, vote));
                    break;
                case MeetingStage.Reply:
                    say.AddRange(Reply(ctx, vote));
                    break;
                case MeetingStage.FinalVote:
                    if (vote != null && vote != "skip" && _rng.NextDouble() < 0.6)
                    {
                        say.Add(Pick($"voting {vote}", $"I'm going {vote}", $"{vote} it is", $"vote {vote}"));
                    }
                    else if (vote == "skip" && _rng.NextDouble() < 0.5)
                    {
                        say.Add(Pick("no proof, skipping", "skip for me", "nothing solid, skip"));
                    }

                    break;
            }

            return new MeetingDecision(say.Take(ctx.MaxLines).ToList(), vote, Name);
        }

        private static string RoomOf(MeetingContext ctx, string fallback) => ctx.BodyRoom ?? fallback;

        private static string Whereabouts(MeetingContext ctx)
        {
            var rooms = ctx.MyRouteRooms.Where(r => r != "-").Distinct().ToList();
            return rooms.Count switch
            {
                0 => "tasks",
                1 => rooms[0],
                _ => $"{rooms[^2]} then {rooms[^1]}",
            };
        }

        private string Pick(params string[] options) => options[_rng.Next(options.Length)];

        private IEnumerable<string> Opening(MeetingContext ctx, string? vote)
        {
            var lines = new List<string>();
            var caught = ctx.Caught.Select(id => ctx.Players.FirstOrDefault(p => p.Id == id)).FirstOrDefault(p => p != null && p.Alive);

            if (ctx.IAmImpostor)
            {
                lines.AddRange(ImpostorOpening(ctx));
                return lines;
            }

            if (caught != null)
            {
                lines.Add($"I SAW {caught.Name} do it. vote {caught.Name}");
                return lines;
            }

            if (ctx.Reporter != null && ctx.Reporter.IsMe && ctx.Body != null)
            {
                lines.Add(Pick(
                    $"found {ctx.Body.Name} dead in {RoomOf(ctx, "here")}",
                    $"{ctx.Body.Name}'s body is in {RoomOf(ctx, "here")}, who was near?"));
            }
            else if (ctx.Reporter != null && ctx.Body == null)
            {
                lines.Add(Pick("who called this?", $"{ctx.Reporter.Name} what's the emergency?"));
            }

            var suspect = ctx.Candidates
                .Select(p => (Player: p, Score: ctx.Suspicion.GetValueOrDefault(p.Id)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (suspect.Player != null && suspect.Score >= 2 && ctx.Body != null && lines.Count < 2)
            {
                lines.Add(Pick(
                    $"{suspect.Player.Name} was right near the body earlier",
                    $"I saw {suspect.Player.Name} close to where {ctx.Body.Name} was found",
                    $"{suspect.Player.Name} where were you just before this?"));
                return lines;
            }

            var sighting = ctx.Sightings.LastOrDefault();
            if (sighting != null && lines.Count < 2)
            {
                lines.Add(Pick($"last I saw: {sighting}", $"I saw: {sighting}"));
            }
            else if (lines.Count < 2)
            {
                lines.Add(Pick("was just doing tasks, saw nothing", "no info from me, on tasks the whole time"));
            }

            return lines;
        }

        private IEnumerable<string> ImpostorOpening(MeetingContext ctx)
        {
            var lines = new List<string>();
            var victim = ctx.Body;
            var innocents = ctx.Candidates.Where(p => !p.IsTeammate).ToList();
            var target = innocents.Count > 0 ? innocents[_rng.Next(innocents.Count)] : null;
            var where = Whereabouts(ctx);

            if (ctx.Reporter != null && ctx.Reporter.IsMe && victim != null)
            {
                lines.Add($"found {victim.Name} in {RoomOf(ctx, "here")}, no idea who");
            }
            else
            {
                lines.Add(Pick($"I was in {where}, saw nothing", "was on tasks, didn't see anything", "just wandering tasks, no info"));
            }

            if (target != null && _rng.NextDouble() < 0.6)
            {
                lines.Add(Pick($"{target.Name} where were you?", $"{target.Name} was near there earlier I think", $"anyone seen {target.Name}?"));
            }

            return lines;
        }

        private IEnumerable<string> Reply(MeetingContext ctx, string? vote)
        {
            var lines = new List<string>();
            var accuser = ctx.Chat.LastOrDefault(c => c.SenderId != ctx.Me.Id && IsAccusation(c.Text, ctx.Me.Name));
            if (accuser != null)
            {
                var room = Whereabouts(ctx);
                lines.Add(ctx.IAmImpostor
                    ? Pick($"{accuser.SenderName} that's a reach, I was in {room}", $"why me {accuser.SenderName}? I was in {room}")
                    : Pick($"not me {accuser.SenderName}, I was in {room}", $"{accuser.SenderName} I have been on tasks, check the timeline"));
                return lines;
            }

            var accused = ctx.Chat.LastOrDefault(c => c.SenderId != ctx.Me.Id);
            if (accused != null && vote != null && vote != "skip" && _rng.NextDouble() < 0.7)
            {
                lines.Add(Pick($"leaning {vote} right now", $"{vote} feels off", $"I think {vote}"));
            }
            else if (_rng.NextDouble() < 0.4)
            {
                lines.Add(Pick("anyone got anything solid?", "need more info before voting"));
            }

            return lines;
        }

        private string? ChooseVote(MeetingContext ctx)
        {
            var candidates = ctx.Candidates.ToList();
            if (candidates.Count == 0)
            {
                return "skip";
            }

            if (ctx.IAmImpostor)
            {
                // Never vote for a teammate; follow the crowd or pick the most suspicious crewmate.
                var pool = candidates.Where(p => !p.IsTeammate).ToList();
                if (pool.Count == 0)
                {
                    return "skip";
                }

                var mentioned = pool
                    .Select(p => (Player: p, Count: ctx.Chat.Count(c => c.SenderId != ctx.Me.Id && c.Text.Contains(p.Name, StringComparison.OrdinalIgnoreCase))))
                    .OrderByDescending(x => x.Count)
                    .First();
                if (mentioned.Count > 0)
                {
                    return mentioned.Player.Name;
                }

                return _rng.NextDouble() < 0.5 ? pool[_rng.Next(pool.Count)].Name : "skip";
            }

            var caught = candidates.FirstOrDefault(p => ctx.Caught.Contains(p.Id));
            if (caught != null)
            {
                return caught.Name;
            }

            var scored = candidates
                .Select(p => (Player: p, Score: ctx.Suspicion.GetValueOrDefault(p.Id) + (ctx.Chat.Count(c => c.SenderId != ctx.Me.Id && c.SenderId != p.Id && MentionsSuspiciously(c.Text, p.Name)) * 1.5)))
                .OrderByDescending(x => x.Score)
                .ToList();

            if (scored[0].Score >= 3)
            {
                return scored[0].Player.Name;
            }

            return "skip";
        }

        private static bool IsAccusation(string text, string name)
        {
            if (!text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var t = text.ToLowerInvariant();
            return t.Contains("sus") || t.Contains("vote") || t.Contains("where were you") || t.Contains("near") || t.Contains("close to")
                   || t.Contains("vent") || t.Contains("kill") || t.Contains("acting off") || t.Contains("feels off") || t.Contains('?') || t.Contains("did it");
        }

        private static bool MentionsSuspiciously(string text, string name)
        {
            if (!text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var t = text.ToLowerInvariant();
            return t.Contains("sus") || t.Contains("vote") || t.Contains("saw") || t.Contains("vent") || t.Contains("kill") || t.Contains("near");
        }
    }
}
