using Content.Shared.Actions;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;

namespace Content.Shared._DeepLagoon.LustWish;

public sealed partial class LustWishPaletteSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<LustWishPaletteComponent, GetItemActionsEvent>((Entity<LustWishPaletteComponent> ent, ref GetItemActionsEvent ev) =>
        {
            if (ev.InHands || TryComp<ClothingComponent>(ent, out var clothing) && ev.SlotFlags is { } flags && (clothing.Slots & flags) != SlotFlags.NONE)
            {
                ev.AddAction(ref ent.Comp.ColorAction, "ActionLustWishChooseColor");
                Dirty(ent, ent.Comp);
            }
        });
    }
}
