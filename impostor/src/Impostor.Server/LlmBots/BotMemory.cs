using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Impostor.Server.LlmBots
{
    internal sealed record Sighting(byte PlayerId, string Room, Vector2 Position, DateTime Time);

    internal sealed record MemoryNote(DateTime Time, string Text, byte? About, int Weight);

    internal sealed record ChatLine(DateTime Time, byte SenderId, string SenderName, string Text);

    /// <summary>
    ///     What a single bot has seen, heard and concluded during the current game.
    /// </summary>
    internal sealed class BotMemory
    {
        public DateTime GameStart { get; set; } = DateTime.UtcNow;

        /// <summary>Gets the last place every player was seen at.</summary>
        public Dictionary<byte, Sighting> LastSeen { get; } = new();

        /// <summary>Gets a running timeline of where players were seen, oldest first.</summary>
        public List<Sighting> Timeline { get; } = new();

        /// <summary>Gets everything the bot witnessed or was told.</summary>
        public List<MemoryNote> Notes { get; } = new();

        /// <summary>Gets the players the bot knows are dead.</summary>
        public HashSet<byte> KnownDead { get; } = new();

        /// <summary>Gets the players the bot has caught red handed (saw a kill or a vent).</summary>
        public HashSet<byte> CaughtImpostors { get; } = new();

        /// <summary>Gets the players that were voted out and turned out to be crew or impostor.</summary>
        public Dictionary<byte, bool> ExiledWasImpostor { get; } = new();

        /// <summary>Gets the bodies the bot has seen since the last meeting.</summary>
        public Dictionary<byte, BodyRecord> KnownBodies { get; } = new();

        /// <summary>Gets bodies this bot already reported or decided to ignore.</summary>
        public HashSet<byte> HandledBodies { get; } = new();

        /// <summary>Gets the chat of the current meeting.</summary>
        public List<ChatLine> Chat { get; } = new();

        /// <summary>Gets a per player suspicion score, higher is more suspicious.</summary>
        public Dictionary<byte, double> Suspicion { get; } = new();

        /// <summary>Gets the kills this bot committed (impostors only).</summary>
        public List<(byte Victim, string Room, DateTime Time)> MyKills { get; } = new();

        /// <summary>Gets what happened in earlier meetings of this game.</summary>
        public List<string> History { get; } = new();

        /// <summary>Gets the number of meetings that happened so far.</summary>
        public int MeetingsHeld { get; set; }

        public void Note(DateTime time, string text, byte? about = null, int weight = 1)
        {
            Notes.Add(new MemoryNote(time, text, about, weight));
            if (Notes.Count > 80)
            {
                Notes.RemoveRange(0, Notes.Count - 80);
            }
        }

        public void AddSuspicion(byte id, double amount)
        {
            Suspicion[id] = Suspicion.GetValueOrDefault(id) + amount;
        }

        public void RecordSighting(Sighting s)
        {
            LastSeen.TryGetValue(s.PlayerId, out var previous);
            LastSeen[s.PlayerId] = s;

            if (previous == null || previous.Room != s.Room || (s.Time - previous.Time).TotalSeconds > 20)
            {
                var last = Timeline.LastOrDefault(t => t.PlayerId == s.PlayerId);
                if (last == null || last.Room != s.Room || (s.Time - last.Time).TotalSeconds > 20)
                {
                    Timeline.Add(s);
                    if (Timeline.Count > 60)
                    {
                        Timeline.RemoveRange(0, Timeline.Count - 60);
                    }
                }
            }
        }

        public void Reset(DateTime now)
        {
            GameStart = now;
            LastSeen.Clear();
            Timeline.Clear();
            Notes.Clear();
            KnownDead.Clear();
            CaughtImpostors.Clear();
            ExiledWasImpostor.Clear();
            KnownBodies.Clear();
            HandledBodies.Clear();
            Chat.Clear();
            Suspicion.Clear();
            MyKills.Clear();
            MeetingsHeld = 0;
            History.Clear();
        }

        public static string Ago(DateTime now, DateTime then)
        {
            var seconds = Math.Max(0, (int)(now - then).TotalSeconds);
            return seconds < 90 ? $"{seconds}s ago" : $"{seconds / 60}m{seconds % 60:00}s ago";
        }
    }
}
