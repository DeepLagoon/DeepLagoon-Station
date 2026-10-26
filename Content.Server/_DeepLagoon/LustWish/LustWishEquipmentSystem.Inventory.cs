using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared._DeepLagoon.LustWish;
using Content.Shared.Inventory.Events;

namespace Content.Server._DeepLagoon.LustWish;

public sealed partial class LustWishEquipmentSystem
{
    private readonly HashSet<(EntityUid User, EntityUid Target, EntityUid Item)> _approvedNative = new();
    private void InitializeNativeInventory()
    {
        SubscribeLocalEvent<LustWishAccessoryComponent, BeingEquippedAttemptEvent>((uid, comp, ev) =>
            CheckNative(uid, comp, ev.Equipee, ev.EquipTarget, ev.Slot, "attach", ev));
        SubscribeLocalEvent<LustWishAccessoryComponent, BeingUnequippedAttemptEvent>((uid, comp, ev) =>
            CheckNative(uid, comp, ev.Unequipee, ev.UnEquipTarget, ev.Slot, "remove", ev));
    }

    private void CheckNative(EntityUid item, LustWishAccessoryComponent component, EntityUid user, EntityUid target,
        string slot, string operation, CancellableEntityEventArgs attempt)
    {
        if (user == target || component.InventorySlots.Length == 0 || !component.InventorySlots.Contains(slot) || _approvedNative.Contains((user,target,item))) return;
        if (_blocked.Contains((target, user)) || Consent(user) == InteractionPanelConsent.No || Consent(target) == InteractionPanelConsent.No) { attempt.Cancel(); return; }
        if (Consent(target) == InteractionPanelConsent.Yes) return;
        attempt.Cancel();
        Request(user, target, item, _hands.GetActiveItem(user), operation, slot, "", false, true);
    }
}
