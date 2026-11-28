using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class ParkingWriterTests
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task WorkerCommitsSnapshotAndPayoutTogetherAndHonoursSlotGuard(bool memory)
    {
        var path = memory ? ":memory:" : Path.Combine(Path.GetTempPath(), "parking-worker-" + Guid.NewGuid() + ".db");
        try
        {
            using var store = new ParkingStore(path);
            var owner = Guid.NewGuid();
            var slot = store.BeginPurchase(owner, Guid.NewGuid(), 1);
            store.GrantPurchase(owner, slot);
            var ship = new StoredShuttle(Guid.NewGuid(), "Test", "Captain", 1, 100, "grid", ParkingStore.Hash("grid"));
            var payout = new ParkingPayout(Guid.NewGuid(), owner, 0, 250, ProfileId: 7,
                Costs: new Dictionary<string, long> { ["Materials"] = 200, ["Industry"] = 50 }, Paid: 3);
            await store.SaveAsync(owner, slot.Id, ship, payout);
            Assert.That(store.Slot(owner, slot.Id).Phase, Is.EqualTo("saving"));
            Assert.That(store.Slot(owner, slot.Id).Ship, Is.EqualTo(ship));
            var receipt = store.PendingPayouts().Single();
            Assert.That(receipt.Id, Is.EqualTo(payout.Id));
            Assert.That(receipt.ProfileId, Is.EqualTo(7));
            Assert.That(receipt.Paid, Is.EqualTo(3));
            Assert.That(receipt.Costs, Is.EquivalentTo(payout.Costs));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(owner, slot.Id, ship, payout));
            Assert.That(store.PendingPayouts(), Has.Count.EqualTo(1));
            await store.FinishSaveAsync(owner, slot.Id, TimeSpan.FromMinutes(5));
            Assert.That(store.Slot(owner, slot.Id).Phase, Is.EqualTo("stored"));
            Assert.That(store.Slot(owner, slot.Id).Ship!.FreeReturnAt, Is.EqualTo(TimeSpan.FromMinutes(5)));
        }
        finally { if (!memory) DeleteDatabase(path); }
    }

    [Test]
    public async Task WorkerRollsBackSnapshotWhenReceiptCannotBeWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), "parking-worker-failure-" + Guid.NewGuid() + ".db");
        try
        {
            using var store = new ParkingStore(path);
            var owner = Guid.NewGuid();
            var slot = store.BeginPurchase(owner, Guid.NewGuid(), 1);
            store.GrantPurchase(owner, slot);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var fault = db.CreateCommand();
            fault.CommandText = "CREATE TRIGGER reject_payout BEFORE INSERT ON parking_payouts BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            fault.ExecuteNonQuery();
            var ship = new StoredShuttle(Guid.NewGuid(), "Test", "Captain", 1, 100, "grid", ParkingStore.Hash("grid"));
            Assert.ThrowsAsync<SqliteException>(async () => await store.SaveAsync(owner, slot.Id, ship, new(Guid.NewGuid(), owner, 0, 1)));
            Assert.That(store.Slot(owner, slot.Id).Phase, Is.EqualTo("empty"));
            Assert.That(store.Slot(owner, slot.Id).Ship, Is.Null);
            Assert.That(store.PendingPayouts(), Is.Empty);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(Guid.NewGuid(), slot.Id, ship));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(owner, slot.Id, ship with { Category = 2 }));
            Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(owner, slot.Id, ship with { Checksum = "bad" }));
        }
        finally { DeleteDatabase(path); }
    }

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }
}
