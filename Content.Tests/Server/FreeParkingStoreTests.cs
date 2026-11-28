using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._DeepLagoon.ShuttleParking;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture]
public sealed class FreeParkingStoreTests
{
    private string _directory = "";
    private string Database => Path.Combine(_directory, "parking.db");
    private static FreeParkingSnapshot Snapshot() => new(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(),
        "Shuttle", "Captain", 1, 100, "", 42, "Frontier", "TestVessel");

    [SetUp]
    public void SetUp()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _directory = Path.Combine(Path.GetTempPath(), "free-parking-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.That(Path.GetDirectoryName(Path.GetFullPath(_directory)), Is.EqualTo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))));
        Assert.That(Path.GetFileName(_directory), Does.StartWith("free-parking-test-"));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task CrashAfterClaimRestoresLastGoodCopyAndReservesAssets()
    {
        var snapshot = Snapshot(); var asset = Guid.NewGuid();
        using (var store = new ParkingStore(Database))
        {
            await store.BeginFreeParkingAsync(snapshot);
            Assert.That(await store.CommitFreeParkingAsync(snapshot, "grid", [asset]), Is.True);
            Assert.That(await store.ClaimFreeParkingAsync(snapshot), Is.True);
            Assert.That(store.ContainsRecoveryIdentity(asset), Is.True);
            // Simulated process loss: intentionally do not complete/abort the claim.
        }
        using (var recovered = new ParkingStore(Database))
        {
            var stored = (await recovered.FreeParkingIndexAsync()).Single();
            Assert.That(stored.Profile, Is.EqualTo(42)); Assert.That(stored.OriginFaction, Is.EqualTo("Frontier"));
            Assert.That(stored.Vessel, Is.EqualTo("TestVessel"));
            Assert.That((await recovered.ReadFreeParkingAsync(stored)).Grid, Is.EqualTo("grid"));
            Assert.That(recovered.ContainsRecoveryIdentity(asset), Is.True);
            Assert.That(await recovered.ClaimFreeParkingAsync(stored), Is.True);
            await recovered.CompleteFreeParkingClaim(stored);
            Assert.That(await recovered.CommitFreeParkingAsync(stored, "grid", [asset]), Is.False, "A late encoder cannot resurrect a completed recall.");
        }
        using (var completed = new ParkingStore(Database))
        {
            Assert.That(await completed.FreeParkingIndexAsync(), Is.Empty);
            Assert.That(completed.ContainsRecoveryIdentity(asset), Is.False);
        }
    }

    [Test]
    public async Task CancelledCaptureCannotResurrectAndRepeatedCommitPreservesFile()
    {
        using var store = new ParkingStore(Database); var snapshot = Snapshot();
        await store.BeginFreeParkingAsync(snapshot);
        await store.SuspendFreeParkingAsync(snapshot.Identity, snapshot.Revision);
        Assert.That(await store.CommitFreeParkingAsync(snapshot, "cancelled", []), Is.False);
        await store.BeginFreeParkingAsync(snapshot);
        Assert.That(await store.CommitFreeParkingAsync(snapshot, "complete", []), Is.True);
        Assert.That(await store.CommitFreeParkingAsync(snapshot, "complete", []), Is.True);
        Assert.That((await store.ReadFreeParkingAsync(snapshot)).Grid, Is.EqualTo("complete"));
        Assert.That(await store.CommitFreeParkingAsync(snapshot, "different", []), Is.False);
        Assert.That((await store.ReadFreeParkingAsync(snapshot)).Grid, Is.EqualTo("complete"));
    }

    [Test]
    public async Task IncompleteResaveKeepsLastGoodCopyAcrossRestart()
    {
        var original = Snapshot(); var resave = original with { Revision = Guid.NewGuid() }; var asset = Guid.NewGuid();
        using (var store = new ParkingStore(Database))
        {
            await store.BeginFreeParkingAsync(original); await store.CommitFreeParkingAsync(original, "original", [asset]);
            await store.BeginFreeParkingAsync(resave);
            var previous = await store.SuspendFreeParkingAsync(resave.Identity, resave.Revision);
            Assert.That(previous!.Revision, Is.EqualTo(original.Revision));
            Assert.That(await store.CommitFreeParkingAsync(resave, "stale", []), Is.False);
        }
        using var recovered = new ParkingStore(Database);
        var stored = (await recovered.FreeParkingIndexAsync()).Single();
        Assert.That(stored.Revision, Is.EqualTo(original.Revision));
        Assert.That((await recovered.ReadFreeParkingAsync(stored)).Grid, Is.EqualTo("original"));
        Assert.That(recovered.ContainsRecoveryIdentity(asset), Is.True);
    }

    [Test]
    public async Task OwnershipProfileFactionAndRevisionAreCheckedBeforeReadOrClaim()
    {
        using var store = new ParkingStore(Database); var snapshot = Snapshot();
        await store.BeginFreeParkingAsync(snapshot); await store.CommitFreeParkingAsync(snapshot, "grid", []);
        foreach (var wrong in new[] { snapshot with { Owner = Guid.NewGuid() }, snapshot with { Profile = 43 },
                     snapshot with { OriginFaction = "Military" }, snapshot with { Revision = Guid.NewGuid() } })
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ReadFreeParkingAsync(wrong));
            Assert.That(await store.ClaimFreeParkingAsync(wrong), Is.False);
        }
        Assert.That(await store.ClaimFreeParkingAsync(snapshot), Is.True);
        await Task.WhenAll(store.AbortFreeParkingClaimAsync(snapshot), store.AbortFreeParkingClaimAsync(snapshot));
        Assert.That(await store.ClaimFreeParkingAsync(snapshot), Is.True, "Duplicate abort must not double-release the writer gate.");
        await store.AbortFreeParkingClaimAsync(snapshot);
    }

    [Test]
    public async Task PaidSaveAtomicallySupersedesFreeRecovery()
    {
        var snapshot = Snapshot(); var asset = Guid.NewGuid();
        using (var store = new ParkingStore(Database))
        {
            await store.BeginFreeParkingAsync(snapshot); await store.CommitFreeParkingAsync(snapshot, "raw", [asset]);
            var slot = store.BeginPurchase(snapshot.Owner, Guid.NewGuid(), 1); store.GrantPurchase(snapshot.Owner, slot);
            var paid = new StoredShuttle(snapshot.Identity, "Shuttle", "Captain", 1, 100, "paid", ParkingStore.Hash("paid"));
            await store.SaveAsync(snapshot.Owner, slot.Id, paid);
            Assert.That(await store.FreeParkingIndexAsync(), Is.Empty);
            Assert.That(store.Slot(snapshot.Owner, slot.Id).Ship!.Grid, Is.EqualTo("paid"));
        }
        using var recovered = new ParkingStore(Database);
        Assert.That(await recovered.FreeParkingIndexAsync(), Is.Empty);
    }
}
