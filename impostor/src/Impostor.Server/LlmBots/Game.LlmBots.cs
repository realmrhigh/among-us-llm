using System.Collections.Generic;
using System.Linq;
using Impostor.Server.Net.Inner;
using Impostor.Server.Net.Inner.Objects;

namespace Impostor.Server.Net.State
{
    /// <summary>
    ///     Read access to the internals of a game for the bot subsystem.
    /// </summary>
    internal partial class Game
    {
        internal IEnumerable<InnerNetObject> AllNetObjects => _allObjects.Values;

        internal InnerMeetingHud? ActiveMeeting => _allObjects.Values.OfType<InnerMeetingHud>().FirstOrDefault();

        internal uint NextFreeNetId() => System.Threading.Interlocked.Increment(ref _nextNetId);
    }
}
