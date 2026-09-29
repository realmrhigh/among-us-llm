using System;
using System.Collections.Generic;
using System.Numerics;

namespace Impostor.Server.LlmBots
{
    internal readonly record struct PlayerPos(byte Id, Vector2 Position, bool Dead);

    /// <summary>
    ///     Something that happened in a game. Events carry the positions of everyone at that moment so each bot can
    ///     work out afterwards what it could have seen.
    /// </summary>
    internal abstract record WorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot);

    internal sealed record MurderWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, byte KillerId, byte VictimId, Vector2 Position) : WorldEvent(Time, Snapshot);

    internal sealed record VentWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, byte PlayerId, bool Entered, string VentName, Vector2 Position) : WorldEvent(Time, Snapshot);

    internal sealed record MeetingStartedWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, byte? ReporterId, byte? BodyId, Vector2? BodyPosition) : WorldEvent(Time, Snapshot);

    internal sealed record ChatWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, byte SenderId, string Text) : WorldEvent(Time, Snapshot);

    internal sealed record MeetingEndedWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, byte? ExiledId, bool Tie, IReadOnlyList<(byte Voter, byte Target)> Votes) : WorldEvent(Time, Snapshot);

    internal sealed record GameStartedWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot) : WorldEvent(Time, Snapshot);

    internal sealed record GameEndedWorldEvent(DateTime Time, IReadOnlyList<PlayerPos> Snapshot, string Reason) : WorldEvent(Time, Snapshot);

    internal sealed record BodyRecord(byte VictimId, Vector2 Position, DateTime Time);
}
