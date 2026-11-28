using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Content.Tests.Server;

public sealed partial class SectorEconomyTests
{
    private async Task<TradeCycle> BeginCycle(bool allowNew = true)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.BeginTradeCycle(db, allowNew); }
    private async Task<TradeCycle> CloseCycle(Guid expected)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.CloseTradeCycle(db, expected); }
    private async Task ExpireCycle()
    { await using var db = new SqliteServerDbContext(_options); await EconomyOperations.ExpireOffers(db, 1); }
    private async Task CreateCycleOffer(TradeCreate request)
    { await using var db = new SqliteServerDbContext(_options); await EconomyOperations.CreateOffer(db, request, 0); }

    [Test]
    public async Task CrashReopenAndUpgradeRetainListingsReservesAndFullItemSnapshotsBeyondSevenDays()
    {
        var profile = await ProfileId(_user);
        var legacy = new TradeCreate(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 2, 5000, new(), null, null);
        await CreateCycleOffer(legacy);
        var cycle = await BeginCycle();
        var item = new TradeItem("Flatpack", "Customized cannon", "FULL SNAPSHOT WITH CONTENTS AND SETTINGS", 81, 0, 1, new());
        var sale = new TradeCreate(Guid.NewGuid(), _user, profile, false, item.Prototype, item.Name, "Long lived listing", 1, 20000, new(), item, null) { Cycle = cycle.Id };
        await CreateCycleOffer(sale);
        // Fresh file-backed contexts stand in for a new process. No normal-end marker was committed.
        Assert.That((await BeginCycle()).Id, Is.EqualTo(cycle.Id));
        await using var db = new SqliteServerDbContext(_options);
        await EconomyOperations.ExpireOffers(db, 1209600000L);
        var board = await EconomyOperations.Board(db, _user, profile);
        Assert.That(board.Offers, Has.Length.EqualTo(2));
        Assert.That(board.Offers.All(o => o.Expires == long.MaxValue), Is.True);
        Assert.That(board.Bank, Is.EqualTo(790000));
        var stored = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == "economy:item:" + sale.Operation.ToString("N"));
        Assert.That(JsonSerializer.Deserialize<TradeItem>(stored.Data)!.Snapshot, Is.EqualTo(item.Snapshot));
        Assert.That((await EconomyOperations.CreateOffer(db, legacy, 1209600000L)).Applied, Is.False,
            "Adding a cycle key must not change old transaction fingerprints.");
    }

    [Test]
    public async Task NormalEndClosesTheBoardRejectsLateWritesAndReturnsEachReserveAndItemExactlyOnce()
    {
        var profile = await ProfileId(_user);
        var cycle = await BeginCycle();
        var buy = new TradeCreate(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 3, 5000, new(), null, null) { Cycle = cycle.Id };
        var item = new TradeItem("Flatpack", "Packed gun", "ORIGINAL FULL ITEM STATE", 100, 0, 1, new());
        var sale = buy with { Operation = Guid.NewGuid(), BuyOrder = false, Prototype = item.Prototype, Quantity = 1, Item = item };
        await CreateCycleOffer(buy); await CreateCycleOffer(sale);
        Assert.That((await CloseCycle(cycle.Id)).Closed, Is.True);
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Board(db, _user, profile)).Offers, Is.Empty);
            var late = buy with { Operation = Guid.NewGuid() };
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => EconomyOperations.CreateOffer(db, late, 2))!.Message, Is.EqualTo("round_closed"));
        }
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => EconomyOperations.Deal(db,
                new(Guid.NewGuid(), _user, profile, sale.Operation, null, null), 2))!.Message, Is.EqualTo("round_closed"));
        await ExpireCycle(); await CloseCycle(cycle.Id); await ExpireCycle();
        await using (var db = new SqliteServerDbContext(_options))
        {
            var board = await EconomyOperations.Board(db, _user, profile);
            Assert.That(board.Bank, Is.EqualTo(800000));
            Assert.That(board.Deliveries, Has.Length.EqualTo(1));
            var returned = await EconomyOperations.ClaimDelivery(db, _user, profile,
                board.Deliveries.Single().Id, Guid.NewGuid(), 2);
            Assert.That(returned.Snapshot, Is.EqualTo(item.Snapshot));
            Assert.That(await db.BankFinancialOperations.CountAsync(), Is.EqualTo(2));
        }
        var next = await BeginCycle();
        Assert.That(next.Id, Is.Not.EqualTo(cycle.Id));
        await CreateCycleOffer(buy with { Operation = Guid.NewGuid(), Cycle = next.Id });
        Assert.That((await CloseCycle(cycle.Id)).Closed, Is.False, "A repeated old end event must not close the new cycle.");
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That((await EconomyOperations.Board(db, _user, profile)).Offers, Has.Length.EqualTo(1));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(() => EconomyOperations.CreateOffer(db,
                buy with { Operation = Guid.NewGuid() }, 3))!.Message, Is.EqualTo("round_closed"));
    }

    [Test]
    public async Task CrashDuringRoundEndSettlementResumesWithoutOpeningASecondMarketOrDuplicatingRefunds()
    {
        var profile = await ProfileId(_user);
        var cycle = await BeginCycle();
        for (var i = 0; i < 25; i++)
            await CreateCycleOffer(new(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 1, 1, new(), null, null) { Cycle = cycle.Id });
        await CloseCycle(cycle.Id);
        await ExpireCycle(); // One bounded batch returns twenty reserves.
        var recovered = await BeginCycle();
        Assert.That(recovered.Id, Is.EqualTo(cycle.Id));
        Assert.That(recovered.Closed && recovered.PendingOffers, Is.True);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That((await EconomyOperations.Board(db, _user, profile)).Bank, Is.EqualTo(799995));
        await ExpireCycle(); await ExpireCycle();
        var next = await BeginCycle();
        Assert.That(next.Closed, Is.False); Assert.That(next.Id, Is.Not.EqualTo(cycle.Id));
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Board(db, _user, profile)).Bank, Is.EqualTo(800000));
            Assert.That(await db.BankFinancialOperations.CountAsync(), Is.EqualTo(50));
        }
    }
}
