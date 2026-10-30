using System.Numerics;
using Content.Server._NF.Shuttles.Components;
using Content.Server.Warps;
using Content.Shared.Station.Components;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.Map.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

/// <summary>Protects POIs even when they have no station entity or are mobile.</summary>
[RegisterComponent]
public sealed partial class SpaceJunkExclusionComponent : Component;

public sealed partial class OrbitalDebrisSystem
{
    public const float InhabitedClearance = 1000f;
    private const float OrbitMargin = 40f;
    private readonly record struct ProtectedZone(EntityUid Map, Vector2 Center, float Radius);
    private readonly List<ProtectedZone> _protected = new();
    private readonly HashSet<EntityUid> _protectedGrids = new();

    public void RefreshExclusions()
    {
        _protected.Clear();
        _protectedGrids.Clear();
        var stations = EntityQueryEnumerator<StationDataComponent>();
        while (stations.MoveNext(out _, out var station))
            foreach (var uid in station.Grids) Protect(uid);
        var members = EntityQueryEnumerator<StationMemberComponent, MapGridComponent>();
        while (members.MoveNext(out var uid, out _, out _)) Protect(uid);
        var pois = EntityQueryEnumerator<SpaceJunkExclusionComponent, MapGridComponent>();
        while (pois.MoveNext(out var uid, out _, out _)) Protect(uid, true);
        var anchored = EntityQueryEnumerator<ForceAnchorComponent, MapGridComponent>();
        while (anchored.MoveNext(out var uid, out _, out _)) Protect(uid, true);
        var warps = EntityQueryEnumerator<WarpPointComponent, TransformComponent>();
        while (warps.MoveNext(out _, out _, out var transform))
            if (transform.GridUid is { } grid) Protect(grid);
    }

    private void Protect(EntityUid uid, bool explicitLocation = false)
    {
        // Purchased ships may have a station entity for late joining and a warp point.
        // Those are not inhabited-space exclusion zones. Explicit POI markers still win.
        if (_piles.ContainsKey(uid) || !explicitLocation && !HasComp<SpaceJunkExclusionComponent>(uid)
            && !HasComp<ForceAnchorComponent>(uid)
            && (HasComp<ShipOwnershipComponent>(uid) || HasComp<ShuttleDeedComponent>(uid))) return;
        if (!_protectedGrids.Add(uid) || TerminatingOrDeleted(uid) || !TryComp<MapGridComponent>(uid, out var grid)
            || !TryComp<TransformComponent>(uid, out var transform) || transform.MapUid is not { } map) return;
        var (position, rotation) = _transform.GetWorldPositionRotation(transform);
        var center = position + rotation.RotateVec(grid.LocalAABB.Center);
        _protected.Add(new ProtectedZone(map, center, grid.LocalAABB.Size.Length() * 0.5f + InhabitedClearance));
    }

    public bool SafeOrbit(EntityUid map, Vector2 position, Vector2 anchor)
    {
        RefreshExclusions();
        return PositionClear(map, position) && OrbitClear(map, anchor, Vector2.Distance(position, anchor));
    }

    private bool OrbitClear(EntityUid map, Vector2 anchor, float radius)
    {
        foreach (var zone in _protected)
            if (zone.Map == map && MathF.Abs(Vector2.Distance(anchor, zone.Center) - radius) <= zone.Radius + OrbitMargin)
                return false;
        return true;
    }

    private bool PositionClear(EntityUid map, Vector2 position)
    {
        foreach (var zone in _protected)
            if (zone.Map == map && Vector2.DistanceSquared(position, zone.Center) <= MathF.Pow(zone.Radius + OrbitMargin, 2)) return false;
        return true;
    }

    private bool PathClear(EntityUid map, Vector2 start, Vector2 end)
    {
        var delta = end - start;
        foreach (var zone in _protected)
        {
            if (zone.Map != map) continue;
            var fraction = delta.LengthSquared() > 0.001f ? Math.Clamp(Vector2.Dot(zone.Center - start, delta) / delta.LengthSquared(), 0f, 1f) : 0f;
            if (Vector2.DistanceSquared(zone.Center, start + delta * fraction) <= MathF.Pow(zone.Radius + OrbitMargin, 2)) return false;
        }
        return true;
    }

    private float HabitationDistance(Observer observer)
    {
        var distance = float.PositiveInfinity;
        foreach (var zone in _protected)
            if (zone.Map == observer.Map) distance = MathF.Min(distance,
                MathF.Max(0, Vector2.Distance(observer.Position, zone.Center) - zone.Radius + InhabitedClearance));
        return distance;
    }

    private bool NearGrid(EntityUid map, Vector2 position)
    {
        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out _, out var grid, out var transform))
            if (transform.MapUid == map && Vector2.Distance(position, _transform.GetWorldPosition(transform))
                < grid.LocalAABB.Size.Length() + 16f) return true;
        return false;
    }
}
