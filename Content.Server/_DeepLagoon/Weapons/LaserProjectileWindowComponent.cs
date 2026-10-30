using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.Weapons;

/// <summary>Tracks glass crossed by a moving laser bolt, independently of its opaque collision mask.</summary>
[RegisterComponent]
public sealed partial class LaserProjectileWindowComponent : Component
{
    public MapCoordinates? Previous;
    public readonly HashSet<EntityUid> Crossed = new();
}
