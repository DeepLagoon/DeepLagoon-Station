using Content.Server.Cargo.Components;
using Content.Shared.Materials;
using Content.Shared.Stacks;
using Robust.Shared.Containers;

namespace Content.Server.Cargo.Systems;

public sealed partial class PricingSystem
{
    internal const double MaxCraftedItemPrice = 500;

    internal double? GetCraftedUnitLimit(EntityUid uid)
    {
        return CompOrNull<CraftedPriceComponent>(uid)?.UnitLimit;
    }

    private double GetCraftedPriceLimit(EntityUid uid)
    {
        return GetCraftedUnitLimit(uid) is { } limit
            ? limit * (CompOrNull<StackComponent>(uid)?.Count ?? 1)
            : double.PositiveInfinity;
    }

    internal void MarkCraftedProduct(EntityUid uid, double limit = MaxCraftedItemPrice, HashSet<string>? retainedContainers = null)
    {
        // Stock resources can be pooled into material storage and are priced as commodities.
        if (HasComp<MaterialComponent>(uid) && HasComp<StackComponent>(uid))
            return;

        EnsureComp<CraftedPriceComponent>(uid).UnitLimit = limit;

        if (!TryComp<ContainerManagerComponent>(uid, out var containers))
            return;

        foreach (var (id, container) in containers.Containers)
        {
            if (retainedContainers?.Contains(id) == true)
                continue;

            foreach (var part in container.ContainedEntities)
                MarkCraftedProduct(part, 0);
        }
    }
}
