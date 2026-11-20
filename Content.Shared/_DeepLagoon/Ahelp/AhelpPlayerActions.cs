using Robust.Shared.Serialization;

namespace Content.Shared._DeepLagoon.Ahelp;

[Serializable, NetSerializable]
public sealed class AhelpPlayerActionRequest(Guid player, string action) : EntityEventArgs
{
    public readonly Guid Player = player;
    public readonly string Action = action;
}

[Serializable, NetSerializable]
public sealed class AhelpPlayerActionResult(Guid player, NetEntity? entity = null, string error = "") : EntityEventArgs
{
    public readonly Guid Player = player;
    public readonly NetEntity? Entity = entity;
    public readonly string Error = error;
}
