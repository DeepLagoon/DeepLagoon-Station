using Content.Server.Atmos.Components;
using Content.Shared._Goobstation.Clothing.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Inventory;

namespace Content.Server._DeepLagoon.Chemistry;

/// <summary>
/// Keeps external reagents off the wearer of a closed pressure suit.
/// </summary>
public sealed class ReagentProtectionSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ReactiveComponent, ReagentTouchAttemptEvent>(OnReagentTouchAttempt);
    }

    private void OnReagentTouchAttempt(Entity<ReactiveComponent> ent, ref ReagentTouchAttemptEvent args)
    {
        if (IsSealedPressureClothing(ent, "outerClothing") && IsSealedPressureClothing(ent, "head"))
            args.Cancelled = true;
    }

    private bool IsSealedPressureClothing(EntityUid wearer, string slot)
    {
        if (!_inventory.TryGetSlotEntity(wearer, slot, out var clothing) ||
            !HasComp<PressureProtectionComponent>(clothing))
            return false;

        // MODsuits gain pressure protection when their parts seal. Check the seal as well
        // so an open part can never provide contact protection.
        return !TryComp<SealableClothingComponent>(clothing, out var seal) || seal.IsSealed;
    }
}
