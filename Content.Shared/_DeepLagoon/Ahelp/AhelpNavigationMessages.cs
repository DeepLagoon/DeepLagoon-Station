using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Ahelp;

[Serializable, NetSerializable]
public sealed class AhelpTeleportRequest(Guid player, string target = "") : EntityEventArgs
{
    public readonly Guid Player = player;
    public readonly string Target = target;
}

[Serializable, NetSerializable]
public sealed class AhelpTeleportState(Guid player, string shipError, string bodyError, string ghostError, string result = "") : EntityEventArgs
{
    public readonly Guid Player = player;
    public readonly string ShipError = shipError;
    public readonly string BodyError = bodyError;
    public readonly string GhostError = ghostError;
    public readonly string Result = result;
}
