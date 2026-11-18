using System.Numerics;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

public sealed partial class OrbitalDebrisSystem
{
    // Leave time for the next radar request and network delivery before the eight-second reaction window.
    private const float SpawnProtectionSeconds = ReactionSeconds + 2f;
    private const float RadarWarningSeconds = SpawnProtectionSeconds + 2f;
    private const float HullClearance = 16f;
    private const float MaximumDebrisSpeed = 8f; // Tangential speed (6) plus orbital correction (2).
    private readonly record struct TrafficHull(EntityUid Uid, EntityUid Map, Vector2 Center, Vector2 Travel,
        float Radius, Vector2 SweepMin, Vector2 SweepMax);
    private readonly record struct GridFootprint(EntityUid Uid, EntityUid Map, Vector2 Center, float ClearanceSquared);
    private readonly List<TrafficHull> _traffic = new();
    private readonly List<GridFootprint> _gridFootprints = new();
    private TimeSpan _trafficTime = TimeSpan.MinValue;

    private void RefreshTraffic()
    {
        if (_trafficTime == _timing.CurTime) return;
        _trafficTime = _timing.CurTime;
        _traffic.Clear();
        _gridFootprints.Clear();
        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var grid, out var xform))
        {
            if (TerminatingOrDeleted(uid) || xform.MapUid is not { } map) continue;
            var (position, rotation) = _transform.GetWorldPositionRotation(xform);
            var halfDiagonal = grid.LocalAABB.Size.Length() * 0.5f;
            var gridCenter = position + rotation.RotateVec(grid.LocalAABB.Center);
            var gridClearance = halfDiagonal + 8f + HullClearance;
            _gridFootprints.Add(new GridFootprint(uid, map, gridCenter, gridClearance * gridClearance));
            if (_piles.ContainsKey(uid) && !_occupied.Contains(uid)
                || !TryComp<PhysicsComponent>(uid, out var body)) continue;
            // Physics velocity translates the center of mass. A circle about it contains the hull at every rotation,
            // including ships whose grid origin is far away from their actual tiles.
            var center = position + rotation.RotateVec(body.LocalCenter);
            var radius = (grid.LocalAABB.Center - body.LocalCenter).Length() + halfDiagonal;
            var travel = _physics.GetMapLinearVelocity(uid, body, xform) * SpawnProtectionSeconds;
            var padding = new Vector2(radius);
            _traffic.Add(new TrafficHull(uid, map, center, travel, radius,
                Vector2.Min(center, center + travel) - padding, Vector2.Max(center, center + travel) + padding));
        }
    }

    private bool SpawnTrajectoryClear(EntityUid map, Vector2 position, float extent, Vector2 anchor,
        float speed, float direction, EntityUid? ignore = null)
    {
        RefreshTraffic();
        var radial = position - anchor;
        var normal = Normal(radial);
        var velocity = new Vector2(-normal.Y, normal.X) * (speed * direction);
        var travel = velocity * SpawnProtectionSeconds;
        // Bound the departure of a circular orbit from its initial tangent over the whole prediction window.
        var radius = MathF.Max(1f, radial.Length());
        var curvature = speed * speed * SpawnProtectionSeconds * SpawnProtectionSeconds / (2f * radius);
        var padding = extent + HullClearance + curvature;
        var sweepMin = Vector2.Min(position, position + travel) - new Vector2(padding);
        var sweepMax = Vector2.Max(position, position + travel) + new Vector2(padding);
        foreach (var hull in _traffic)
        {
            // Reject disjoint swept bounds before entity lookups, projection and division. A ship moving
            // into the area is included even when its current position is far outside it.
            if (hull.Map != map || sweepMax.X < hull.SweepMin.X || sweepMin.X > hull.SweepMax.X
                || sweepMax.Y < hull.SweepMin.Y || sweepMin.Y > hull.SweepMax.Y
                || hull.Uid == ignore || TerminatingOrDeleted(hull.Uid)) continue;
            var relative = position - hull.Center;
            var sweep = travel - hull.Travel;
            var sweepSquared = sweep.LengthSquared();
            var fraction = sweepSquared > 0.0001f
                ? Math.Clamp(-Vector2.Dot(relative, sweep) / sweepSquared, 0f, 1f) : 0f;
            var clearance = hull.Radius + padding;
            if ((relative + sweep * fraction).LengthSquared() <= clearance * clearance) return false;
        }
        return true;
    }
}
