using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared._DeepLagoon.Apartments;

[RegisterComponent, NetworkedComponent]
public sealed partial class ApartmentIsolationComponent : Component;

/// <summary>One policy for gameplay teleportation; apartment travel uses its own validated transport.</summary>
public sealed class ApartmentTeleportSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _maps = default!;

    public bool Restricted(EntityUid entity) => TryComp<TransformComponent>(entity, out var transform) &&
        (HasComp<ApartmentProtectedGridComponent>(transform.GridUid) || HasComp<ApartmentIsolationComponent>(transform.MapUid));

    public bool CanTeleport(EntityUid subject) => !Restricted(subject);

    public bool CanTeleport(EntityUid subject, EntityCoordinates target) => !Restricted(subject) &&
        !Restricted(target.EntityId) && CanTeleport(subject, _transform.ToMapCoordinates(target));

    public bool CanTeleport(EntityUid subject, MapCoordinates target) => !Restricted(subject) &&
        (!_maps.TryGetMap(target.MapId, out var map) || !HasComp<ApartmentIsolationComponent>(map)) &&
        (!_maps.TryFindGridAt(target, out var grid, out _) || !HasComp<ApartmentProtectedGridComponent>(grid));
}
