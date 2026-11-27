using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record BasketSelection(Guid Offer, int Units);
public sealed record BasketQuery(Guid User, int Profile, Guid Cycle, BasketSelection[] Lines, string Prototype, int Units, string Search = "");
public sealed record BasketRow(Guid Offer, Guid User, int Profile, string Character, string Prototype, string Name,
    int Units, long Price, int RemainingUnits, long RemainingPrice);
public sealed record BasketPlan(Guid Id, Guid User, int Profile, Guid Cycle, long Until, BasketRow[] Rows);
public sealed record BasketPurchase(Guid Operation, BasketPlan Plan);
public sealed record BasketSeller(Guid User, int Slot);
public sealed record BasketResult(bool Applied, long Bank, BasketSeller[] Sellers, BasketRow[] Rows)
{
    public Guid[] Deliveries { get; init; } = [];
}

public static partial class EconomyOperations
{
    public static async Task<BasketPlan> QuoteBasket(ServerDbContext db, BasketQuery query, long now, CancellationToken cancel = default)
    {
        if (query.Lines.Length > 64 || query.Units is < 0 or > 1000000 || query.Prototype.Length > 160 || query.Search.Length > 100)
            throw new ArgumentException("invalid_basket");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        await RequireTradeCycle(db, query.Cycle, cancel);
        await Profile(db, query.User, query.Profile, cancel);
        var selections = query.Lines;
        if (query.Prototype.Length > 0)
        {
            if (query.Units < 1 || selections.Length != 0) throw new ArgumentException("invalid_basket");
            var json = await db.LagoonEconomyRecords.AsNoTracking().Where(r => r.Id.StartsWith(OfferPrefix)).Select(r => r.Data).ToListAsync(cancel);
            var candidates = json.Select(s => JsonSerializer.Deserialize<TradeOffer>(s)!).Where(o => o.Prototype == query.Prototype &&
                o.User != query.User && !o.BuyOrder && !o.Closed && o.Splittable && o.Expires > now &&
                (query.Search.Length == 0 || o.ItemName.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                    o.Description.Contains(query.Search, StringComparison.OrdinalIgnoreCase) || o.Character.Contains(query.Search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(o => (decimal)o.OriginalPrice / o.OriginalUnits).ThenBy(o => o.Created).ThenBy(o => o.Id);
            var planned = new System.Collections.Generic.List<BasketSelection>();
            var needed = query.Units;
            foreach (var offer in candidates)
            {
                var units = Math.Min(needed, offer.Criteria.StackUnits);
                planned.Add(new(offer.Id, units)); needed -= units;
                if (needed == 0) break;
                if (planned.Count == 64) throw new InvalidOperationException("basket_limit");
            }
            if (needed != 0) throw new InvalidOperationException("basket_stock");
            selections = planned.ToArray();
        }
        if (selections.Length is < 1 or > 64 || selections.Select(s => s.Offer).Distinct().Count() != selections.Length)
            throw new ArgumentException("invalid_basket");
        var rows = new System.Collections.Generic.List<BasketRow>();
        foreach (var selection in selections)
        {
            var (_, offer) = await Offer(db, selection.Offer, cancel);
            if (offer.BuyOrder || offer.Closed || offer.Expires <= now) throw new InvalidOperationException("basket_changed");
            if (offer.User == query.User) throw new InvalidOperationException("self_trade");
            await Profile(db, offer.User, offer.Profile, cancel);
            var units = selection.Units;
            if (units < 1 || units > offer.Criteria.StackUnits || !offer.Splittable && units != offer.Criteria.StackUnits)
                throw new InvalidOperationException("basket_changed");
            var price = offer.Splittable ? TradePricing.Cost(offer.OriginalPrice, offer.OriginalUnits, offer.Criteria.StackUnits, units) : offer.Price;
            rows.Add(new(offer.Id, offer.User, offer.Profile, offer.Character, offer.Prototype, offer.ItemName,
                units, price, offer.Criteria.StackUnits, offer.Price));
        }
        await tx.CommitAsync(cancel);
        return new(Guid.NewGuid(), query.User, query.Profile, query.Cycle, now + 45000, rows.ToArray());
    }

    private sealed record BasketReceipt(string Fingerprint, BasketResult Result);
    public static async Task<BasketResult> PurchaseBasket(ServerDbContext db, BasketPurchase request, long now, CancellationToken cancel = default)
    {
        var plan = request.Plan;
        if (request.Operation == Guid.Empty || plan.Id == Guid.Empty || plan.Rows.Length is < 1 or > 64 ||
            plan.Rows.Select(r => r.Offer).Distinct().Count() != plan.Rows.Length) throw new ArgumentException("invalid_basket");
        await using var tx = await db.Database.BeginTransactionAsync(cancel);
        await Lock(db, cancel);
        var key = "economy:basket:" + request.Operation.ToString("N");
        var fingerprint = Fingerprint(request);
        var receipt = await db.LagoonEconomyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Id == key, cancel);
        if (receipt != null)
        {
            var prior = JsonSerializer.Deserialize<BasketReceipt>(receipt.Data)!;
            if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("operation_mismatch");
            return prior.Result with { Applied = false };
        }
        await RequireTradeCycle(db, plan.Cycle, cancel);
        if (plan.Until <= now) throw new InvalidOperationException("quote_expired");
        await LockUsers(db, plan.Rows.Select(r => r.User).Append(plan.User), cancel);
        var buyer = await Profile(db, plan.User, plan.Profile, cancel);
        var total = plan.Rows.Sum(r => r.Price);
        if (buyer.BankBalance < total) throw new InvalidOperationException("insufficient_spessos");
        var sellers = new System.Collections.Generic.List<BasketSeller>();
        var deliveries = new System.Collections.Generic.List<Guid>();
        foreach (var line in plan.Rows)
        {
            var (row, offer) = await Offer(db, line.Offer, cancel);
            if (offer.BuyOrder || offer.Closed || offer.Expires <= now || offer.User != line.User || offer.Profile != line.Profile ||
                offer.User == plan.User || offer.Price != line.RemainingPrice || offer.Criteria.StackUnits != line.RemainingUnits ||
                line.Units < 1 || line.Units > offer.Criteria.StackUnits || !offer.Splittable && line.Units != offer.Criteria.StackUnits)
                throw new InvalidOperationException("basket_changed");
            var price = offer.Splittable ? TradePricing.Cost(offer.OriginalPrice, offer.OriginalUnits, offer.Criteria.StackUnits, line.Units) : offer.Price;
            if (price != line.Price || price < 0) throw new InvalidOperationException("basket_changed");
            var seller = await Profile(db, offer.User, offer.Profile, cancel);
            var item = await StoredItem(db, offer.Id, cancel);
            var delivered = item with { StackUnits = line.Units, Materials = item.Materials.ToDictionary(p => p.Key, p => p.Value * line.Units / item.StackUnits) };
            deliveries.Add(Deliver(db, plan.User, plan.Profile, delivered));
            buyer.BankBalance -= price;
            seller.BankBalance = checked(seller.BankBalance + price);
            BankFinancialOperation.Record(db, plan.User, buyer, price, "market_purchase");
            BankFinancialOperation.Record(db, offer.User, seller, price, "market_sale");
            sellers.Add(new(offer.User, seller.Slot));
            await NotifyCharacter(db, offer.User, offer.Profile, request.Operation, "market-" + offer.Id.ToString("N"),
                "Ваш товар купили", $"{item.Name} ×{line.Units}. Зачислено: {price:N0} спессо.", price, cancel);
            if (line.Units == offer.Criteria.StackUnits) { offer.Remaining = 0; offer.Closed = true; }
            else
            {
                offer.Criteria = offer.Criteria with { StackUnits = offer.Criteria.StackUnits - line.Units };
                offer.Price -= price;
                var stored = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == ItemPrefix + offer.Id.ToString("N"), cancel);
                stored.Data = JsonSerializer.Serialize(item with { StackUnits = offer.Criteria.StackUnits,
                    Materials = item.Materials.ToDictionary(p => p.Key, p => p.Value * offer.Criteria.StackUnits / item.StackUnits) });
            }
            await SaveOffer(db, row, offer, cancel);
        }
        var result = new BasketResult(true, buyer.BankBalance, sellers.Distinct().ToArray(), plan.Rows) { Deliveries = deliveries.ToArray() };
        db.LagoonEconomyRecords.Add(new() { Id = key, UserId = plan.User, Data = JsonSerializer.Serialize(new BasketReceipt(fingerprint, result)) });
        await db.SaveChangesAsync(cancel);
        await tx.CommitAsync(cancel);
        return result;
    }
}
