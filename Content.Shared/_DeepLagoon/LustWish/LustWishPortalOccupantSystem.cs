using Robust.Shared.Physics.Events;

namespace Content.Shared._DeepLagoon.LustWish;

public sealed class LustWishPortalOccupantSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishPortalOccupantComponent, PreventCollideEvent>((Entity<LustWishPortalOccupantComponent> ent, ref PreventCollideEvent ev) =>
        {
            if (TryComp<LustWishPortalComponent>(ent.Comp.Entrance, out var portal) && ev.OtherEntity == portal.Support)
                ev.Cancelled = true;
        });
    }
}
