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
    private const string OfferPrefix = "economy:offer:";
    private const string ClosedPrefix = "economy:closed-offer:";
    private const string ItemPrefix = "economy:item:";
    private const string DeliveryPrefix = "economy:delivery:";
    private const string DeliveredPrefix = "economy:delivered:";
    private static void ValidateItem(TradeItem item)
    {
        if (item.Prototype.Length is < 1 or > 160 || item.Name.Length > 200 || item.Snapshot.Length is < 1 or > 2000000 ||
            item.Condition is < 0 or > 100 || item.Charge is < 0 or > 100 || item.StackUnits is < 1 or > 1000000)
            throw new ArgumentException("invalid_item");
    }
    private static bool Matches(TradeOffer order, TradeItem item) => item.Prototype == order.Prototype &&
        item.Condition >= order.Criteria.MinimumCondition && item.Charge >= order.Criteria.MinimumCharge &&
        item.StackUnits >= order.Criteria.StackUnits;
    private static async Task<(LagoonEconomyRecord Row, TradeOffer Offer)> Offer(ServerDbContext db, Guid id, CancellationToken cancel)
    {
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == OfferPrefix + id.ToString("N") || r.Id == ClosedPrefix + id.ToString("N"), cancel)
            ?? throw new InvalidOperationException("offer_missing");
        return (row, JsonSerializer.Deserialize<TradeOffer>(row.Data)!);
    }
    private static Guid Deliver(ServerDbContext db, Guid user, int profile, TradeItem item)
    {
        var id = Guid.NewGuid();
        db.LagoonEconomyRecords.Add(new() { Id = ItemPrefix + "delivery-" + id.ToString("N"), UserId = user, Data = JsonSerializer.Serialize(item) });
        db.LagoonEconomyRecords.Add(new() { Id = DeliveryPrefix + id.ToString("N"), UserId = user,
            Data = JsonSerializer.Serialize(new TradeDelivery { Id = id, User = user, Profile = profile,
                Name = item.Name, Prototype = item.Prototype, SplitStack = item.Splittable, StackUnits = item.StackUnits }) });
        return id;
    }
    private static async Task<TradeItem> StoredItem(ServerDbContext db, Guid offer, CancellationToken cancel)
        => JsonSerializer.Deserialize<TradeItem>((await db.LagoonEconomyRecords.SingleAsync(r => r.Id == ItemPrefix + offer.ToString("N"), cancel)).Data)!;
    private static async Task<TradeResult?> PriorTrade(ServerDbContext db, Guid operation, string fingerprint, CancellationToken cancel)
    {
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == "economy:trade:" + operation.ToString("N"), cancel);
        if (row == null) return null;
        var receipt = JsonSerializer.Deserialize<TradeReceipt>(row.Data)!;
        if (receipt.Fingerprint != fingerprint) throw new InvalidOperationException("operation_mismatch");
        return receipt.Result with { Applied = false };
    }
    private static void Receipt(ServerDbContext db, Guid user, string fingerprint, TradeResult result)
        => db.LagoonEconomyRecords.Add(new() { Id = "economy:trade:" + result.Operation.ToString("N"), UserId = user,
            Data = JsonSerializer.Serialize(new TradeReceipt(fingerprint, result)) });

    public static async Task<TradeResult> CreateOffer(ServerDbContext db, TradeCreate request, long now, CancellationToken cancel = default)
    {
        if (request.Operation == Guid.Empty || request.User == Guid.Empty || request.Profile <= 0 || request.Quantity is < 1 or > 100 ||
            request.Price is < 1 or > 1000000000 || request.Prototype.Length is < 1 or > 160 || request.Name.Length > 200 ||
            request.Description.Length > 500 || request.Criteria.MinimumCondition is < 0 or > 100 || request.Criteria.MinimumCharge is < 0 or > 100 ||
            request.Criteria.StackUnits is < 1 or > 1000000 || !request.BuyOrder && (request.Item == null || request.Quantity != 1))
            throw new ArgumentException("invalid_offer");
        if (request.Item != null) ValidateItem(request.Item);
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await LockUsers(db, [request.User], cancel);
        var fingerprint = Fingerprint(request);
        if (await PriorTrade(db, request.Operation, fingerprint, cancel) is { } prior) return prior;
        await RequireTradeCycle(db, request.Cycle, cancel);
        var profile = await Profile(db, request.User, request.Profile, cancel);
        var mine = await db.LagoonEconomyRecords.Where(r => r.UserId == request.User && r.Id.StartsWith(OfferPrefix)).Select(r => r.Data).ToListAsync(cancel);
        if (mine.Count(s => !JsonSerializer.Deserialize<TradeOffer>(s)!.Closed) >= 50 ||
            await db.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith(OfferPrefix), cancel) >= 2000)
            throw new InvalidOperationException("offer_limit");
        var reserved = request.BuyOrder ? checked(request.Price * request.Quantity) : 0;
        if (profile.BankBalance < reserved) throw new InvalidOperationException("insufficient_spessos");
        if (reserved > 0)
        {
            profile.BankBalance -= reserved;
            BankFinancialOperation.Record(db, request.User, profile, reserved, "market_escrow");
        }
        var id = request.Operation;
        var offer = new TradeOffer { Id = id, User = request.User, Profile = request.Profile, Character = profile.CharacterName,
            BuyOrder = request.BuyOrder, Prototype = request.Prototype, ItemName = request.Name, Description = request.Description,
            Remaining = request.Quantity, Price = request.Price,
            Criteria = request.BuyOrder ? request.Criteria : new(request.Item!.Condition, request.Item.Charge, request.Item.StackUnits), Escrow = reserved,
            HasBattery = request.Item?.HasBattery,
            Splittable = !request.BuyOrder && request.Item!.Splittable,
            OriginalPrice = request.Price, OriginalUnits = request.Item?.StackUnits ?? 1,
            Created = now, Expires = request.Cycle == Guid.Empty ? now + 604800000L : long.MaxValue };
        db.LagoonEconomyRecords.Add(new() { Id = OfferPrefix + id.ToString("N"), UserId = request.User, Data = JsonSerializer.Serialize(offer) });
        if (!request.BuyOrder)
            db.LagoonEconomyRecords.Add(new() { Id = ItemPrefix + id.ToString("N"), UserId = request.User, Data = JsonSerializer.Serialize(request.Item) });
        if (request.Recovery != null) await CrashRecoveryOperations.RemoveInTransaction(db, request.Recovery, cancel);
        var result = new TradeResult(request.Operation, true, profile.BankBalance, id);
        Receipt(db, request.User, fingerprint, result);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    public static async Task<TradeResult> Deal(ServerDbContext db, TradeDeal request, long now, CancellationToken cancel = default)
    {
        if (request.Operation == Guid.Empty || request.User == Guid.Empty || request.Profile <= 0) throw new ArgumentException("invalid_deal");
        if (request.Item != null) ValidateItem(request.Item);
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var fingerprint = Fingerprint(request);
        if (await PriorTrade(db, request.Operation, fingerprint, cancel) is { } prior) return prior;
        await RequireTradeCycle(db, Guid.Empty, cancel);
        var (row, offer) = await Offer(db, request.Offer, cancel);
        await LockUsers(db, [request.User, offer.User], cancel);
        var actor = await Profile(db, request.User, request.Profile, cancel);
        if (offer.Closed || offer.Remaining <= 0 || now >= offer.Expires) throw new InvalidOperationException("offer_closed");
        if (offer.User == request.User) throw new InvalidOperationException("self_trade");
        var owner = await Profile(db, offer.User, offer.Profile, cancel);
        TradeItem item;
        Guid delivery;
        if (offer.BuyOrder)
        {
            item = request.Item ?? throw new InvalidOperationException("supply_item");
            if (!Matches(offer, item)) throw new InvalidOperationException("item_mismatch");
            if (offer.Escrow < offer.Price) throw new InvalidOperationException("invalid_escrow");
            offer.Escrow -= offer.Price;
            actor.BankBalance = checked(actor.BankBalance + offer.Price);
            BankFinancialOperation.Record(db, request.User, actor, offer.Price, "market_sale");
            delivery = Deliver(db, offer.User, offer.Profile, item);
            if (request.Recovery != null) await CrashRecoveryOperations.RemoveInTransaction(db, request.Recovery, cancel);
        }
        else
        {
            if (request.Item != null) throw new InvalidOperationException("item_mismatch");
            if (actor.BankBalance < offer.Price) throw new InvalidOperationException("insufficient_spessos");
            item = await StoredItem(db, offer.Id, cancel);
            actor.BankBalance -= offer.Price;
            owner.BankBalance = checked(owner.BankBalance + offer.Price);
            BankFinancialOperation.Record(db, request.User, actor, offer.Price, "market_purchase");
            BankFinancialOperation.Record(db, offer.User, owner, offer.Price, "market_sale");
            delivery = Deliver(db, request.User, request.Profile, item);
        }
        offer.Remaining--;
        offer.Closed = offer.Remaining == 0;
        await NotifyCharacter(db, offer.User, offer.Profile, request.Operation, "market-order",
            offer.BuyOrder ? "Заявка на покупку выполнена" : "Ваш товар купили",
            $"{item.Name} ×{item.StackUnits}. Сумма сделки: {offer.Price:N0} спессо.", offer.BuyOrder ? -offer.Price : offer.Price, cancel);
        await SaveOffer(db, row, offer, cancel);
        var result = new TradeResult(request.Operation, true, actor.BankBalance, offer.Id, delivery,
            offer.User, owner.Slot, item.Prototype, offer.Price, item.Materials);
        Receipt(db, request.User, fingerprint, result);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    public static async Task<TradeResult> CancelOffer(ServerDbContext db, Guid user, int profileId, Guid id, Guid operation,
        long now, CancellationToken cancel = default)
    {
        if (operation == Guid.Empty) throw new ArgumentException("invalid_operation");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await LockUsers(db, [user], cancel);
        var fingerprint = Fingerprint(new { user, profileId, id, operation });
        if (await PriorTrade(db, operation, fingerprint, cancel) is { } prior) return prior;
        var profile = await Profile(db, user, profileId, cancel);
        var (row, offer) = await Offer(db, id, cancel);
        if (offer.User != user || offer.Profile != profileId) throw new InvalidOperationException("not_owner");
        Guid? delivery = null;
        if (!offer.Closed)
        {
            if (offer.BuyOrder)
            {
                profile.BankBalance = checked(profile.BankBalance + offer.Escrow);
                if (offer.Escrow > 0) BankFinancialOperation.Record(db, user, profile, offer.Escrow,
                    offer.ClosureReason == "round_end" ? "market_round_refund" : "market_refund");
                offer.Escrow = 0;
            }
            else delivery = Deliver(db, user, profileId, await StoredItem(db, id, cancel));
            offer.Closed = true;
            offer.Remaining = 0;
            await SaveOffer(db, row, offer, cancel);
        }
        var result = new TradeResult(operation, true, profile.BankBalance, id, delivery);
        Receipt(db, user, fingerprint, result);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }

    private static async Task SaveOffer(ServerDbContext db, LagoonEconomyRecord row, TradeOffer offer, CancellationToken cancel)
    {
        if (!offer.Closed) { row.Data = JsonSerializer.Serialize(offer); return; }
        var comments = CommentPrefix + offer.Id.ToString("N") + ":";
        db.LagoonEconomyRecords.RemoveRange(await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith(comments)).ToListAsync(cancel));
        db.LagoonEconomyRecords.Remove(row);
        db.LagoonEconomyRecords.Add(new() { Id = ClosedPrefix + offer.Id.ToString("N"), UserId = offer.User, Data = JsonSerializer.Serialize(offer) });
        var item = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == ItemPrefix + offer.Id.ToString("N"), cancel);
        if (item != null) db.LagoonEconomyRecords.Remove(item);
    }

    public static async Task ExpireOffers(ServerDbContext db, long now, CancellationToken cancel = default)
    {
        var rows = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(OfferPrefix)).Select(r => r.Data).ToListAsync(cancel);
        foreach (var offer in rows.Select(s => JsonSerializer.Deserialize<TradeOffer>(s)!).Where(o => !o.Closed && o.Expires <= now).Take(20))
        {
            var operation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("expire:" + offer.Id)).AsSpan(0, 16));
            try { await CancelOffer(db, offer.User, offer.Profile, offer.Id, operation, now, cancel); }
            catch (InvalidOperationException e) when (e.Message == "character_missing" || e.Message == "Player preferences do not exist.")
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancel);
                await Lock(db, cancel);
                if (!await db.Profile.AnyAsync(p => p.Id == offer.Profile && p.Preference.UserId == offer.User, cancel))
                {
                    var (row, orphan) = await Offer(db, offer.Id, cancel);
                    if (!orphan.Closed)
                    {
                        // Retain the original escrow/item for an administrative audit, never redirect it to a reused slot.
                        orphan.Closed = orphan.Orphaned = true;
                        db.LagoonEconomyRecords.Remove(row);
                        db.LagoonEconomyRecords.Add(new() { Id = ClosedPrefix + orphan.Id.ToString("N"), UserId = orphan.User, Data = JsonSerializer.Serialize(orphan) });
                        await db.SaveChangesAsync(cancel);
                    }
                }
                await transaction.CommitAsync(cancel);
            }
            db.ChangeTracker.Clear();
        }
    }

    public static async Task<TradeBoard> Board(ServerDbContext db, Guid user, int profileId, CancellationToken cancel = default)
    {
        var profile = await Profile(db, user, profileId, cancel);
        var offers = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(OfferPrefix)).OrderBy(r => r.Id)
            .Take(2000).Select(r => r.Data).ToListAsync(cancel);
        var cycleJson = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id == TradeCycleId).Select(r => r.Data).SingleOrDefaultAsync(cancel);
        if (cycleJson != null && JsonSerializer.Deserialize<TradeCycle>(cycleJson)!.Closed) offers.Clear();
        var deliveries = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(DeliveryPrefix) && r.UserId == user)
            .Select(r => r.Data).ToListAsync(cancel);
        return new(offers.Select(s => JsonSerializer.Deserialize<TradeOffer>(s)!).Where(o => !o.Closed).ToArray(),
            deliveries.Select(s => JsonSerializer.Deserialize<TradeDelivery>(s)!).Where(d => !d.Delivered && d.Profile == profileId).ToArray(),
            profile.BankBalance, profileId);
    }

    public static async Task<TradeDelivery> ClaimDelivery(ServerDbContext db, Guid user, int profileId, Guid id, Guid lease,
        long now, CancellationToken cancel = default)
    {
        if (lease == Guid.Empty) throw new ArgumentException("invalid_lease");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await Profile(db, user, profileId, cancel);
        var (row, delivery) = await Load<TradeDelivery>(db, DeliveryPrefix + id.ToString("N"), user, cancel);
        if (delivery.Id != id || delivery.User != user || delivery.Profile != profileId || delivery.Delivered)
            throw new InvalidOperationException("delivery_missing");
        if (delivery.Lease != Guid.Empty && delivery.Lease != lease && delivery.LeaseUntil > now)
            throw new InvalidOperationException("delivery_busy");
        delivery.Lease = lease;
        delivery.LeaseUntil = now + 180000;
        row.Data = JsonSerializer.Serialize(delivery);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        var payload = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == ItemPrefix + "delivery-" + id.ToString("N"), cancel);
        delivery.Snapshot = JsonSerializer.Deserialize<TradeItem>(payload.Data)!.Snapshot;
        return delivery;
    }

    public static async Task CompleteDelivery(ServerDbContext db, Guid user, int profile, Guid id, Guid lease,
        RecoveryExternal? recovery, CancellationToken cancel = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var row = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r =>
            r.Id == DeliveryPrefix + id.ToString("N") || r.Id == DeliveredPrefix + id.ToString("N"), cancel)
            ?? throw new InvalidOperationException("delivery_missing");
        var delivery = JsonSerializer.Deserialize<TradeDelivery>(row.Data)!;
        if (delivery.Id != id || delivery.User != user || delivery.Profile != profile || delivery.Lease != lease)
            throw new InvalidOperationException("delivery_missing");
        if (delivery.Delivered) return;
        if (recovery != null) await CrashRecoveryOperations.ImportExternal(db, recovery, user, profile, cancel);
        delivery.Delivered = true;
        db.LagoonEconomyRecords.Remove(row);
        db.LagoonEconomyRecords.Add(new() { Id = DeliveredPrefix + id.ToString("N"), UserId = user, Data = JsonSerializer.Serialize(delivery) });
        var item = await db.LagoonEconomyRecords.SingleOrDefaultAsync(r => r.Id == ItemPrefix + "delivery-" + id.ToString("N"), cancel);
        if (item != null) db.LagoonEconomyRecords.Remove(item);
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
    }
}
