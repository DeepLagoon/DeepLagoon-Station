using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public static partial class AccountEconomyOperations
{
    private static string Key(Guid user) => "account:" + user.ToString("N");
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(LagoonEconomyRecord record) => JsonSerializer.Deserialize<T>(record.Data) ?? throw new InvalidOperationException("Invalid economy record.");
    private static async Task<(Preference Wallet, LagoonEconomyRecord Record, LagoonEconomyState State)> Load(
        ServerDbContext db, Guid user, DateTime now, CancellationToken cancel)
    {
        var wallet = await db.Preference.SingleAsync(p => p.UserId == user, cancel);
        var record = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == Key(user), cancel);
        if (record != null)
        {
            var existing = Read<LagoonEconomyState>(record);
            var stock = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == "furniture-stock:" + user.ToString("N"), cancel);
            if (stock != null) existing.FurnitureStock = Read<Dictionary<string, int>>(stock);
            return (wallet, record, existing);
        }
        var first = await db.Player.Where(p => p.UserId == user).Select(p => (DateTime?)p.FirstSeenTime).FirstOrDefaultAsync(cancel) ?? now;
        var played = await db.PlayTime.Where(p => p.PlayerId == user && p.Tracker == "Overall").Select(p => (TimeSpan?)p.TimeSpent).FirstOrDefaultAsync(cancel);
        var state = new LagoonEconomyState { FirstSeen = first, PlayedTicks = played?.Ticks ?? 0 };
        record = new LagoonEconomyRecord { Id = Key(user), UserId = user, Data = Json(state) };
        db.LagoonEconomyRecords.Add(record);
        return (wallet, record, state);
    }
    private static void Save(LagoonEconomyRecord record, LagoonEconomyState state) { state.Revision++; record.Data = Json(state); }
    private static void AdvancePass(LagoonEconomyState state, DateTime now, int externalTier, DateTime? externalExpiresAt = null)
    {
        if (state.PassAccountedAt != default)
        {
            var chargeFrom = state.PassAccountedAt;
            if (state.ExternalTier >= state.PassTier)
                chargeFrom = state.ExternalExpiresAt is { } expiry ? (expiry > chargeFrom ? expiry : chargeFrom) : now;
            state.PassTicks = Math.Max(0, state.PassTicks - Math.Max(0, (now - chargeFrom).Ticks));
        }
        if (state.PassTicks == 0) state.PassTier = 0;
        state.PassAccountedAt = now;
        state.ExternalTier = Math.Clamp(externalTier, 0, 4);
        state.ExternalExpiresAt = externalExpiresAt;
    }
    private static int Effective(LagoonEconomyState state) => Math.Max(state.ExternalTier, state.PassTicks > 0 ? state.PassTier : 0);
    private static void Credit(ServerDbContext db, Preference wallet, string key, long amount, string reason, DateTime now, Guid? actor = null, long ticks = 0)
    {
        var balance = checked(wallet.LagoonCoins + amount);
        if (balance < 0) throw new InsufficientLagoonCoinsException();
        wallet.LagoonCoins = balance;
        db.LagoonCoinOperations.Add(new LagoonCoinOperation { UserId = wallet.UserId, OperationId = key,
            Amount = amount, Reason = reason, ActorId = actor, CreatedAt = now, BalanceAfter = balance,
            PlayedTicks = ticks, WebhookDelivered = amount == 0 });
    }
    private static async Task<bool> Receipt(ServerDbContext db, Guid user, string key, object payload, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 150) throw new ArgumentException("Invalid operation key.");
        var id = "receipt:" + key;
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
        var existing = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == id, cancel);
        if (existing != null)
        {
            if (existing.UserId != user || Read<LagoonEconomyReceipt>(existing).Fingerprint != fingerprint)
                throw new InvalidOperationException("Economy operation key was reused with different data.");
            return false;
        }
        db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = id, UserId = user, Data = Json(new LagoonEconomyReceipt(fingerprint)) });
        return true;
    }
    public static async Task<LagoonEconomySnapshot> Get(ServerDbContext db, Guid user, int externalTier, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (wallet, record, state) = await Load(db, user, now, cancel);
        await AdvanceExternalPass(db, user, state, externalTier, now, cancel);
        if (state.ReferralCode.Length == 0)
        {
            state.ReferralCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
            db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = "refcode:" + state.ReferralCode, UserId = user });
        }
        Save(record, state);
        await db.SaveChangesAsync(cancel);
        beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, state);
    }
    public static async Task<LagoonCoinResult> Activity(ServerDbContext db, Guid user, LagoonActivity activity, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (activity.Ticks <= 0 || activity.Ticks > TimeSpan.FromHours(1).Ticks || activity.Rate is < 2 or > 6 ||
            !DateTime.TryParseExact(activity.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day) || activity.Round.Length > 80)
            throw new ArgumentException("Invalid activity.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (wallet, record, state) = await Load(db, user, now, cancel);
        if (!await Receipt(db, user, activity.Key, activity, cancel)) return new(wallet.LagoonCoins, 0, false);
        if (!state.MigratedTime)
        {
            state.IncomeTicks = checked(state.IncomeTicks + wallet.LagoonCoinPlayedTicks * 2 + wallet.LagoonCoinBonusTicks * 2);
            wallet.LagoonCoinPlayedTicks = wallet.LagoonCoinBonusTicks = 0;
            state.MigratedTime = true;
        }
        state.PlayedTicks = checked(state.PlayedTicks + activity.Ticks);
        state.IncomeTicks = checked(state.IncomeTicks + activity.Ticks * activity.Rate);
        var coins = state.IncomeTicks / TimeSpan.TicksPerHour;
        state.IncomeTicks %= TimeSpan.TicksPerHour;
        Credit(db, wallet, "activity:" + activity.Key, coins, "playtime", now, ticks: activity.Ticks);
        var priorDay = state.Days.GetValueOrDefault(activity.Day);
        var currentDay = checked(priorDay + activity.Ticks);
        state.Days[activity.Day] = currentDay;
        if (priorDay < LagoonEconomyRules.DailyActivity.Ticks && currentDay >= LagoonEconomyRules.DailyActivity.Ticks &&
            !await db.LagoonCoinOperations.AnyAsync(p => p.UserId == user && p.OperationId == "daily:" + activity.Day, cancel))
        {
            state.QualifiedDayCount++;
            Credit(db, wallet, "daily:" + activity.Day, LagoonEconomyRules.DailyReward, "daily-activity", now);
            coins += LagoonEconomyRules.DailyReward;
        }
        if (activity.Ready)
        {
            if (state.ReadyRound != activity.Round)
            {
                state.ReadyRound = activity.Round;
                state.ReadyTicks = 0;
                state.ReadyAwarded = false;
            }
            state.ReadyTicks = checked(state.ReadyTicks + activity.Ticks);
            if (!state.ReadyAwarded && state.ReadyTicks >= LagoonEconomyRules.ReadyActivity.Ticks &&
                !await db.LagoonCoinOperations.AnyAsync(p => p.UserId == user && p.OperationId == "round:" + activity.Round + ":ready", cancel))
            {
                state.ReadyAwarded = true;
                Credit(db, wallet, "round:" + activity.Round + ":ready", LagoonEconomyRules.ReadyReward, "ready", now);
                coins += LagoonEconomyRules.ReadyReward;
            }
        }
        // Only the last 35 days are needed for daily rewards and referral milestones.
        foreach (var key in state.Days.Keys.Where(k => string.CompareOrdinal(k, day.AddDays(-35).ToString("yyyy-MM-dd")) < 0).ToArray()) state.Days.Remove(key);
        Save(record, state);
        await db.SaveChangesAsync(cancel);
        beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, coins, true);
    }
    public static async Task<LagoonEconomySnapshot> Purchase(ServerDbContext db, Guid user, LagoonEconomyPurchase purchase, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (purchase.Price < 0 || purchase.Product.Length is < 1 or > 128) throw new ArgumentException("Invalid purchase.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (wallet, record, state) = await Load(db, user, now, cancel);
        object receipt = purchase.Kind == "furniture-pack"
            ? new { purchase.Kind, purchase.Product, purchase.Price, purchase.Quantity, purchase.PackSize, purchase.ExpectedUnitPrice }
            : new { purchase.Kind, purchase.Product, purchase.Price };
        if (!await Receipt(db, user, purchase.Key, receipt, cancel)) return new(wallet.LagoonCoins, state);
        await AdvanceExternalPass(db, user, state, purchase.ExternalTier, now, cancel);
        long price;
        switch (purchase.Kind)
        {
            case "pass":
                if (!int.TryParse(purchase.Product, out var tier) || tier is < 1 or > 4) throw new ArgumentException("Invalid subscription tier.");
                price = LagoonEconomyRules.PassPrice(tier);
                if (state.PassTicks > 0 && state.PassTier != tier)
                {
                    if (tier < state.PassTier) throw new InvalidOperationException("Дождитесь окончания текущего LC-периода для понижения уровня.");
                    price = checked(((price - LagoonEconomyRules.PassPrice(state.PassTier)) * state.PassTicks + LagoonEconomyRules.PassDuration.Ticks - 1) / LagoonEconomyRules.PassDuration.Ticks);
                    state.PassTier = tier;
                }
                else
                {
                    state.PassTier = tier;
                    state.PassTicks = checked(state.PassTicks + LagoonEconomyRules.PassDuration.Ticks);
                }
                break;
            case "furniture-pack":
                if (purchase.Quantity < 1 || purchase.PackSize is < 1 or > 400 || purchase.ExpectedUnitPrice == null)
                    throw new ArgumentException("Некорректное количество наборов.");
                var unitPrice = LagoonEconomyRules.DecorPrice(purchase.Price, Effective(state));
                if (unitPrice != purchase.ExpectedUnitPrice)
                    throw new InvalidOperationException("Цена изменилась. Проверьте новую стоимость и подтвердите покупку ещё раз.");
                price = checked(unitPrice * purchase.Quantity);
                state.FurnitureStock[purchase.Product] = checked(state.FurnitureStock.GetValueOrDefault(purchase.Product) + checked(purchase.PackSize * purchase.Quantity));
                var stockKey = "furniture-stock:" + user.ToString("N");
                var stockRecord = await db.LagoonEconomyRecords.SingleOrDefaultAsync(p => p.Id == stockKey, cancel);
                if (stockRecord == null)
                {
                    stockRecord = new LagoonEconomyRecord { Id = stockKey, UserId = user };
                    db.LagoonEconomyRecords.Add(stockRecord);
                }
                // A separately keyed record survives older account-service writers that do not know this state field.
                stockRecord.Data = Json(state.FurnitureStock);
                break;
            case "furniture":
                price = state.Furniture.Contains(purchase.Product) ? 0 : LagoonEconomyRules.DecorPrice(purchase.Price, Effective(state));
                state.Furniture.Add(purchase.Product);
                break;
            case "apartment-item":
                // Delivery is committed with the apartment snapshot. The receipt makes
                // retries after an interrupted delivery safe, without granting furniture stock.
                price = purchase.Price;
                break;
            case "apartment":
                price = Math.Max(0, purchase.Price - state.ApartmentCredit);
                state.ApartmentCredit = Math.Max(state.ApartmentCredit, purchase.Price);
                state.Apartments.Add(purchase.Product);
                break;
            default: throw new ArgumentException("Unknown purchase type.");
        }
        Credit(db, wallet, "spend:" + purchase.Key, -price, purchase.Kind + ":" + purchase.Product, now, user);
        Save(record, state);
        await db.SaveChangesAsync(cancel);
        beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, state, -price, true);
    }
    public static async Task SetColor(ServerDbContext db, Guid user, string color, int externalTier, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null, string? operationKey = null)
    {
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var rgb)) throw new ArgumentException("Укажите цвет #RRGGBB.");
        var r = (rgb >> 16) & 255; var g = (rgb >> 8) & 255; var b = rgb & 255;
        if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 16 && (r < 55 || r > 225))
            throw new ArgumentException("Выберите более различимый цвет.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var (_, record, state) = await Load(db, user, now, cancel);
        if (operationKey != null && !await Receipt(db, user, operationKey, new { kind = "color", color }, cancel)) return;
        await AdvanceExternalPass(db, user, state, externalTier, now, cancel);
        if (Effective(state) == 0) throw new InvalidOperationException("Цвет OOC доступен с подписки «Искра».");
        state.OocColor = color.ToUpperInvariant();
        Save(record, state); await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
    }
    public static async Task<LagoonCoinResult> ConfirmPayment(ServerDbContext db, Guid user, Guid actor, string receipt,
        int tier, string name, DateTime paidAt, bool historical, DateTime now, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        if (receipt.Length is < 1 or > 120 || name.Length is < 1 or > 64 || tier is < 1 or > 4 || paidAt == default || paidAt > now.AddMinutes(5)) throw new ArgumentException("Invalid payment.");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await AccountCurrencyOperations.LockAccount(db, user, cancel);
        var wallet = await db.Preference.SingleAsync(p => p.UserId == user, cancel);
        if (!await Receipt(db, user, "payment:" + receipt, new { tier, paidAt, historical }, cancel)) return new(wallet.LagoonCoins, 0, false);
        db.LagoonEconomyRecords.Add(new LagoonEconomyRecord { Id = "payment:" + receipt, UserId = user, Data = Json(new LagoonPayment(user, name, tier, paidAt, historical)) });
        var amount = historical ? 0 : LagoonEconomyRules.PaidGift(tier);
        Credit(db, wallet, "payment:" + receipt, amount, "paid-subscription:" + LagoonEconomyRules.Name(tier), now, actor);
        await db.SaveChangesAsync(cancel); beforeCommit?.Invoke(); await tx.CommitAsync(cancel);
        return new(wallet.LagoonCoins, amount, true);
    }
    public static async Task<List<LagoonPublicSupporter>> PublicSupporters(ServerDbContext db, CancellationToken cancel = default, Action? beforeCommit = null)
    {
        var records = await db.LagoonEconomyRecords.AsNoTracking().Where(p => p.Id.StartsWith("payment:")).ToListAsync(cancel);
        return records.Select(Read<LagoonPayment>).GroupBy(p => p.User).Select(g =>
            new LagoonPublicSupporter(g.OrderByDescending(p => p.PaidAt).First().Name, g.Min(p => p.PaidAt)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
