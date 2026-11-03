using Content.Shared.Shuttles.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;

namespace Content.Server.Shuttles.Systems;

/// <summary>
/// The hyperspace map displays independent journeys; it is not a shared flight path.
/// Keep hull contacts there from turning an overlapping transit slot into an impact.
/// </summary>
public sealed class FTLTransitCollisionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MapGridComponent, PreventCollideEvent>(OnPreventCollide);
    }

    private void OnPreventCollide(Entity<MapGridComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled || !HasComp<MapGridComponent>(args.OtherEntity))
            return;

        var map = Transform(ent).MapUid;
        if (map != null && HasComp<FTLMapComponent>(map) && Transform(args.OtherEntity).MapUid == map)
            args.Cancelled = true;
    }
}
