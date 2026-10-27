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
                Assert.That(reopened.Owned(Guid.NewGuid(), "host-granted"), Is.False);
                Assert.That(loaded.Revision, Is.EqualTo(2));
                Assert.That(loaded.TemplateVersion, Is.EqualTo(3));
                Assert.That(loaded.Removed, Is.EqualTo(new[] { "base_table" }));
            });
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
}
