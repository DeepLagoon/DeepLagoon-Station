using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Content.Server._DeepLagoon.Economy;
using Content.Server.Database;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;

namespace Content.Server._DeepLagoon.ShuttleParking;

public sealed partial class ParkingSystem
{
    [Dependency] private ITaskManager _economyTasks = default!;
    private readonly HashSet<Guid> _economyPayouts = new();

    private ParkingPayout PrepareEconomyPayout(ParkingPayout payout, double materials = 0, double research = 0)
    {
        var task = _database.ReadEconomyAccountAsync(payout.Owner, payout.Character);
        _economyTasks.BlockWaitOnTask(task);
        var account = task.GetAwaiter().GetResult();
        var materialCost = (long)Math.Clamp(Math.Floor(materials), 0, payout.Amount);
        var researchCost = (long)Math.Clamp(Math.Floor(research), 0, payout.Amount - materialCost);
        return payout with { ProfileId = account.Profile, Costs = new()
        { ["Materials"] = materialCost, ["Recovery"] = researchCost, ["Industry"] = payout.Amount - materialCost - researchCost } };
    }
    private async void QueueEconomyPayout(ParkingPayout payout)
    {
        var economy = EntityManager.System<SectorEconomySystem>();
        if (!economy.Ready || !_economyPayouts.Add(payout.Id)) return;
        var user = new NetUserId(payout.Owner);
        if (!_bank.BeginExchangePayment(user)) { _economyPayouts.Remove(payout.Id); return; }
        try
        {
            var amount = Math.Min(8000, payout.Amount - payout.Paid);
            if (amount <= 0) { _store.FinishPayout(payout.Id); return; }
            var source = payout.Costs ?? new() { ["Industry"] = payout.Amount };
            Dictionary<string, long> Allocation(int paid)
            {
                var remaining = (long)paid;
                var costs = new Dictionary<string, long>();
                // Monotonic allocation: integer rounding must never create a negative category installment.
                foreach (var entry in source.OrderBy(p => p.Key, StringComparer.Ordinal))
                { var part = Math.Min(entry.Value, remaining); costs[entry.Key] = part; remaining -= part; }
                return costs;
            }
            var before = Allocation(payout.Paid);
            var costs = Allocation(payout.Paid + amount).ToDictionary(p => p.Key, p => p.Value - before[p.Key]);
            var operation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"parking-economy:{payout.Id:N}:{payout.Paid}"))[..16]);
            var request = new EconomyProcurement(operation, payout.Owner, payout.ProfileId, amount, 0, costs, null,
                WaitForFunding: true, AllowProtected: payout.Amount <= economy.Policy.SmallSale);
            var result = await _database.PayProcurementAsync(request, economy.Policy, SectorEconomySystem.UtcNow);
            if (!result.Accepted) return;
            var next = payout with { Paid = payout.Paid + amount };
            // If this local write is interrupted, the same database receipt advances it without paying twice.
            await WaitForSaveWriter();
            _store.UpdatePayout(next);
            if (next.Paid >= next.Amount) _store.FinishPayout(next.Id);
            await _bank.PublishExchangeBalance(user, payout.Character);
        }
        catch (Exception e) { Log.Warning($"Parking procurement claim {payout.Id} awaits confirmation ({Content.Server._DeepLagoon.Diagnostics.ExceptionDiagnostics.Describe(e)})."); }
        finally { _bank.EndExchangePayment(user); _economyPayouts.Remove(payout.Id); }
    }
}
