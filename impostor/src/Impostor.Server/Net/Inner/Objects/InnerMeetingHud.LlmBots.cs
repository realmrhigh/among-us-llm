using System;
using System.Collections.Generic;

namespace Impostor.Server.Net.Inner.Objects
{
    internal partial class InnerMeetingHud
    {
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        internal IReadOnlyList<PlayerVoteArea> VoteAreas => _playerStates ?? Array.Empty<PlayerVoteArea>();
    }
}
