using System.Linq;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishEquipmentSystem
{
    private bool CanColor(EntityUid user, EntityUid item) => Alive(user) && _blocker.CanConsciouslyPerformAction(user) &&
        (_hands.IsHolding(user, item, out _) || Attached(user).Any(a => a.Uid == item) ||
         TryComp<ClothingComponent>(item, out var clothing) && _inventory.TryGetContainingSlot(item, out var slot) &&
         (clothing.Slots & slot.SlotFlags) != SlotFlags.NONE && Transform(item).ParentUid == user);

    private void OpenColorPicker(EntityUid item, LustWishPaletteComponent palette, EntityUid user)
    {
        if (CanColor(user, item) && TryComp<ActorComponent>(user, out var actor))
            RaiseNetworkEvent(new LustWishColorPickerEvent(GetNetEntity(item), Name(item), palette.Color), actor.PlayerSession);
    }

    private void OnChooseColor(Entity<LustWishPaletteComponent> ent, ref LustWishChooseColorEvent ev)
    {
        if (ev.Handled || !CanColor(ev.Performer, ent)) return;
        ev.Handled = true;
        OpenColorPicker(ent, ent.Comp, ev.Performer);
    }

    private void OnSetColor(LustWishSetColorEvent ev, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user || !TryGetEntity(ev.Item, out var item) || item is not { } uid ||
            !TryComp<LustWishPaletteComponent>(uid, out var palette) || !CanColor(user, uid) || ev.Color.Length != 7 ||
            !Color.TryFromHex(ev.Color, out var color)) return;
        palette.Color = color.WithAlpha(1f); Dirty(uid, palette);
    }
}
