using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.Database;
using Content.Shared.Preferences;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Tests.Server;

public sealed partial class SectorEconomyTests
{
    private async Task<(Guid User, int Profile)> BasketTrader()
    {
        var user = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        { db.Preference.Add(new Preference { UserId = user, AdminOOCColor = "#FF0000" }); await db.SaveChangesAsync(); }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(user), new HumanoidCharacterProfile().WithBankBalance(100000), 0);
        return (user, await ProfileId(user));
    }
    private async Task<Guid> BasketLot((Guid User, int Profile) seller, Guid cycle, int units, long price, bool split = true)
    {
        var item = new TradeItem("SheetSteel", "Steel", "FULL UNMODIFIED SNAPSHOT", 100, 0, units, new() { ["Steel"] = units }) { Splittable = split };
        var request = new TradeCreate(Guid.NewGuid(), seller.User, seller.Profile, false, item.Prototype, item.Name, "", 1, price, new(), item, null) { Cycle = cycle };
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.CreateOffer(db, request, 1);
        return request.Operation;
    }
    private async Task<BasketPlan> BasketQuote(BasketQuery query)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.QuoteBasket(db, query, 2); }
    private async Task<BasketResult> BasketBuy(BasketPurchase request, long now = 3)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.PurchaseBasket(db, request, now); }

    [Test]
    public async Task CombinedPurchaseUsesEachSellersPriceSplitsStacksAndReplaysExactlyOnce()
    {
        var a = await BasketTrader(); var b = await BasketTrader(); var c = await BasketTrader();
        var profile = await ProfileId(_user); var cycle = (await BeginCycle()).Id;
        var first = await BasketLot(a, cycle, 30, 3000);
        var second = await BasketLot(b, cycle, 50, 7000);
        await BasketLot(c, cycle, 15, 15000);
        await BasketLot((_user, profile), cycle, 100, 100); // Self-trades are never an auto-fill source.
        var plan = await BasketQuote(new(_user, profile, cycle, [], "SheetSteel", 50));
        Assert.That(plan.Rows.Select(r => (r.Offer, r.Units, r.Price)), Is.EqualTo(new[] { (first, 30, 3000L), (second, 20, 2800L) }));
        var request = new BasketPurchase(Guid.NewGuid(), plan);
        var paid = await BasketBuy(request);
        Assert.That(paid.Bank, Is.EqualTo(794200)); Assert.That(paid.Deliveries.Distinct().Count(), Is.EqualTo(2));
        Assert.That((await BasketBuy(request, 999999)).Applied, Is.False, "A committed retry remains idempotent after quote expiration.");
        await using var db = new SqliteServerDbContext(_options);
        var board = await EconomyOperations.Board(db, _user, profile);
        Assert.That(board.Deliveries, Has.Length.EqualTo(2));
        Assert.That(board.Deliveries.Select(d => d.StackUnits).Order(), Is.EqualTo(new[] { 20, 30 }));
        var remainder = board.Offers.Single(o => o.Id == second);
        Assert.That(remainder.Criteria.StackUnits, Is.EqualTo(30)); Assert.That(remainder.Price, Is.EqualTo(4200));
        Assert.That((await EconomyOperations.Read(db, a.User, 0)).Bank, Is.EqualTo(103000));
        Assert.That((await EconomyOperations.Read(db, b.User, 0)).Bank, Is.EqualTo(102800));
        await EconomyOperations.CancelOffer(db, b.User, b.Profile, second, Guid.NewGuid(), 4);
        var returned = (await EconomyOperations.Board(db, b.User, b.Profile)).Deliveries.Single();
        Assert.That(returned.StackUnits, Is.EqualTo(30));
        Assert.That((await EconomyOperations.ClaimDelivery(db, b.User, b.Profile, returned.Id, Guid.NewGuid(), 5)).Snapshot, Is.EqualTo("FULL UNMODIFIED SNAPSHOT"));
    }

    [Test]
    public async Task ChangedOrExpiredCombinedQuotesNeverPartiallyChargeTheBuyer()
    {
        var seller = await BasketTrader(); var competitor = await BasketTrader();
        var profile = await ProfileId(_user); var cycle = (await BeginCycle()).Id;
        var a = await BasketLot(seller, cycle, 30, 3000); var b = await BasketLot(seller, cycle, 20, 2800);
        var plan = await BasketQuote(new(_user, profile, cycle, [new(a, 30), new(b, 20)], "", 0));
        var competing = await BasketQuote(new(competitor.User, competitor.Profile, cycle, [new(b, 20)], "", 0));
        await BasketBuy(new(Guid.NewGuid(), competing));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => BasketBuy(new(Guid.NewGuid(), plan)))!.Message, Is.EqualTo("basket_changed"));
        await using var db = new SqliteServerDbContext(_options);
        var board = await EconomyOperations.Board(db, _user, profile);
        Assert.That(board.Bank, Is.EqualTo(800000)); Assert.That(board.Deliveries, Is.Empty);
        Assert.That(board.Offers.Single(o => o.Id == a).Criteria.StackUnits, Is.EqualTo(30));
        var fresh = await BasketQuote(new(_user, profile, cycle, [new(a, 10)], "", 0));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => BasketBuy(new(Guid.NewGuid(), fresh), 45003))!.Message, Is.EqualTo("quote_expired"));
    }

    [Test]
    public async Task ConcurrentBuyersCannotConsumeTheSameLotTwiceAndWholeItemsCannotBeSplit()
    {
        var seller = await BasketTrader(); var other = await BasketTrader(); var profile = await ProfileId(_user);
        var cycle = (await BeginCycle()).Id; var lot = await BasketLot(seller, cycle, 50, 2000, false);
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => BasketQuote(new(_user, profile, cycle, [new(lot, 10)], "", 0)))!.Message, Is.EqualTo("basket_changed"));
        var p = await BasketQuote(new(_user, profile, cycle, [new(lot, 50)], "", 0));
        var q = await BasketQuote(new(other.User, other.Profile, cycle, [new(lot, 50)], "", 0));
        async Task<bool> Buy(BasketPlan plan)
        { try { await BasketBuy(new(Guid.NewGuid(), plan)); return true; } catch (InvalidOperationException e) when (e.Message == "basket_changed") { return false; } }
        Assert.That((await Task.WhenAll(Buy(p), Buy(q))).Count(v => v), Is.EqualTo(1));
        await using var db = new SqliteServerDbContext(_options);
        Assert.That((await EconomyOperations.Read(db, seller.User, 0)).Bank, Is.EqualTo(102000));
    }

    [Test]
    public void PartialPriceRoundingConservesTheWholeLotPrice()
    {
        Assert.That(new[] { TradePricing.Cost(101, 3, 3, 1), TradePricing.Cost(101, 3, 2, 1), TradePricing.Cost(101, 3, 1, 1) }, Is.EqualTo(new[] { 34L, 34L, 33L }));
    }
}
