using Content.Shared._Crescent.ShipShields;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Events;

namespace Content.Client._Crescent.ShipShields;

/// <summary>
/// Keeps predicted non-projectile contacts consistent with the server's ship shield filter.
/// </summary>
public sealed class ShipShieldCollisionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        // ShipShieldComponent is server-only; the replicated visual component identifies shields here.
        SubscribeLocalEvent<ShipShieldVisualsComponent, PreventCollideEvent>(OnPreventCollide);
    }

    private void OnPreventCollide(Entity<ShipShieldVisualsComponent> ent, ref PreventCollideEvent args)
    {
        // Characters and their sensors pass through shields. In particular, a flammable sensor can
        // overlap multiple proxies of the same chain fixture during client prediction.
        if (!HasComp<ProjectileComponent>(args.OtherEntity))
            args.Cancelled = true;
    }
}
