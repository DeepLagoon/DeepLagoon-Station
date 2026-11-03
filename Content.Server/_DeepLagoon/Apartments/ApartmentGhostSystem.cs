using System.Numerics;
using Content.Server.GameTicking;
using Content.Shared._DeepLagoon.Apartments;
using Content.Shared.Ghost;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.Apartments;

/// <summary>Marks the standalone residential stop map, including the space outside its grid.</summary>
[RegisterComponent]
public sealed partial class ApartmentGhostRestrictedMapComponent : Component;

/// <summary>Returns ordinary observers from residential areas to the normal observer spawn.</summary>
public sealed class ApartmentGhostSystem : EntitySystem
{
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private FollowerSystem _followers = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Include sleeping apartments and ghosts spawned directly on paused maps.
        // Checking every update also covers following, teleporting and losing admin-ghost mode.
        var query = AllEntityQuery<GhostComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var ghost, out var transform))
        {
            if (TerminatingOrDeleted(uid) || ghost.CanGhostInteract || !Restricted(transform))
                continue;

            if (TryGetReturnPoint(out var destination))
            {
                if (TryComp<FollowerComponent>(uid, out var follower))
                    _followers.StopFollowingEntity(uid, follower.Following, deparent: false);
                _transform.SetCoordinates(uid, destination);
                _transform.AttachToGridOrMap(uid);
            }
        }
    }

    private bool Restricted(TransformComponent transform) =>
        HasComp<ApartmentIsolationComponent>(transform.MapUid) ||
        HasComp<ApartmentGhostRestrictedMapComponent>(transform.MapUid) ||
        HasComp<ApartmentProtectedGridComponent>(transform.GridUid);

    private bool ValidDestination(EntityCoordinates coordinates) =>
        coordinates.IsValid(EntityManager) &&
        TryComp(coordinates.EntityId, out TransformComponent? transform) &&
        transform.MapUid is { } map && !TerminatingOrDeleted(map) &&
        !_maps.IsPaused(map) && !Restricted(transform) &&
        (!_maps.TryFindGridAt(_transform.ToMapCoordinates(coordinates), out var grid, out _) ||
         !HasComp<ApartmentProtectedGridComponent>(grid));

    private bool TryGetReturnPoint(out EntityCoordinates coordinates)
    {
        coordinates = _ticker.GetObserverSpawnPoint();
        if (ValidDestination(coordinates))
            return true;

        // The ticker's fallback can randomly select an apartment grid. Never return there again.
        var grids = EntityQueryEnumerator<MapGridComponent>();
        while (grids.MoveNext(out var grid, out _))
        {
            coordinates = new EntityCoordinates(grid, Vector2.Zero);
            if (ValidDestination(coordinates))
                return true;
        }

        if (_maps.TryGetMap(_ticker.DefaultMap, out var defaultMap) && defaultMap is { } mapUid)
        {
            coordinates = new EntityCoordinates(mapUid, Vector2.Zero);
            if (ValidDestination(coordinates))
                return true;
        }

        var maps = EntityQueryEnumerator<MapComponent>();
        while (maps.MoveNext(out var map, out _))
        {
            coordinates = new EntityCoordinates(map, Vector2.Zero);
            if (ValidDestination(coordinates))
                return true;
        }

        coordinates = EntityCoordinates.Invalid;
        return false;
    }
}
