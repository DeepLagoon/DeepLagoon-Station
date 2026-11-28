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
public sealed class FreeParkingSaleStoreTests
{
    private string _directory = null!;
    private string _path = null!;
    private const string Yaml = "meta: { format: 7 }\nentities: []\n";

    [SetUp]
    public void SetUp()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _directory = Path.Combine(Path.GetTempPath(), "parking-free-sale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "parking.db");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(_directory);
        Assert.That(Path.GetDirectoryName(target), Is.EqualTo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))));
        Assert.That(Path.GetFileName(target), Does.StartWith("parking-free-sale-"));
        Directory.Delete(target, recursive: true);
    }

    [Test]
    public async Task PendingSaleSurvivesRestartAndKeepsAssetsProtectedUntilAppraised()
    {
        var asset = Guid.NewGuid();
        FreeParkingSnapshot snapshot;
        using (var store = new ParkingStore(_path))
        {
            snapshot = await Save(store, asset);
            await store.MarkFreeParkingSalesAsync([snapshot]);
        }
        using (var store = new ParkingStore(_path))
        {
            Assert.That(await store.FreeParkingIndexAsync(), Is.Empty, "A pending sale cannot also be recalled.");
            Assert.That(await store.PendingFreeParkingSalesAsync(), Is.EqualTo(new[] { snapshot }));
            Assert.That(store.ContainsRecoveryIdentity(asset), Is.True);
            var read = await store.ReadFreeParkingAsync(snapshot);
            Assert.That(read.Grid, Is.EqualTo(Yaml));
            Assert.That(read.Assets, Is.EqualTo(new[] { asset }));
            Assert.That(await store.ClaimFreeParkingAsync(snapshot), Is.False);
        }
    }

    [Test]
    public async Task ChangedRevisionOrProfileCannotDeleteSnapshotOrCreatePayout()
    {
        using var store = new ParkingStore(_path);
        var asset = Guid.NewGuid();
        var snapshot = await Save(store, asset);
        await store.MarkFreeParkingSalesAsync([snapshot]);
        var stale = snapshot with { Revision = Guid.NewGuid() };
        Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CommitFreeParkingSaleAsync(stale, Payout(stale, 4321)));
        var wrongProfile = snapshot with { Profile = snapshot.Profile + 1 };
        Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CommitFreeParkingSaleAsync(wrongProfile, Payout(wrongProfile, 4321)));
        Assert.That(await store.PendingFreeParkingSalesAsync(), Is.EqualTo(new[] { snapshot }));
        Assert.That(store.ContainsRecoveryIdentity(asset), Is.True);
        Assert.That(store.PendingPayouts(), Is.Empty);
    }

    [Test]
    public async Task ReceiptFailureRollsBackSnapshotRemovalAndCanBeRetried()
    {
        using var store = new ParkingStore(_path);
        var asset = Guid.NewGuid();
        var snapshot = await Save(store, asset);
        await store.MarkFreeParkingSalesAsync([snapshot]);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_free_sale BEFORE INSERT ON parking_payouts BEGIN SELECT RAISE(ABORT, 'simulated receipt failure'); END";
        command.ExecuteNonQuery();
        Assert.ThrowsAsync<SqliteException>(async () => await store.CommitFreeParkingSaleAsync(snapshot, Payout(snapshot, 4321)));
        Assert.That(await store.PendingFreeParkingSalesAsync(), Is.EqualTo(new[] { snapshot }));
        Assert.That(store.ContainsRecoveryIdentity(asset), Is.True);
        Assert.That(store.PendingPayouts(), Is.Empty);
        command.CommandText = "DROP TRIGGER reject_free_sale";
        command.ExecuteNonQuery();
        await store.CommitFreeParkingSaleAsync(snapshot, Payout(snapshot, 4321));
        Assert.That(store.PendingPayouts(), Has.Count.EqualTo(1));
        Assert.That(store.ContainsRecoveryIdentity(asset), Is.False);
    }

    [TestCase(4321)]
    [TestCase(0)]
    public async Task CommittedSaleReopensWithOneImmutableReceiptAndNoRecoverableGrid(int amount)
    {
        var asset = Guid.NewGuid();
        FreeParkingSnapshot snapshot;
        using (var store = new ParkingStore(_path))
        {
            snapshot = await Save(store, asset);
            await store.MarkFreeParkingSalesAsync([snapshot]);
            await store.CommitFreeParkingSaleAsync(snapshot, Payout(snapshot, amount));
        }
        using (var store = new ParkingStore(_path))
        {
            Assert.That(await store.FreeParkingIndexAsync(), Is.Empty);
            Assert.That(await store.PendingFreeParkingSalesAsync(), Is.Empty);
            Assert.That(store.ContainsRecoveryIdentity(asset), Is.False);
            var receipt = store.PendingPayouts().Single();
            Assert.That(receipt.Id, Is.EqualTo(snapshot.Identity));
            Assert.That(receipt.Owner, Is.EqualTo(snapshot.Owner));
            Assert.That(receipt.Character, Is.EqualTo(snapshot.Character));
            Assert.That(receipt.ProfileId, Is.EqualTo(snapshot.Profile));
            Assert.That(receipt.Amount, Is.EqualTo(amount));
            Assert.That(receipt.Costs!["Industry"], Is.EqualTo(amount));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CommitFreeParkingSaleAsync(snapshot, Payout(snapshot, amount)));
            Assert.That(store.PendingPayouts(), Has.Count.EqualTo(1));
            store.FinishPayout(receipt.Id);
        }
        using (var store = new ParkingStore(_path))
        {
            Assert.That(store.PendingPayouts(), Is.Empty);
            Assert.That(await store.PendingFreeParkingSalesAsync(), Is.Empty, "Finishing payment must not resurrect a disposed snapshot.");
        }
    }

    private static async Task<FreeParkingSnapshot> Save(ParkingStore store, Guid asset)
    {
        var snapshot = new FreeParkingSnapshot(Guid.NewGuid(), Guid.NewGuid(), 3, Guid.NewGuid(),
            "Recovered vessel", "Original character", 1, 10, "", Profile: 12345, Vessel: "TestVessel");
        await store.BeginFreeParkingAsync(snapshot);
        Assert.That(await store.CommitFreeParkingAsync(snapshot, Yaml, [asset]), Is.True);
        return (await store.FreeParkingIndexAsync()).Single();
    }

    private static ParkingPayout Payout(FreeParkingSnapshot snapshot, int amount) => new(snapshot.Identity,
        snapshot.Owner, snapshot.Character, amount, snapshot.Profile, new Dictionary<string, long> { ["Industry"] = amount });
}
