using System.Numerics;
using Content.Server._DeepLagoon.Salvage;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Climbing.Components;
using Content.Shared.DoAfter;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem
{
    private bool TryVaultExpeditionObstacle(EntityUid uid, NPCSteeringComponent steering, TransformComponent xform)
    {
        if (!HasComp<ExpeditionLootOriginComponent>(uid) || (steering.Flags & PathFlags.Climbing) == 0 ||
            !TryComp(uid, out ClimbingComponent? climbing) || !climbing.CanClimb)
            return false;
        if (climbing.NextTransition != null || _doAfter.GetStatus(steering.DoAfterId) == DoAfterStatus.Running)
            return true;
        if (climbing.IsClimbing || !steering.Coordinates.IsValid(EntityManager))
            return false;
        var target = _transform.ToMapCoordinates(GetTargetCoordinates(steering));
        var current = _transform.ToMapCoordinates(xform.Coordinates);
        if (target.MapId != current.MapId)
            return false;
        var direction = target.Position - current.Position;
        if (direction.LengthSquared() < 0.01f)
            return false;
        direction = Vector2.Normalize(direction);
        if (!_fixturesQuery.TryGetComponent(uid, out var fixtures) || !_physicsQuery.TryGetComponent(uid, out var npcBody))
            return false;
        var entities = _entSetPool.Get();
        _lookup.GetEntitiesInRange(uid, 0.9f, entities, LookupFlags.Dynamic | LookupFlags.Static);
        try
        {
            foreach (var obstacle in entities)
            {
                if (obstacle == uid || !TryComp(obstacle, out ClimbableComponent? surface) ||
                    !_physicsQuery.TryGetComponent(obstacle, out var body) || !body.CanCollide || !body.Hard ||
                    !_physics.IsCurrentlyHardCollidable((uid, fixtures, npcBody), obstacle))
                    continue;
                var delta = _transform.GetWorldPosition(obstacle) - current.Position;
                var forward = Vector2.Dot(delta, direction);
                // Only vault obstacles across the route, not a crate beside or behind the NPC.
                var sideways = MathF.Abs(delta.X * direction.Y - delta.Y * direction.X);
                if (forward <= 0.05f || forward > 1f || sideways > 0.45f ||
                    !_climb.CanVault(surface, uid, obstacle, out _) ||
                    !_climb.TryClimb(uid, uid, obstacle, out var id, surface, climbing))
                    continue;
                steering.DoAfterId = id;
                return true;
            }
        }
        finally
        {
            _entSetPool.Return(entities);
        }
        return false;
    }
}
