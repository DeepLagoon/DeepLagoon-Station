using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class ExchangeOperations
{
    private const string MarketId = "exchange:market:v1";
    private sealed record Receipt(string Fingerprint);
    private sealed record MiningReceipt(string Fingerprint, long At, ExchangeMiningResult Result);

    private static async Task LockMarket(ServerDbContext db, CancellationToken cancel)
    {
        // SQLite's transaction owns its writer lock; PostgreSQL needs one global market lock.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(785120261126)", cancel);
    }

    private static async Task<(LagoonEconomyRecord Row, ExchangeMarket Market)> Market(ServerDbContext db, long now, CancellationToken cancel)
    {
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == MarketId, cancel);
        if (row != null) return (row, JsonSerializer.Deserialize<ExchangeMarket>(row.Data)!);
        row = new() { Id = MarketId, UserId = Guid.Empty };
        db.LagoonEconomyRecords.Add(row);
        return (row, ExchangeMarket.Create(now));
    }

    public static async Task<ExchangeResult> Read(ServerDbContext db, Guid user, int slot, long now, CancellationToken cancel = default, string walletAddress = "")
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        var (row, market) = await Market(db, now, cancel);
        if (db.Entry(row).State == EntityState.Added)
            row.Data = JsonSerializer.Serialize(market);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        var result = await Result(db, user, profile, market, false, walletAddress, cancel);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    public static async Task<ExchangeResult> Trade(ServerDbContext db, Guid user, ExchangeTradeRequest request, long now,
        CancellationToken cancel = default)
    {
        if (request.Operation == Guid.Empty || request.Slot < 0 || request.Limit < 0 || request.Cash && (request.Buy || request.Limit > int.MaxValue) || request.Units is < 1 or > ExchangeMarket.MaxOrderUnits ||
            !ExchangeMarket.Assets.Any(a => a.Id == request.Asset)) throw new ArgumentException("invalid_order");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (row, market) = await Market(db, now, cancel);
        var profile = await db.Profile.SingleAsync(p => p.Preference.UserId == user && p.Slot == request.Slot, cancel);
        if (request.ExpectedProfileId != 0 && profile.Id != request.ExpectedProfileId)
            throw new InvalidOperationException("character_changed");
        var address = request.WalletAddress.Length > 0 ? request.WalletAddress : await Address(db, user, profile.Id, cancel);
        var (walletRow, wallet) = await Wallet(db, user, profile.Id, cancel, address);
        var key = $"exchange:trade:{user:N}:{request.Operation:N}";
        var fingerprint = JsonSerializer.Serialize(request);
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (previous != null)
        {
            if (JsonSerializer.Deserialize<Receipt>(previous.Data)!.Fingerprint != fingerprint)
                throw new InvalidOperationException("operation_mismatch");
            return await Result(db, user, profile, market, false, address, cancel);
        }
        await CheckWalletVersion(db, address, request.WalletVersion, cancel);
        var owned = wallet.GetValueOrDefault(request.Asset);
        var updated = checked(owned + (request.Buy ? request.Units : -request.Units));
        // Physical cash is captured before this transaction. Require its exact quoted denomination.
        if (request.Cash && market.Quote(request.Asset, request.Units).SellProceeds != request.Limit)
            throw new InvalidOperationException("price_changed");
        var (change, fee) = market.Trade(request.Asset, request.Units, request.Buy, request.Limit, owned, request.Cash ? 0 : profile.BankBalance, now);
        if (!request.Cash) profile.BankBalance = checked(profile.BankBalance + change);
        if (request.CashRecovery != null)
        {
            if (!request.Cash) throw new InvalidOperationException("invalid_order");
            await CrashRecoveryOperations.ImportExternal(db, request.CashRecovery, user, profile.Id, cancel);
        }
        wallet[request.Asset] = updated;
        row.Data = JsonSerializer.Serialize(market);
        walletRow.Data = JsonSerializer.Serialize(wallet);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(new Receipt(fingerprint)) });
        if (!request.Buy)
            db.LagoonEconomyRecords.Add(new() { Id = $"exchange:withdraw:{address}:{now:D19}:{request.Operation:N}", UserId = user,
                Data = JsonSerializer.Serialize(new ExchangeWithdrawal(request.Operation, now, change, profile.CharacterName,
                    request.Cash, request.Asset, request.Units)) });
        Audit(db, user, profile, request.Buy ? "crypto_buy" : "crypto_sell", Math.Abs(change),
            new(request.Operation, request.Asset, request.Units, address, owned, updated, fee,
                (Math.Abs(change) + (request.Buy ? -fee : fee)) * (decimal)ExchangeMarket.UnitsPerCoin / request.Units,
                request.Cash ? "cash" : "bank", WalletCharacter: (await Recipient(db, address, cancel)).Profile.CharacterName));
        var result = await Result(db, user, profile, market, true, address, cancel);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    public static async Task<ExchangeResult> Deposit(ServerDbContext db, Guid user, int slot, string asset, long units,
        Guid operation, RecoveryRemoval? recovery, long now, CancellationToken cancel = default, int expectedProfileId = 0, string walletAddress = "", Guid? walletVersion = null)
    {
        if (operation == Guid.Empty || units is < 1 or > 1000000000 || !ExchangeMarket.Assets.Any(a => a.Id == asset))
            throw new ArgumentException("invalid_deposit");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (row, market) = await Market(db, now, cancel);
        var key = $"exchange:deposit:{operation:N}";
        var profile = await db.Profile.SingleOrDefaultAsync(p => p.Preference.UserId == user && p.Slot == slot, cancel);
        var identity = expectedProfileId != 0 ? expectedProfileId : profile?.Id ?? 0;
        var fingerprint = $"{user:N}:{identity}:{asset}:{units}" + (walletAddress.Length > 0 ? ":" + walletAddress : "");
        var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (previous != null)
        {
            if (JsonSerializer.Deserialize<Receipt>(previous.Data)!.Fingerprint != fingerprint)
                throw new InvalidOperationException("operation_mismatch");
            var currentWallet = profile == null ? new Dictionary<string, long>() : (await Wallet(db, user, profile.Id, cancel)).Value;
            return new(profile?.BankBalance ?? 0, profile?.Id ?? identity, slot, market, currentWallet);
        }
        if (profile == null || expectedProfileId != 0 && profile.Id != expectedProfileId)
            throw new InvalidOperationException("character_changed");
        var address = walletAddress.Length > 0 ? walletAddress : await Address(db, user, profile.Id, cancel);
        await CheckWalletVersion(db, address, walletVersion, cancel);
        var (walletRow, wallet) = await Wallet(db, user, profile.Id, cancel, address);
        var before = wallet.GetValueOrDefault(asset);
        wallet[asset] = checked(wallet.GetValueOrDefault(asset) + units);
        walletRow.Data = JsonSerializer.Serialize(wallet);
        row.Data = JsonSerializer.Serialize(market);
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = user, Data = JsonSerializer.Serialize(new Receipt(fingerprint)) });
        if (recovery != null) await CrashRecoveryOperations.RemoveInTransaction(db, recovery, cancel);
        Audit(db, user, profile, "crypto_deposit", 0, new(operation, asset, units, address, before, wallet[asset]));
        var result = await Result(db, user, profile, market, true, address, cancel);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    public static async Task<string[]> Mint(ServerDbContext db, int count, long now, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        var (row, market) = await Market(db, now, cancel);
        var minted = market.Mint(count, now);
        row.Data = JsonSerializer.Serialize(market);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return minted;
    }

    /// <summary>Issuance, wallet credits and stable per-farm receipts commit in one transaction.</summary>
    public static async Task<ExchangeMiningResult[]> MintToWallets(ServerDbContext db, ExchangeMiningRequest[] requests,
        long now, CancellationToken cancel = default)
    {
        if (requests.Length is < 1 or > 64 || requests.Select(r => r.Operation).Distinct().Count() != requests.Length ||
            requests.Any(r => r.Operation == Guid.Empty || r.User == Guid.Empty || r.ProfileId <= 0))
            throw new ArgumentException("invalid_mining_batch");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        // Same ordering as bank transfers; multiple farms may belong to the same or different users.
        foreach (var user in requests.Select(r => r.User).Distinct().OrderBy(u => u))
            if (await db.Preference.AnyAsync(p => p.UserId == user, cancel))
                await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (row, market) = await Market(db, now, cancel);
        market.Mint(0, now);
        var results = new List<ExchangeMiningResult>(requests.Length);
        foreach (var request in requests)
        {
            var key = $"exchange:mine:{request.Operation:N}";
            var fingerprint = JsonSerializer.Serialize(request);
            var previous = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
            if (previous != null)
            {
                var receipt = JsonSerializer.Deserialize<MiningReceipt>(previous.Data)!;
                if (receipt.Fingerprint != fingerprint) throw new InvalidOperationException("operation_mismatch");
                results.Add(receipt.Result with { Applied = false });
                continue;
            }
            var exists = await db.Profile.AnyAsync(p => p.Id == request.ProfileId && p.Preference.UserId == request.User, cancel);
            var coin = exists ? market.Mint(1, now).SingleOrDefault() : null;
            var asset = coin == null ? null : ExchangeMarket.Assets.Single(a => a.CoinPrototype == coin).Id;
            var result = new ExchangeMiningResult(request.Operation, asset, asset == null ? 0 : ExchangeMarket.UnitsPerCoin,
                true, exists ? null : "character_missing");
            if (asset != null)
            {
                var (walletRow, wallet) = await Wallet(db, request.User, request.ProfileId, cancel);
                var before = wallet.GetValueOrDefault(asset);
                wallet[asset] = checked(wallet.GetValueOrDefault(asset) + result.Units);
                walletRow.Data = JsonSerializer.Serialize(wallet);
                var profile = await db.Profile.SingleAsync(p => p.Id == request.ProfileId, cancel);
                var address = await Address(db, request.User, profile.Id, cancel);
                Audit(db, request.User, profile, "crypto_mining", 0,
                    new(request.Operation, asset, result.Units, address, before, wallet[asset]));
            }
            db.LagoonEconomyRecords.Add(new() { Id = key, UserId = request.User,
                Data = JsonSerializer.Serialize(new MiningReceipt(fingerprint, now, result)) });
            results.Add(result);
        }
        row.Data = JsonSerializer.Serialize(market);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return results.ToArray();
    }

    public static async Task Sample(ServerDbContext db, ExchangeMaterialSample[] materials, long now, CancellationToken cancel = default)
    {
        if (materials.Length > 32) throw new ArgumentException("too_many_materials");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await LockMarket(db, cancel);
        var (row, market) = await Market(db, now, cancel);
        foreach (var asset in ExchangeMarket.Assets) market.Sample(asset.Id, now, 0);
        market.SampleMaterials(materials, now);
        row.Data = JsonSerializer.Serialize(market);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
    }

    private static async Task<(LagoonEconomyRecord Row, Dictionary<string, long> Value)> Wallet(ServerDbContext db, Guid user,
        int profile, CancellationToken cancel, string address = "")
    {
        // A deleted/recreated character slot cannot inherit the previous character's wallet.
        var key = address.Length > 0 ? BalanceKey((await Recipient(db, address, cancel)).Owner) : $"exchange:wallet:{user:N}:{profile}";
        var row = db.LagoonEconomyRecords.Local.FirstOrDefault(r => r.Id == key)
            ?? await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (row != null) return (row, JsonSerializer.Deserialize<Dictionary<string, long>>(row.Data)!);
        row = new() { Id = key, UserId = user };
        db.LagoonEconomyRecords.Add(row);
        return (row, new());
    }
}
