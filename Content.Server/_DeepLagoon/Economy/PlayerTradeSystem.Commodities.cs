using System.Linq;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Economy;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Economy;

public sealed partial class PlayerTradeSystem
{
    private bool IsPlainCommodity(EntityUid root, EntityUid[] entities, TradeItem item)
    {
        if (TradeCatalogue.Commodity(item.Prototype) == null || item.Condition != 100 || item.HasBattery == true ||
            !_prototypes.TryIndex<EntityPrototype>(item.Prototype, out var prototype) || Name(root) != prototype.Name)
            return false;
        if (!TryComp<SolutionContainerManagerComponent>(root, out var manager)) return entities.Length == 1;
        if (!prototype.TryComp<SolutionContainerManagerComponent>(out var defaults, EntityManager.ComponentFactory) || defaults.Solutions == null) return false;
        var solutions = _solutions.EnumerateSolutions((root, manager)).ToArray();
        // Raw sheets have chemical composition entities. They are per-sheet data, not cargo stored inside a lot.
        // Only the exact prototype composition/temperature can enter automatic interchangeable-material purchases.
        if (solutions.Length != defaults.Solutions.Count || entities.Any(uid => uid != root && !solutions.Any(s => s.Solution.Owner == uid))) return false;
        foreach (var (name, entity) in solutions)
        {
            if (name == null) return false;
            var expected = defaults.Solutions.GetValueOrDefault(name);
            if (expected == null) return false;
            var actual = entity.Comp.Solution;
            if (actual.Temperature != expected.Temperature || actual.CanReact != expected.CanReact ||
                actual.Contents.Count != expected.Contents.Count ||
                !expected.Contents.All(r => actual.Contents.Any(a => a.Quantity == r.Quantity && a.Reagent.Equals(r.Reagent)))) return false;
        }
        return true;
    }
}
