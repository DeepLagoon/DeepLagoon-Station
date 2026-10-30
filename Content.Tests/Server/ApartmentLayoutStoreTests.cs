using System;
using System.IO;
using System.Linq;
using Content.Server._DeepLagoon.Apartments;
using Content.Shared._DeepLagoon.Apartments;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ApartmentLayoutStoreTests
{
    [Test]
    public void ItemDeliveryCommitsWithSnapshotAndOrdersSurviveReopenWithoutDuplicateDelivery()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"dl-apartment-items-{Guid.NewGuid():N}.db");
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        try
        {
            using (var store = new ApartmentLayoutStore(path))
            {
                var order = store.BeginItemOrder(owner, id, "canvas", 100);
                Assert.That(store.BeginItemOrder(owner, id, "canvas", 100), Is.EqualTo(order));
                Assert.Throws<InvalidOperationException>(() => store.BeginItemOrder(owner, id, "frame", 35));
                Assert.Throws<InvalidOperationException>(() => store.BeginItemOrder(owner, Guid.NewGuid(), "frame", 35));
                Assert.That(store.PendingItemOrder(Guid.NewGuid()), Is.Null);
                store.SaveSnapshot(owner, "home", new() { Grid = "before" }, new() { Grid = "before" });
                // Updating a snapshot without its matching pending delivery must roll back both.
                Assert.Throws<InvalidOperationException>(() => store.SaveSnapshot(owner, "home",
                    new() { Grid = "bad" }, new() { Grid = "bad" }, Guid.NewGuid()));
                Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("before"));
                Assert.That(store.PendingItemOrder(owner), Is.EqualTo(order));
            }
            using var reopened = new ApartmentLayoutStore(path);
            Assert.That(reopened.PendingItemOrder(owner)!.Id, Is.EqualTo(id));
            reopened.SaveSnapshot(owner, "home", new() { Grid = "delivered canvas" }, new() { Grid = "delivered canvas" }, id);
            Assert.That(reopened.PendingItemOrder(owner), Is.Null);
            Assert.That(reopened.BeginItemOrder(owner, id, "canvas", 100).Delivered, Is.True);
            Assert.Throws<InvalidOperationException>(() => reopened.SaveSnapshot(owner, "home",
                new() { Grid = "duplicate canvas" }, new() { Grid = "duplicate canvas" }, id));
            Assert.That(reopened.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("delivered canvas"));
            var cancelled = Guid.NewGuid();
            reopened.BeginItemOrder(owner, cancelled, "frame", 35);
            reopened.CancelItemOrder(owner, cancelled);
            Assert.That(reopened.PendingItemOrder(owner), Is.Null);
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Test]
    public void PopularityCountsGrantedOwnersOnceAndIgnoresPendingPurchases()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        using var store = new ApartmentLayoutStore(":memory:");
        var alice = Guid.NewGuid(); var bob = Guid.NewGuid();
        store.BeginPurchase(alice, "home", 10);
        Assert.That(store.Popularity(), Is.Empty);
        store.Grant(alice, "home");
        store.Grant(alice, "home");
        store.BeginPurchase(bob, "home", 10); store.Grant(bob, "home");
        store.BeginPurchase(bob, "pending", 20);
        Assert.That(store.Popularity()["home"], Is.EqualTo(2));
        Assert.That(store.Popularity().ContainsKey("pending"), Is.False);
    }

    [Test]
    public void SnapshotsAreIsolatedByOwnerAndTemplateAndFinalizeUnloadedTrashAfterReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dl-apartment-snapshot-{Guid.NewGuid():N}.db");
        var owner = Guid.NewGuid();
        try
        {
#if USE_SYSTEM_SQLITE
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
            SQLitePCL.Batteries_V2.Init();
#endif
            using (var store = new ApartmentLayoutStore(path))
            {
                var full = new ApartmentSnapshot { Grid = "with trash", Layout = new() { Template = "home", OxygenPercent = 35 },
                    MapFurniture = new() { new() { Prototype = "ChairWood", Baseline = new() { Id = "stable", Furniture = "stable", X = 3 }, AllowsOverlap = true } } };
                store.SaveSnapshot(owner, "home", full, new ApartmentSnapshot { Grid = "empty bin", Layout = full.Layout, MapFurniture = full.MapFurniture });
                store.SaveSnapshot(owner, "other", new() { Grid = "other home" }, new() { Grid = "other home" });
                Assert.That(store.LoadSnapshot(Guid.NewGuid(), "home"), Is.Null);
                Assert.That(store.LoadSnapshot(owner, "missing"), Is.Null);
            }
            using var reopened = new ApartmentLayoutStore(path);
            Assert.That(reopened.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("with trash"));
            reopened.FinishRound();
            reopened.FinishRound();
            var saved = reopened.LoadSnapshot(owner, "home")!;
            Assert.Multiple(() =>
            {
                Assert.That(saved.Grid, Is.EqualTo("empty bin"));
                Assert.That(saved.Layout.OxygenPercent, Is.EqualTo(35));
                Assert.That(saved.MapFurniture.Single().Baseline.Id, Is.EqualTo("stable"));
                Assert.That(saved.MapFurniture.Single().Baseline.X, Is.EqualTo(3));
                Assert.That(saved.MapFurniture.Single().AllowsOverlap, Is.True);
                Assert.That(reopened.LoadSnapshot(owner, "other")!.Grid, Is.EqualTo("other home"));
            });
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Test]
    public void SwitchingTemplatesArchivesLayoutsAndPurchaseOrdersSurviveRetries()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        using var store = new ApartmentLayoutStore(":memory:");
        var owner = Guid.NewGuid();
        var legacy = new ApartmentDelta { Template = "rectangle", TemplateVersion = 1, Revision = 1,
            Added = new() { new() { Id = "personal", Furniture = "chair", X = 3, Y = 4 } } };
        Assert.That(store.Save(owner, legacy, 0), Is.True);
        var basic = new ApartmentTemplatePrototype { ID = "basic", Version = 1 };
        var old = new ApartmentTemplatePrototype { ID = "rectangle", Version = 1 };
        var migrated = store.Select(owner, basic);
        Assert.That(migrated.Template, Is.EqualTo("basic")); Assert.That(migrated.Revision, Is.EqualTo(2));
        var restored = store.Select(owner, old);
        Assert.That(restored.Added.Single().Id, Is.EqualTo("personal")); Assert.That(restored.Revision, Is.EqualTo(3));
        var purchase = store.BeginPurchase(owner, "premium", 10);
        Assert.That(store.BeginPurchase(owner, "premium", 100), Is.EqualTo(purchase));
        Assert.That(store.Owned(owner, "premium"), Is.False);
        store.Grant(owner, "premium"); Assert.That(store.Owned(owner, "premium"), Is.True);
        Assert.That(store.BeginPurchase(owner, "premium", 10).Operation, Is.EqualTo(purchase.Operation));
        Assert.That(store.Owned(Guid.NewGuid(), "premium"), Is.False);
    }

    [Test]
    public void ReopeningPreservesDeltaAndStaleRevisionCannotOverwriteIt()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        var path = Path.Combine(Path.GetTempPath(), $"dl-apartment-test-{Guid.NewGuid():N}.db");
        var owner = Guid.NewGuid();
        try
        {
            using (var store = new ApartmentLayoutStore(path))
            {
                var delta = new ApartmentDelta { Template = "studio", TemplateVersion = 3, Revision = 1, Removed = new() { "base_table" } };
                Assert.That(store.Save(owner, delta, 0), Is.True);
                Assert.That(store.Save(owner, new ApartmentDelta { Revision = 1 }, 0), Is.False);
                delta.Revision = 2;
                Assert.That(store.Save(owner, delta, 1), Is.True);
                Assert.That(store.Load(Guid.NewGuid()), Is.Null);
                store.BeginPurchase(owner, "host-granted", 0);
                store.Grant(owner, "host-granted");
                store.Grant(owner, "host-granted");
            }
            using var reopened = new ApartmentLayoutStore(path);
            var loaded = reopened.Load(owner)!;
            Assert.Multiple(() =>
            {
                Assert.That(reopened.Owned(owner, "host-granted"), Is.True);
                Assert.That(reopened.HasLayout(owner, "studio"), Is.True);
                Assert.That(reopened.HasLayout(Guid.NewGuid(), "studio"), Is.False);
                Assert.That(reopened.Owned(Guid.NewGuid(), "host-granted"), Is.False);
                Assert.That(loaded.Revision, Is.EqualTo(2));
                Assert.That(loaded.TemplateVersion, Is.EqualTo(3));
                Assert.That(loaded.Removed, Is.EqualTo(new[] { "base_table" }));
            });
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
}
