using System;
using System.IO;
using System.Linq;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared._DeepLagoon.ShuttleParking;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ParkingStoreTests
{
    [SetUp]
    public void Init()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }
    [TestCase(231, 22, 14, 1)]
    [TestCase(627, 23, 41, 2)]
    [TestCase(6538, 63, 136, 4)]
    [TestCase(200, 24, 10, 0)]
    [TestCase(200, 33, 10, 2)]
    [TestCase(451, 30, 30, 2)]
    public void ClassifiesActualHullAndGrowth(int tiles, float width, float height, int expected) =>
        Assert.That(ParkingRules.Category(tiles, width, height), Is.EqualTo(expected));

    [Test]
    public void PurchasesRetrySameReceiptAndOnlyAdditionalPlacesAreDiscounted()
    {
        using var store = new ParkingStore(":memory:"); var owner = Guid.NewGuid();
        var full = new[] {65,125,250,550,1000}; var half = new[] {33,63,125,275,500};
        for (var category = 0; category < 5; category++)
        {
            var purchase = store.BeginPurchase(owner, Guid.NewGuid(), category);
            Assert.That(purchase.Cost, Is.EqualTo(full[category]));
            Assert.That(store.BeginPurchase(owner, Guid.NewGuid(), category), Is.EqualTo(purchase));
            var otherCategory = (category + 1) % 5;
            Assert.Throws<InvalidOperationException>(() => store.BeginPurchase(owner, Guid.NewGuid(), otherCategory));
            store.GrantPurchase(owner, purchase); store.GrantPurchase(owner, purchase);
            Assert.That(store.BeginPurchase(owner, purchase.Id, category).Completed, Is.True);
            var additional = store.BeginPurchase(owner, Guid.NewGuid(), category);
            Assert.That(additional.Cost, Is.EqualTo(half[category]));
            store.GrantPurchase(owner, additional);
        }
        Assert.That(store.Slots(owner).Count, Is.EqualTo(10));
        Assert.That(store.Slots(Guid.NewGuid()), Is.Empty);
    }
    [Test]
    public void RestartRetainsSnapshotsAndPaidRedemptionWithoutSecondDelivery()
    {
        var path = Path.Combine(Path.GetTempPath(), "parking-" + Guid.NewGuid() + ".db");
        var owner = Guid.NewGuid(); var slot = Guid.NewGuid(); var ship = Ship(1);
        Guid operation;
        try
        {
            using (var store = new ParkingStore(path))
            {
                store.GrantPurchase(owner, store.BeginPurchase(owner, slot, 1));
                Assert.Throws<InvalidOperationException>(() => store.Slot(Guid.NewGuid(), slot));
                Assert.Throws<InvalidDataException>(() => store.Save(owner, slot, ship with { Checksum = "corrupt" }));
                store.Save(owner, slot, ship); // crash after durable commit, before live-grid removal
            }
            using (var store = new ParkingStore(path))
            {
                Assert.That(store.Slot(owner, slot).Phase, Is.EqualTo("stored"));
                Assert.That(store.Slot(owner, slot).Ship, Is.EqualTo(ship));
                Assert.Throws<InvalidOperationException>(() => store.Save(owner, slot, Ship(0)));
                var redeem = store.BeginRedeem(owner, slot, 2); operation = redeem.Redemption;
                Assert.Throws<InvalidOperationException>(() => store.BeginRedeem(owner, slot, 3));
                Assert.Throws<InvalidOperationException>(() => store.CompleteRedeem(owner, slot));
                store.MarkPaid(owner, slot);
            }
            using (var store = new ParkingStore(path))
            {
                var retry = store.BeginRedeem(owner, slot, 2);
                Assert.That(retry.Paid, Is.True); Assert.That(retry.Redemption, Is.EqualTo(operation));
                store.CompleteRedeem(owner, slot);
                Assert.That(store.Slot(owner, slot).Ship, Is.Null);
                Assert.Throws<InvalidOperationException>(() => store.BeginRedeem(owner, slot, 2));
                Assert.Throws<InvalidOperationException>(() => store.CompleteRedeem(owner, slot));
                store.Save(owner, slot, Ship(1)); store.FinishSave(owner, slot);
            }
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    [Test]
    public void GrownHullCannotReplaceSmallSlotAndOneShipCannotOccupyTwoPlaces()
    {
        using var store = new ParkingStore(":memory:"); var owner = Guid.NewGuid();
        var small = store.BeginPurchase(owner, Guid.NewGuid(), 0); store.GrantPurchase(owner, small);
        Assert.Throws<InvalidOperationException>(() => store.Save(owner, small.Id, Ship(1)));
        Assert.That(store.Slot(owner, small.Id).Ship, Is.Null);
        var big = store.BeginPurchase(owner, Guid.NewGuid(), 4); store.GrantPurchase(owner, big);
        var another = store.BeginPurchase(owner, Guid.NewGuid(), 4); store.GrantPurchase(owner, another);
        var ship = Ship(4); store.Save(owner, big.Id, ship);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Save(owner, another.Id, ship));
        Assert.That(store.Slot(owner, another.Id).Ship, Is.Null);
    }
    [Test]
    public void RedemptionRoundsUpAndRejectsOverflow()
    {
        Assert.That(ParkingRules.RedemptionPrice(81733), Is.EqualTo(204333));
        Assert.That(ParkingRules.RedemptionPrice(282000), Is.EqualTo(705000));
        Assert.Throws<OverflowException>(() => ParkingRules.RedemptionPrice(int.MaxValue));
    }
    private static StoredShuttle Ship(int category) => new(Guid.NewGuid(), "Изменённый шаттл", "Капитан", category, 81733, "grid data", ParkingStore.Hash("grid data"));
}
