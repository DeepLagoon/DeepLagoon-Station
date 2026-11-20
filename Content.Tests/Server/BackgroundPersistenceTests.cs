using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.Apartments;
using Content.Server._DeepLagoon.Currency;
using Content.Server._DeepLagoon.Persistence;
using Content.Shared._DeepLagoon.Apartments;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class BackgroundPersistenceTests
{
    [OneTimeSetUp]
    public void Sqlite()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }

    [Test]
    public async Task QueueRunsOffCallerInOrderAndFailureDoesNotDropFollowingWrites()
    {
        using var queue = new SerialPersistenceQueue();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        var first = queue.Enqueue(() => { entered.Set(); release.Wait(); return Environment.CurrentManagedThreadId; });
        Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True);
        var failed = queue.Enqueue<int>(() => throw new IOException("injected write failure"));
        var last = queue.Enqueue(() => 42);
        Assert.That(last.IsCompleted, Is.False, "A receipt must wait for preceding writes.");
        release.Set();
        Assert.That(await first, Is.Not.EqualTo(caller));
        Assert.ThrowsAsync<IOException>(async () => await failed);
        Assert.That(await last, Is.EqualTo(42));
        await queue.FlushAsync();
    }

    [Test]
    public async Task LockedApartmentWriterDoesNotBlockCallerOrWalReadersAndCapturesOwnedSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "dl-background-" + Guid.NewGuid().ToString("N") + ".db");
        var owner = Guid.NewGuid();
        try
        {
            using (var store = new ApartmentLayoutStore(path))
            {
                await store.SaveSnapshotAsync(owner, "home", new() { Grid = "previous" }, new() { Grid = "previous" });
                using var blocker = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                blocker.Open();
                using var transaction = blocker.BeginTransaction();
                var snapshot = new ApartmentSnapshot { Grid = "captured", Layout = new() { Template = "home", Revision = 7 },
                    MapFurniture = new() { new() { Baseline = new() { Id = "chair", X = 3 } } } };
                var receipt = store.SaveSnapshotAsync(owner, "home", snapshot, snapshot);
                snapshot.Grid = "mutated"; snapshot.Layout.Revision = 99; snapshot.MapFurniture[0].Baseline.X = 99;
                Assert.That(receipt.IsCompleted, Is.False);
                Assert.That(store.LoadSnapshot(owner, "home")!.Grid, Is.EqualTo("previous"));
                transaction.Rollback();
                await receipt;
            }
            using var reopened = new ApartmentLayoutStore(path);
            var saved = reopened.LoadSnapshot(owner, "home")!;
            Assert.That(saved.Grid, Is.EqualTo("captured"));
            Assert.That(saved.Layout.Revision, Is.EqualTo(7));
            Assert.That(saved.MapFurniture.Single().Baseline.X, Is.EqualTo(3));
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Test]
    public async Task OutboxBatchIsDurableBeforeDeliveryAndReplayDoesNotDuplicateKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "dl-outbox-async-" + Guid.NewGuid().ToString("N") + ".db");
        var rewards = Enumerable.Range(0, 100).Select(i => new PendingLagoonCoinReward(Guid.NewGuid(), "stable-" + i, i, "test")).ToArray();
        try
        {
            using (var store = new LagoonCoinOutbox(path))
            {
                Assert.That(await store.StoreBatchAsync(rewards), Has.All.True);
                Assert.That(await store.StoreBatchAsync(rewards), Has.All.False);
            }
            using (var reopened = new LagoonCoinOutbox(path))
            {
                Assert.That(reopened.Load().ToArray(), Is.EqualTo(rewards));
                await reopened.CompleteAsync(rewards[0]);
                await reopened.FlushAsync();
            }
            using var last = new LagoonCoinOutbox(path);
            Assert.That(last.Load().Count(), Is.EqualTo(99));
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
}
