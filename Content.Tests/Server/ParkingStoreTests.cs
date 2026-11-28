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
    [Test]
    public void ResourceSaleReceiptAndSnapshotRollbackTogetherWhenReceiptWriteFails()
    {
        var path = Path.Combine(Path.GetTempPath(), "parking-sale-" + Guid.NewGuid() + ".db");
        try
        {
            using var store = new ParkingStore(path);
            var owner = Guid.NewGuid();
            var purchase = store.BeginPurchase(owner, Guid.NewGuid(), 1);
            store.GrantPurchase(owner, purchase);
            var ship = Ship(1);
            var sale = new ParkingPayout(Guid.NewGuid(), owner, 0, 125000);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_sale BEFORE INSERT ON parking_payouts BEGIN SELECT RAISE(ABORT, 'simulated receipt write failure'); END";
            command.ExecuteNonQuery();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => store.Save(owner, purchase.Id, ship, sale));
            Assert.That(store.Slot(owner, purchase.Id).Ship, Is.Null);
            Assert.That(store.Slot(owner, purchase.Id).Phase, Is.EqualTo("empty"));
            Assert.That(store.PendingPayouts(), Is.Empty);
            command.CommandText = "DROP TRIGGER reject_sale";
            command.ExecuteNonQuery();
            store.Save(owner, purchase.Id, ship, sale);
            Assert.That(store.PendingPayouts(), Is.EquivalentTo(new[] { sale }));
            Assert.Throws<InvalidOperationException>(() => store.Save(owner, purchase.Id, ship, sale));
            Assert.That(store.PendingPayouts(), Has.Count.EqualTo(1));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [TestCase(231, 22, 14, 1)]
    [TestCase(627, 23, 41, 2)]
    [TestCase(6538, 63, 136, 4)]
    [TestCase(200, 24, 10, 0)]
    [TestCase(200, 33, 10, 2)]
    [TestCase(451, 30, 30, 2)]
    public void ClassifiesActualHullAndGrowth(int tiles, float width, float height, int expected) =>
        Assert.That(ParkingRules.Category(tiles, width, height), Is.EqualTo(expected));

    [TestCase(1, 700, 24, 48, 700, 24, 48, 1)]
    [TestCase(1, 700, 48, 24, 700, 24, 48, 1)]
    [TestCase(1, 100, 10, 10, 700, 24, 48, 1)]
    [TestCase(1, 701, 24, 48, 700, 24, 48, 2)]
    [TestCase(1, 700, 24, 49, 700, 24, 48, 2)]
    [TestCase(1, 700, 24, 97, 700, 24, 48, 3)]
    [TestCase(1, 700, 24, 151, 700, 24, 48, 4)]
    [TestCase(0, 200, 24, 10, 100, 10, 10, 0)]
    [TestCase(0, 201, 24, 10, 100, 10, 10, 1)]
    [TestCase(2, 6538, 63, 136, 6538, 63, 136, 2)]
    [TestCase(2, 6539, 63, 136, 6538, 63, 136, 3)]
    [TestCase(2, 22000, 63, 136, 6538, 63, 136, 4)]
    public void CatalogCategoryAllowsStockHullAndStillLimitsExtensions(int category, int tiles, float width, float height,
        int stockTiles, float stockWidth, float stockHeight, int expected) =>
        Assert.That(ParkingRules.VesselCategory(category, tiles, width, height, stockTiles, stockWidth, stockHeight), Is.EqualTo(expected));

    [Test]
    public void CatalogCategoryRejectsInvalidHullAndCategory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ParkingRules.VesselCategory(1, 0, 24, 48, 700, 24, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParkingRules.VesselCategory(1, 700, float.NaN, 48, 700, 24, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParkingRules.VesselCategory(1, 700, 24, 48, 0, 24, 48));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParkingRules.VesselCategory(4, 700, 24, 48, 700, 24, 48));
    }

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
    public void SameShiftReturnOpensAtFiveMinutesAndConsumesOnlyTheMatchingSnapshot()
    {
        using var store = new ParkingStore(":memory:");
        var owner = Guid.NewGuid();
        var shift = Guid.NewGuid();
        var purchase = store.BeginPurchase(owner, Guid.NewGuid(), 1);
        store.GrantPurchase(owner, purchase);
        var deadline = TimeSpan.FromMinutes(5);
        var ship = Ship(1) with { SavedShift = shift, FreeReturnAt = deadline };
        store.Save(owner, purchase.Id, ship with { FreeReturnAt = null });
        store.FinishSave(owner, purchase.Id, deadline);

        Assert.Throws<InvalidOperationException>(() => store.BeginRedeem(owner, purchase.Id, 0, shift, deadline - TimeSpan.FromTicks(1)));
        var ready = store.BeginRedeem(owner, purchase.Id, 0, shift, deadline);
        Assert.That(ready.Phase, Is.EqualTo("stored"));
        Assert.That(ready.Redemption, Is.EqualTo(Guid.Empty));
        Assert.That(ready.Paid, Is.False);
        Assert.Throws<InvalidOperationException>(() => store.CompleteRedeem(owner, purchase.Id));
        Assert.Throws<InvalidOperationException>(() => store.CompleteFreeReturn(owner, purchase.Id, ship with { Identity = Guid.NewGuid() }, shift, deadline));
        Assert.Throws<InvalidOperationException>(() => store.CompleteFreeReturn(owner, purchase.Id, ship, Guid.NewGuid(), deadline));
        store.CompleteFreeReturn(owner, purchase.Id, ship, shift, deadline);
        Assert.That(store.Slot(owner, purchase.Id).Ship, Is.Null);
        Assert.Throws<InvalidOperationException>(() => store.CompleteFreeReturn(owner, purchase.Id, ship, shift, deadline));
    }

    [Test]
    public void InterruptedFreeReturnDoesNotCarryPaymentOrCharacterLockIntoNextShift()
    {
        using var store = new ParkingStore(":memory:");
        var owner = Guid.NewGuid();
        var shift = Guid.NewGuid();
        var purchase = store.BeginPurchase(owner, Guid.NewGuid(), 1);
        store.GrantPurchase(owner, purchase);
        var deadline = ParkingRules.FreeReturnDelay;
        var ship = Ship(1) with { SavedShift = shift, FreeReturnAt = deadline };
        store.Save(owner, purchase.Id, ship);
        store.FinishSave(owner, purchase.Id);

        store.BeginRedeem(owner, purchase.Id, 0, shift, deadline);
        var later = store.BeginRedeem(owner, purchase.Id, 1, Guid.NewGuid(), TimeSpan.Zero);
        Assert.That(later.Phase, Is.EqualTo("redeeming"));
        Assert.That(later.Character, Is.EqualTo(1));
        Assert.That(later.Paid, Is.False);
        Assert.That(later.Redemption, Is.Not.EqualTo(Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => store.CompleteRedeem(owner, purchase.Id));
        store.MarkPaid(owner, purchase.Id);
        store.CompleteRedeem(owner, purchase.Id);
        Assert.That(store.Slot(owner, purchase.Id).Phase, Is.EqualTo("empty"));
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
