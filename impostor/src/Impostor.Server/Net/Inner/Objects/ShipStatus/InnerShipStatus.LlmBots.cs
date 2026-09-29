using System.Collections.Generic;
using Impostor.Api.Innersloth;
using Impostor.Server.Net.Inner.Objects.Systems;

namespace Impostor.Server.Net.Inner.Objects.ShipStatus
{
    internal abstract partial class InnerShipStatus
    {
        internal IReadOnlyDictionary<SystemTypes, ISystemType> Systems => _systems;
    }
}
