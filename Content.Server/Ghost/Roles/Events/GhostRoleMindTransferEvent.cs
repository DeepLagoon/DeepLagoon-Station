using Robust.Shared.Player;

namespace Content.Server.Ghost.Roles.Events;

/// <summary>Raised on the spawned body before a ghost role replaces its player's mind.</summary>
public sealed class GhostRoleMindTransferEvent(ICommonSession player) : EntityEventArgs
{
    public readonly ICommonSession Player = player;
}
