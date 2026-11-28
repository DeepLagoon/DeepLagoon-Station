using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Server.Database;

public sealed record EconomyPolicy(long HourlyPerPlayer = 20000, decimal ProtectedShare = .40m,
    long ProtectedDaily = 60000, long SmallSale = 8000);
public sealed record EconomyProtectedPayment(Guid User, long At, decimal Amount);
public sealed class EconomyPool
{
    public decimal General { get; set; }
    public decimal Protected { get; set; }
}
public sealed class EconomyDemand
{
    public double Units { get; set; }
    public long At { get; set; }
    public double Value(long now) => Units * Math.Pow(.5, Math.Clamp(now - At, 0, 604800000L) / 1800000d);
}
public sealed class EconomyFinance
{
    public static readonly IReadOnlyDictionary<string, decimal> Shares = new Dictionary<string, decimal>
    { ["Materials"] = .4m, ["Industry"] = .2m, ["Recovery"] = .2m, ["Gas"] = .1m, ["Food"] = .1m };
    public Dictionary<string, EconomyPool> Pools { get; set; } = new();
    public List<EconomyProtectedPayment> ProtectedPayments { get; set; } = new();
    public Dictionary<string, EconomyDemand> Demand { get; set; } = new();
    public Guid Epoch { get; set; }
    public long LastSample { get; set; }
    public int Active { get; set; }
    public decimal Issued { get; set; }
    public decimal Spent { get; set; }
    public decimal Expired { get; set; }

    public void Sample(Guid epoch, long now, int active, EconomyPolicy policy)
    {
        if (Epoch == epoch && now < LastSample) return;
        if (active is < 0 or > 10000 || policy.HourlyPerPlayer is < 1 or > 1000000 ||
            policy.ProtectedShare is < 0 or > 1 || policy.ProtectedDaily < 0 || policy.SmallSale < 0)
            throw new ArgumentException("invalid_economy_policy");
        foreach (var category in Shares.Keys) Pools.TryAdd(category, new());
        var elapsed = now - LastSample;
        if (Epoch == epoch && elapsed is > 0 and <= 60000 && Active > 0 && active > 0)
            foreach (var (category, share) in Shares)
            {
                var pool = Pools[category];
                var rate = policy.HourlyPerPlayer * Math.Min(Active, active) * share;
                var amount = rate * elapsed / 3600000m;
                var general = Math.Min(pool.General + amount * (1 - policy.ProtectedShare), rate * 2 * (1 - policy.ProtectedShare));
                var reserved = Math.Min(pool.Protected + amount * policy.ProtectedShare, rate * 2 * policy.ProtectedShare);
                Issued += amount;
                Expired += pool.General + pool.Protected + amount - general - reserved;
                pool.General = general;
                pool.Protected = reserved;
            }
        Epoch = epoch;
        LastSample = Math.Max(now, LastSample);
        Active = active;
        ProtectedPayments.RemoveAll(p => p.At <= now - 86400000L);
        foreach (var key in Demand.Where(p => p.Value.Value(now) < .0001).Select(p => p.Key).ToArray()) Demand.Remove(key);
    }

    public bool TrySpend(Guid user, IReadOnlyDictionary<string, long> costs, long payout, long now, EconomyPolicy policy)
    {
        if (costs.Count == 0 || costs.Any(p => p.Value < 0 || !Shares.ContainsKey(p.Key))) return false;
        ProtectedPayments.RemoveAll(p => p.At <= now - 86400000L);
        var remaining = user != Guid.Empty && payout is > 0 && payout <= policy.SmallSale
            ? Math.Max(0, policy.ProtectedDaily - ProtectedPayments.Where(p => p.User == user).Sum(p => p.Amount)) : 0;
        var uses = new List<(EconomyPool Pool, decimal General, decimal Protected)>();
        foreach (var (category, cost) in costs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!Pools.TryGetValue(category, out var pool)) return false;
            var reserved = Math.Min(cost, Math.Min(remaining, pool.Protected));
            var general = cost - reserved;
            if (pool.General < general) return false;
            uses.Add((pool, general, reserved));
            remaining -= reserved;
        }
        foreach (var use in uses) { use.Pool.General -= use.General; use.Pool.Protected -= use.Protected; }
        var protectedUsed = uses.Sum(u => u.Protected);
        if (protectedUsed > 0) ProtectedPayments.Add(new(user, now, protectedUsed));
        Spent += uses.Sum(u => u.General + u.Protected);
        return true;
    }

    public void Purchase(string key, long units, long now)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || units is < 1 or > 1000000) return;
        if (!Demand.TryGetValue(key, out var pressure)) Demand[key] = pressure = new() { At = now };
        pressure.Units = Math.Min(20, pressure.Value(now) + units);
        pressure.At = now;
    }
    public double DemandFactor(string key, long now) => 1 + Math.Clamp(Demand.GetValueOrDefault(key)?.Value(now) ?? 0, 0, 20) * .02;
}

public sealed record EconomyAccount(int Profile, int Slot, long Bank, string Character, EconomyFinance Finance,
    Dictionary<string, double> MaterialFactors);
public sealed record EconomyPublic(EconomyFinance Finance, Dictionary<string, double> MaterialFactors);
public sealed record EconomyProcurement(Guid Operation, Guid User, int Profile, long Payout, long Principal,
    Dictionary<string, long> Costs, RecoveryRemoval? Recovery, bool WaitForFunding = false, bool AllowProtected = true,
    long QuotaPayout = -1);
public sealed record EconomyPayment(Guid Operation, bool Applied, bool Accepted, long Bank, string Reason);
public sealed record EconomyPurchase(string Key, long Units);

public sealed record TradeCriteria(int MinimumCondition = 100, int MinimumCharge = 0, int StackUnits = 1);
public sealed record TradeItem(string Prototype, string Name, string Snapshot, int Condition, int Charge,
    int StackUnits, Dictionary<string, double> Materials)
{
    // Omit missing legacy metadata so old request fingerprints remain stable.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasBattery { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Splittable { get; init; }
}
public sealed class TradeOffer
{
    public Guid Id { get; set; }
    public Guid User { get; set; }
    public int Profile { get; set; }
    public string Character { get; set; } = "";
    public bool BuyOrder { get; set; }
    public string Prototype { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Description { get; set; } = "";
    public long Price { get; set; }
    public int Remaining { get; set; }
    public TradeCriteria Criteria { get; set; } = new();
    public bool? HasBattery { get; set; }
    public long Escrow { get; set; }
    public long Created { get; set; }
    public long Expires { get; set; }
    public bool Closed { get; set; }
    public bool Orphaned { get; set; }
    public string ClosureReason { get; set; } = "";
    public bool Splittable { get; set; }
    public long OriginalPrice { get; set; }
    public int OriginalUnits { get; set; }
}
public sealed class TradeDelivery
{
    public Guid Id { get; set; }
    public Guid User { get; set; }
    public int Profile { get; set; }
    public string Prototype { get; set; } = "";
    public string Name { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public Guid Lease { get; set; }
    public long LeaseUntil { get; set; }
    public bool Delivered { get; set; }
    public bool SplitStack { get; set; }
    public int StackUnits { get; set; }
}
public sealed record TradeCreate(Guid Operation, Guid User, int Profile, bool BuyOrder, string Prototype,
    string Name, string Description, int Quantity, long Price, TradeCriteria Criteria, TradeItem? Item, RecoveryRemoval? Recovery)
{
    // Preserve fingerprints of transactions queued before round-scoped listings were introduced.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public Guid Cycle { get; init; }
}

public sealed class TradeCycle
{
    public Guid Id { get; set; }
    public bool Closed { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PendingOffers { get; set; }
}
public sealed record TradeDeal(Guid Operation, Guid User, int Profile, Guid Offer, TradeItem? Item, RecoveryRemoval? Recovery);
public sealed record TradeResult(Guid Operation, bool Applied, long Bank, Guid Offer, Guid? Delivery = null,
    Guid? Counterparty = null, int CounterpartySlot = -1, string? TradedPrototype = null, long Price = 0,
    Dictionary<string, double>? Materials = null);
public sealed record TradeBoard(TradeOffer[] Offers, TradeDelivery[] Deliveries, long Bank, int Profile);
