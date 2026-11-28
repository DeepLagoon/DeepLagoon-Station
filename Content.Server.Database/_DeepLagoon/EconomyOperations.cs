using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class EconomyOperations
{
    private const string FinanceId = "economy:finance:v1";
    private sealed record PaymentReceipt(string Fingerprint, EconomyPayment Result);
    private sealed record TradeReceipt(string Fingerprint, TradeResult Result);
    private static string Fingerprint<T>(T request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
    private static async Task Lock(ServerDbContext db, CancellationToken cancel)
    {
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(785120261127)", cancel);
    }
    private static async Task<(LagoonEconomyRecord Row, T State)> Load<T>(ServerDbContext db, string id, Guid user,
        CancellationToken cancel) where T : new()
    {
        var row = db.LagoonEconomyRecords.Local.FirstOrDefault(r => r.Id == id)
            ?? await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == id, cancel);
        if (row != null) return (row, JsonSerializer.Deserialize<T>(row.Data)!);
        row = new() { Id = id, UserId = user };
        db.LagoonEconomyRecords.Add(row);
        return (row, new());
    }
    private static async Task<Profile> Profile(ServerDbContext db, Guid user, int id, CancellationToken cancel)
        => await db.Profile.SingleOrDefaultAsync(p => p.Id == id && p.Preference.UserId == user, cancel)
            ?? throw new InvalidOperationException("character_missing");
    private static async Task LockUsers(ServerDbContext db, IEnumerable<Guid> users, CancellationToken cancel)
    {
        foreach (var user in users.Distinct().OrderBy(u => u)) await AccountCurrencyOperations.LockAccount(db, user, cancel);
    }
    private static async Task<Dictionary<string, double>> Materials(ServerDbContext db, CancellationToken cancel)
    {
        var json = await db.LagoonEconomyRecords.Where(r => r.Id == "exchange:market:v1").Select(r => r.Data).SingleOrDefaultAsync(cancel);
        if (json == null) return new();
        var market = JsonSerializer.Deserialize<ExchangeMarket>(json)!;
        return market.MaterialBase.Where(p => p.Value > 0 && !p.Key.EndsWith("Index", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => Math.Clamp((double)((market.Materials.GetValueOrDefault(p.Key)?.LastOrDefault()?.Price ?? p.Value) / p.Value), .65, 1.75));
    }

    public static async Task<EconomyFinance> Sample(ServerDbContext db, Guid epoch, long now, int active,
        EconomyPolicy policy, EconomyPurchase[] purchases, CancellationToken cancel = default)
    {
        if (purchases.Length > 4096) throw new ArgumentException("too_many_purchases");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var (row, state) = await Load<EconomyFinance>(db, FinanceId, Guid.Empty, cancel);
        state.Sample(epoch, now, active, policy);
        foreach (var purchase in purchases) state.Purchase(purchase.Key, purchase.Units, now);
        row.Data = JsonSerializer.Serialize(state);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return state;
    }

    public static async Task<EconomyAccount> Read(ServerDbContext db, Guid user, int slot, CancellationToken cancel = default)
    {
        var profile = await db.Profile.AsNoTracking().SingleAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        var json = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id == FinanceId).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        return new(profile.Id, slot, profile.BankBalance, profile.CharacterName,
            json == null ? new() : JsonSerializer.Deserialize<EconomyFinance>(json)!, await Materials(db, cancel));
    }

    public static async Task<EconomyPublic> Public(ServerDbContext db, CancellationToken cancel = default)
    {
        var json = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id == FinanceId).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        return new(json == null ? new() : JsonSerializer.Deserialize<EconomyFinance>(json)!, await Materials(db, cancel));
    }

    public static async Task<EconomyPayment> Procure(ServerDbContext db, EconomyProcurement request,
        EconomyPolicy policy, long now, CancellationToken cancel = default)
    {
        if (request.Operation == Guid.Empty || request.User == Guid.Empty || request.Profile <= 0 ||
            request.Payout is < 0 or > int.MaxValue || request.Principal < 0 || request.Principal > request.Payout ||
            request.Costs.Count > 5 || request.Costs.Values.Any(v => v < 0) ||
            request.Costs.Values.Sum(v => (decimal)v) < request.Payout - request.Principal || request.QuotaPayout < -1 ||
            request.QuotaPayout >= 0 && (request.QuotaPayout < request.Payout - request.Principal || request.QuotaPayout > request.Costs.Values.Sum(v => (decimal)v)))
            throw new ArgumentException("invalid_procurement");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await LockUsers(db, [request.User], cancel);
        var key = $"economy:npc:{request.Operation:N}";
        var fingerprint = Fingerprint(request);
        var prior = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (prior != null)
        {
            var receipt = JsonSerializer.Deserialize<PaymentReceipt>(prior.Data)!;
            if (receipt.Fingerprint != fingerprint) throw new InvalidOperationException("operation_mismatch");
            var current = await db.Profile.SingleOrDefaultAsync(p => p.Id == request.Profile && p.Preference.UserId == request.User, cancel);
            return receipt.Result with { Applied = false, Bank = current?.BankBalance ?? 0 };
        }
        var profile = await Profile(db, request.User, request.Profile, cancel);
        var (row, state) = await Load<EconomyFinance>(db, FinanceId, Guid.Empty, cancel);
        var income = request.QuotaPayout >= 0 ? request.QuotaPayout : request.Payout - request.Principal;
        var accepted = state.TrySpend(request.User, request.Costs, request.AllowProtected ? income : long.MaxValue, now, policy);
        // Durable parking claims wait for future funding; unsuccessful polls create no terminal receipt.
        if (!accepted && request.WaitForFunding) return new(request.Operation, false, false, profile.BankBalance, "procurement_exhausted");
        if (accepted)
        {
            profile.BankBalance = checked(profile.BankBalance + request.Payout);
            if (request.Payout > 0) BankFinancialOperation.Record(db, request.User, profile, request.Payout, "procurement_sale");
            if (request.Recovery != null) await CrashRecoveryOperations.RemoveInTransaction(db, request.Recovery, cancel);
        }
        var result = new EconomyPayment(request.Operation, true, accepted, profile.BankBalance, accepted ? "paid" : "procurement_exhausted");
        row.Data = JsonSerializer.Serialize(state);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = request.User, Data = JsonSerializer.Serialize(new PaymentReceipt(fingerprint, result)) });
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }
}
