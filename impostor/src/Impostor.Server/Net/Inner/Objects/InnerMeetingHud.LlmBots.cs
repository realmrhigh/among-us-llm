using System;
using System.Collections.Generic;

namespace Impostor.Server.Net.Inner.Objects
{
    internal partial class InnerMeetingHud
    {
        private DateTimeOffset? _votingCompleteAt;

        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        internal IReadOnlyList<PlayerVoteArea> VoteAreas => _playerStates ?? Array.Empty<PlayerVoteArea>();

        /// <summary>
        ///     Gets a value indicating whether the host has closed this meeting. A real client does not always despawn the
        ///     meeting object afterwards, so bots go by the host's CloseMeeting / VotingComplete messages instead.
        /// </summary>
        internal bool HostClosed { get; private set; }

        internal bool IsOver => HostClosed || (_votingCompleteAt is { } t && DateTimeOffset.UtcNow - t > TimeSpan.FromSeconds(8));

        internal void MarkHostClosed() => HostClosed = true;

        internal void MarkVotingComplete() => _votingCompleteAt ??= DateTimeOffset.UtcNow;
    }
}
