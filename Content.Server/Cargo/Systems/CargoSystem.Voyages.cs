using System.Linq;
using Content.Server.Cargo.Components;
using Content.Shared.Cargo;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Stacks;
using Content.Shared.Chemistry.Components.SolutionManager;
using Robust.Shared.Containers;

namespace Content.Server.Cargo.Systems;
public sealed partial class CargoSystem
{
    public bool TakeVoyageBounty(EntityUid station, CargoBountyData bounty)
    {
        if (_economyBounties.Contains((station, bounty.Id)) || !TryComp<StationCargoBountyDatabaseComponent>(station, out var db)) return false;
        var index = db.Bounties.FindIndex(b => b.Id == bounty.Id && b.Instance == bounty.Instance);
        if (index < 0) return false;
        db.Bounties.RemoveAt(index); UpdateBountyConsoles(); return true;
    }
    public void RestoreVoyageBounty(EntityUid station, CargoBountyData bounty)
    {
        if (TryComp<StationCargoBountyDatabaseComponent>(station, out var db) && db.Bounties.All(b => b.Id != bounty.Id))
        { db.Bounties.Add(bounty); UpdateBountyConsoles(); }
    }
    public void CompleteVoyageBounty(EntityUid station, CargoBountyData bounty, string name)
    {
        if (!TryComp<StationCargoBountyDatabaseComponent>(station, out var db)) return;
        db.History.Add(new CargoBountyHistoryData(bounty, CargoBountyHistoryData.BountyResult.Completed, _timing.CurTime, name));
        FillBountyDatabase(station, db);
    }
    public bool PlanVoyageCargo(EntityUid console, CargoBountyPrototype prototype, out Dictionary<EntityUid, int> plan, IReadOnlyDictionary<EntityUid, int>? reserved = null)
    {
        plan = new(); var goods = new HashSet<EntityUid>();
        foreach (var root in TradingPalletItems(console))
        {
            goods.UnionWith(GetBountyEntities(root));
            if (goods.Count > 2048) return false;
        }
        foreach (var entry in prototype.Entries)
        {
            var left = entry.Amount;
            foreach (var uid in goods.OrderBy(u => u.Id))
            {
                if (left <= 0) break;
                if (!Exists(uid) || EntityManager.IsQueuedForDeletion(uid) || !IsValidBountyEntry(uid, entry)) continue;
                // Containers themselves are never consumed with unrelated contents.
                if (TryComp<ContainerManagerComponent>(uid, out var cm) && cm.Containers.Values.Any(c => c.ContainedEntities.Any(e => !HasComp<Content.Shared.Chemistry.Components.SolutionComponent>(e)))) continue;
                var units = TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1;
                var take = Math.Min(left, units - plan.GetValueOrDefault(uid) - (reserved?.GetValueOrDefault(uid) ?? 0));
                if (take <= 0) continue;
                plan[uid] = plan.GetValueOrDefault(uid) + take; left -= take;
            }
            if (left > 0) return false;
        }
        return plan.Count > 0;
    }
}
