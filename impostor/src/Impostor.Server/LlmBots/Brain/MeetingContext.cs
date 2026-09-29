using System;
using System.Collections.Generic;
using System.Linq;

namespace Impostor.Server.LlmBots.Brain
{
    internal enum MeetingStage
    {
        Opening,
        Reply,
        FinalVote,
    }

    internal sealed record PlayerBrief(byte Id, string Name, string Color, bool Alive, bool IsMe, bool IsTeammate);

    /// <summary>
    ///     Everything a brain gets to see when it has to talk or vote in a meeting.
    /// </summary>
    internal sealed class MeetingContext
    {
        public string MapName { get; set; } = "The Skeld";

        public int MeetingNumber { get; set; }

        public MeetingStage Stage { get; set; }

        public PlayerBrief Me { get; set; } = null!;

        public bool IAmImpostor { get; set; }

        public IReadOnlyList<PlayerBrief> Players { get; set; } = Array.Empty<PlayerBrief>();

        public string Reason { get; set; } = string.Empty;

        public string? BodyRoom { get; set; }

        public PlayerBrief? Reporter { get; set; }

        public PlayerBrief? Body { get; set; }

        /// <summary>Gets or sets what the bot saw or worked out, oldest first, in plain sentences.</summary>
        public IReadOnlyList<string> Observations { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the whereabouts timeline: who was seen where.</summary>
        public IReadOnlyList<string> Sightings { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the things only an impostor knows (its own kills, its teammates).</summary>
        public IReadOnlyList<string> SecretFacts { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> MyTasks { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the rooms this bot walked through recently, oldest first, with how long ago.</summary>
        public IReadOnlyList<string> MyRoute { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the room names of <see cref="MyRoute"/> without timestamps.</summary>
        public IReadOnlyList<string> MyRouteRooms { get; set; } = Array.Empty<string>();

        public IReadOnlyList<string> History { get; set; } = Array.Empty<string>();

        public IReadOnlyList<ChatLine> Chat { get; set; } = Array.Empty<ChatLine>();

        public IReadOnlyDictionary<byte, double> Suspicion { get; set; } = new Dictionary<byte, double>();

        public IReadOnlySet<byte> Caught { get; set; } = new HashSet<byte>();

        public double SecondsLeft { get; set; }

        public int MaxLines { get; set; } = 2;

        public string Persona { get; set; } = string.Empty;

        public IEnumerable<PlayerBrief> Candidates => Players.Where(p => p.Alive && !p.IsMe);

        public PlayerBrief? FindByName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var t = text.Trim().Trim('"', '\'', '.', '!', '?').ToLowerInvariant();
            return Players.FirstOrDefault(p => p.Name.ToLowerInvariant() == t)
                   ?? Players.FirstOrDefault(p => p.Color.ToLowerInvariant() == t)
                   ?? Players.FirstOrDefault(p => t.Contains(p.Name.ToLowerInvariant()));
        }
    }

    /// <summary>
    ///     What a brain decided: lines to say and who to vote for ("skip", a player name or null when undecided).
    /// </summary>
    internal sealed record MeetingDecision(IReadOnlyList<string> Say, string? Vote, string Source);

    internal interface IBrain
    {
        string Name { get; }

        System.Threading.Tasks.ValueTask<MeetingDecision> DecideAsync(MeetingContext context, System.Threading.CancellationToken cancellationToken);
    }
}
