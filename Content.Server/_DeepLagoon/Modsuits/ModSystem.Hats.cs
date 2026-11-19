using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Clothing.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Verbs;
using Robust.Shared.Containers;

namespace Content.Server._DeepLagoon.Modsuits;

[RegisterComponent]
public sealed partial class ModHatStabilizerComponent : Component;

// WhiteMoon supplies this inbuilt module. Hearing-organ damage itself is not
// implemented by this content fork; keep the protection capability explicit.
[RegisterComponent]
public sealed partial class ModHearingProtectionComponent : Component;

public sealed partial class ModSystem
{
    private void InitializeHats()
    {
        SubscribeLocalEvent<ModHatStabilizerComponent, InteractUsingEvent>((Entity<ModHatStabilizerComponent> helmet, ref InteractUsingEvent args) =>
        {
            if (!args.Handled) args.Handled = MountHat(helmet, args.User, args.Used);
        }, before: [typeof(Content.Server.Storage.EntitySystems.StorageSystem)]);
        SubscribeLocalEvent<ModHatStabilizerComponent, GetVerbsEvent<AlternativeVerb>>((Entity<ModHatStabilizerComponent> helmet, ref GetVerbsEvent<AlternativeVerb> args) =>
        {
            if (!args.CanInteract || !args.CanAccess || args.Using is not {} held) return;
            var user = args.User;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("mod-mount-hat"), Act = () => MountHat(helmet, user, held) });
        });
        SubscribeLocalEvent<ModMountedHatComponent, EntGotRemovedFromContainerMessage>((Entity<ModMountedHatComponent> hat, ref EntGotRemovedFromContainerMessage args) => RemComp<ModMountedHatComponent>(hat));
    }

    public bool MountHat(EntityUid helmet, EntityUid user, EntityUid hat)
    {
        if (!HasComp<ModHatStabilizerComponent>(helmet) || !_blocker.CanInteract(user, helmet) ||
            !_inventory.TryGetSlotEntity(user, "head", out var head) || head != helmet ||
            _inventory.TryGetSlotEntity(user, "helmetcover", out _) ||
            !TryComp<ClothingComponent>(hat, out var clothing) || (clothing.Slots & SlotFlags.HEAD) == 0 ||
            !_hands.IsHolding(user, hat) || !_hands.TryDrop(user, hat)) return false;
        EnsureComp<ModMountedHatComponent>(hat);
        if (_inventory.TryEquip(user, hat, "helmetcover", force: true)) return true;
        RemComp<ModMountedHatComponent>(hat);
        _hands.TryPickupAnyHand(user, hat);
        return false;
    }
}
