using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record ShipRepairPurchase(Guid Id, Guid User, int Profile, int Slot, Guid Ship, Guid Run,
    long Credits, long Coins, long Expires, string State = "paid");

public static partial class EconomyOperations
{
    private const string RepairPurchasePrefix = "parking:repair:";

    /// <summary>Wallet debit and order receipt are one database transaction. Retries cannot debit twice.</summary>
    public static async Task<ShipRepairPurchase> PurchaseShipRepair(ServerDbContext db, ShipRepairPurchase request, CancellationToken cancel = default)
    {
        if (request.Id == Guid.Empty || request.User == Guid.Empty || request.Ship == Guid.Empty || request.Run == Guid.Empty ||
            request.Profile <= 0 || request.Slot < 0 || request.Credits < 0 || request.Coins < 0 ||
            (request.Credits == 0) == (request.Coins == 0) || request.Expires <= 0 || request.State != "paid")
            throw new ArgumentException("Invalid ship repair payment.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await AccountCurrencyOperations.LockAccount(db, request.User, cancel);
        var key = RepairPurchasePrefix + request.Id.ToString("N");
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (previous != null)
        {
            var receipt = JsonSerializer.Deserialize<ShipRepairPurchase>(previous.Data)!;
            if (receipt with { State = "paid" } != request) throw new InvalidOperationException("Repair payment key was reused.");
            return receipt;
        }
        var profile = await Profile(db, request.User, request.Profile, cancel);
        if (profile.Slot != request.Slot) throw new InvalidOperationException("Character slot changed.");
        if (request.Credits > 0)
        {
            if (profile.BankBalance < request.Credits) throw new InvalidOperationException("Недостаточно кредитов.");
            profile.BankBalance -= request.Credits;
            BankFinancialOperation.Record(db, request.User, profile, request.Credits, "shipyard_repair");
        }
        else
        {
            var wallet = await db.Preference.SingleAsync(p => p.UserId == request.User, cancel);
            if (wallet.LagoonCoins < request.Coins) throw new InsufficientLagoonCoinsException();
            wallet.LagoonCoins -= request.Coins;
            db.LagoonCoinOperations.Add(new LagoonCoinOperation { UserId = request.User,
                OperationId = "spend:shipyard:" + request.Id.ToString("N"), Amount = -request.Coins,
                Reason = "Мгновенный ремонт шаттла", ActorId = request.User, BalanceAfter = wallet.LagoonCoins, CreatedAt = DateTime.UtcNow });
        }
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = request.User, Data = JsonSerializer.Serialize(request) });
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return request;
    }

    /// <summary>Completion/refund are mutually exclusive and idempotent. Expired interrupted orders are refunded.</summary>
    public static async Task<ShipRepairPurchase> FinishShipRepair(ServerDbContext db, Guid id, Guid user, bool refund, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var row = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == RepairPurchasePrefix + id.ToString("N") && r.UserId == user, cancel);
        var order = JsonSerializer.Deserialize<ShipRepairPurchase>(row.Data)!;
        if (order.State != "paid") return order;
        if (refund)
        {
            if (order.Credits > 0)
            {
                var profile = await Profile(db, user, order.Profile, cancel);
                profile.BankBalance = checked(profile.BankBalance + order.Credits);
                BankFinancialOperation.Record(db, user, profile, order.Credits, "shipyard_repair_refund");
            }
            else
            {
                var wallet = await db.Preference.SingleAsync(p => p.UserId == user, cancel);
                wallet.LagoonCoins = checked(wallet.LagoonCoins + order.Coins);
                db.LagoonCoinOperations.Add(new LagoonCoinOperation { UserId = user,
                    OperationId = "refund:shipyard:" + order.Id.ToString("N"), Amount = order.Coins,
                    Reason = "Возврат оплаты ремонта шаттла", ActorId = user, BalanceAfter = wallet.LagoonCoins, CreatedAt = DateTime.UtcNow });
            }
        }
        order = order with { State = refund ? "refunded" : "completed" };
        row.Data = JsonSerializer.Serialize(order);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return order;
    }

    public static async Task<ShipRepairPurchase[]> ExpiredShipRepairs(ServerDbContext db, long now, CancellationToken cancel = default)
        => (await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(RepairPurchasePrefix) && r.Data.Contains("\"State\":\"paid\""))
                .Select(r => r.Data).ToArrayAsync(cancel))
            .Select(s => JsonSerializer.Deserialize<ShipRepairPurchase>(s)!).Where(p => p.State == "paid" && p.Expires <= now).ToArray();
}
