using System.Numerics;
using Content.Shared.Physics;
using Content.Shared.Tag;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._DeepLagoon.Weapons;

/// <summary>Optical transmission through real window fixtures along a laser's path.</summary>
public sealed class LaserWindowSystem : EntitySystem
{
    public const float WindowTransmission = 0.75f;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TagSystem _tags = default!;

    public float Transmission(in HitscanRaycastFiredEvent shot, EntityUid target)
    {
        var from = _transform.ToMapCoordinates(shot.FromCoordinates);
        var to = _transform.GetMapCoordinates(target);
        if (from.MapId != to.MapId)
            return 1f;

        var distance = MathF.Min(shot.DistanceTried,
            Vector2.Dot(to.Position - from.Position, shot.ShotDirection));
        return Transmission(from, shot.ShotDirection, distance, target);
    }

    public float Transmission(MapCoordinates from, Vector2 direction, float distance,
        EntityUid? target = null, HashSet<EntityUid>? crossed = null)
    {
        if (!float.IsFinite(distance) || distance <= 0.001f)
            return 1f;

        var windows = new HashSet<EntityUid>();
        var ray = new CollisionRay(from.Position, direction, (int) CollisionGroup.BulletImpassable);
        foreach (var hit in _physics.IntersectRay(from.MapId, ray, distance, returnOnFirstHit: false))
        {
            // The window being hit has not been passed. Multiple fixtures count only once.
            if (hit.HitEntity == target || hit.Distance <= 0.001f || !_tags.HasTag(hit.HitEntity, "Window"))
                continue;
            if (crossed != null && !crossed.Add(hit.HitEntity))
                continue;
            windows.Add(hit.HitEntity);
        }

        return MathF.Pow(WindowTransmission, windows.Count);
    }
}
