using System.Linq;

namespace Content.Server._DeepLagoon.Procurement;

/// <summary>
/// Pure counterfactual, whole-lot procurement. Contains no payment or entity APIs.
/// Persistent state is versioned by the complete policy ID. Round changes do not reset it.
/// </summary>
public sealed class ProcurementBudget
{
    public sealed record ProtectedPayment(Guid Player, long At, decimal Amount);
    public sealed class Pool
    {
        public decimal General { get; set; }
        public decimal Protected { get; set; }
    }
    public sealed class State
    {
        public Dictionary<ProcurementCategory, Pool> Pools { get; set; } = new();
        public List<ProtectedPayment> Payments { get; set; } = new();
        public decimal Issued { get; set; }
        public decimal Spent { get; set; }
        public decimal Expired { get; set; }
        public long Accepted { get; set; }
        public long Rejected { get; set; }
    }

    public static readonly IReadOnlyDictionary<ProcurementCategory, decimal> Shares =
        new Dictionary<ProcurementCategory, decimal>
        {
            [ProcurementCategory.Materials] = .40m,
            [ProcurementCategory.Industry] = .20m,
            [ProcurementCategory.Recovery] = .20m,
            [ProcurementCategory.Gas] = .10m,
            [ProcurementCategory.Food] = .10m,
        };

    public ProcurementPolicy Policy { get; }
    public State Data { get; }

    public ProcurementBudget(ProcurementPolicy policy, State? state = null)
    {
        if (policy.HourlyRate <= 0 || policy.HourlyRate > 1000000 ||
            policy.ProtectedShare is < 0 or > 1 || policy.DailyProtectedLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(policy));
        Policy = policy;
        Data = state ?? new();
        foreach (var category in Shares.Keys)
            Data.Pools.TryAdd(category, new());
    }

    /// <summary>Accrue only a measured interval. The caller rejects gaps and offline time.</summary>
    public void Accrue(decimal hours, int activePlayers, long now)
    {
        if (hours is < 0 or > (1m / 60) || activePlayers is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(hours));
        Prune(now);
        if (activePlayers == 0)
            return; // Keep existing limits through an empty lobby; never issue money for offline time.
        foreach (var (category, share) in Shares)
        {
            var pool = Data.Pools[category];
            var rate = Policy.HourlyRate * activePlayers * share;
            var general = rate * (1 - Policy.ProtectedShare);
            var protectedRate = rate * Policy.ProtectedShare;
            var addition = rate * hours;
            // Two hours at the observed population; reducing population cannot retain a giant stockpile.
            var nextGeneral = Math.Min(pool.General + general * hours, general * 2);
            var nextProtected = Math.Min(pool.Protected + protectedRate * hours, protectedRate * 2);
            Data.Issued += addition;
            Data.Expired += pool.General + pool.Protected + addition - nextGeneral - nextProtected;
            pool.General = nextGeneral;
            pool.Protected = nextProtected;
        }
    }

    public ProcurementShadowDecision Observe(long now, Guid? player, ProcurementCategory category,
        decimal cost, bool manifestComplete = true)
    {
        Prune(now);
        if (cost < 0)
            throw new ArgumentOutOfRangeException(nameof(cost));
        if (cost == 0)
            return new(Policy.Id, true, "no_new_spessos", 0, 0, 0);
        if (!manifestComplete || !Data.Pools.TryGetValue(category, out var pool))
            return Reject(!manifestComplete ? "incomplete_manifest" : "mixed_or_unclassified", cost);
        // Existing sales are not reserved contracts. This is an explicitly labelled small-sale proxy.
        decimal protectedUse = 0;
        if (player is { } user && cost is >= 3000 and <= 8000)
        {
            var used = Data.Payments.Where(p => p.Player == user).Sum(p => p.Amount);
            protectedUse = Math.Min(cost, Math.Min(pool.Protected, Math.Max(0, Policy.DailyProtectedLimit - used)));
        }
        var generalUse = cost - protectedUse;
        if (generalUse > pool.General)
            return Reject("budget_exhausted_whole_lot", cost);
        pool.General -= generalUse;
        pool.Protected -= protectedUse;
        if (protectedUse > 0)
            Data.Payments.Add(new(player!.Value, now, protectedUse));
        Data.Spent += cost;
        Data.Accepted++;
        return new(Policy.Id, true, "whole_lot", cost, protectedUse, generalUse);
    }

    public static (decimal Cost, ProcurementCategory Category) Cost(ProcurementSale sale)
    {
        var cost = (sale.Currency == "Credit" ? Math.Max(0, sale.CashPayout - (decimal) sale.ReturnedPrincipal) : 0)
            + sale.FactionAccruals.Values.Where(v => v > 0).Sum(v => (decimal) v);
        var categories = sale.Roots.Where(r => r.Payout > 0 && r.SuggestedCategory != ProcurementCategory.Principal)
            .Select(r => r.SuggestedCategory).Distinct().ToArray();
        return (cost, categories.Length == 1 ? categories[0] : ProcurementCategory.Unclassified);
    }

    private ProcurementShadowDecision Reject(string reason, decimal cost)
    {
        Data.Rejected++;
        return new(Policy.Id, false, reason, cost, 0, 0);
    }

    private void Prune(long now) => Data.Payments.RemoveAll(p => p.At <= now - 86400000L);
}
