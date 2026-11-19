using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._DeepLagoon.CrashRecovery;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;

namespace Content.Tests.Server;

[TestFixture]
public sealed class CrashRecoveryTests
{
    private string _path = "";
    private DbContextOptions<SqliteServerDbContext> _options = default!;
    private Guid _user;
    private int _profile;
    private readonly DateTime _now = new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
    private SqliteServerDbContext Db() => new(_options);

    [SetUp]
    public async Task Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _path = Path.Combine(Path.GetTempPath(), "crash-recovery-" + Guid.NewGuid() + ".db");
        _options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options;
        _user = Guid.NewGuid();
        await using (var db = Db())
        {
            await db.Database.MigrateAsync();
            db.Preference.Add(new Preference { UserId = _user, AdminOOCColor = "#FF0000" });
            await db.SaveChangesAsync();
        }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(1000), 0);
        await using var read = Db();
        _profile = (await read.Profile.SingleAsync()).Id;
    }

    [TearDown]
    public void Cleanup() { SqliteConnection.ClearAllPools(); File.Delete(_path); }

    private RecoveryAsset Asset(Guid? id = null, RecoveryLocation location = RecoveryLocation.Carried, long revision = 1)
        => new(id ?? Guid.NewGuid(), revision, _user, _profile, 1, "ResearchDisk", location, null, [], "uid: 1\ncomponents: []\n");

    [Test]
    public async Task CrashSurvivesContextRestartButNormalRoundNeverRecovers()
    {
        var clean = Guid.NewGuid(); var crashed = Guid.NewGuid();
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", clean, _now);
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", clean, [Asset()], [], _now);
        await using (var db = Db()) await CrashRecoveryOperations.Close(db, "test", clean, _now);
        await using (var db = Db()) Assert.That((await CrashRecoveryOperations.Boot(db, "test", crashed, _now)).Incidents, Is.Empty);
        var item = Asset();
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", crashed, [item], [], _now);
        await using (var db = Db())
        {
            var boot = await CrashRecoveryOperations.Boot(db, "test", Guid.NewGuid(), _now.AddMinutes(1));
            Assert.That(boot.Incidents.Single().Assets.Single().Identity, Is.EqualTo(item.Identity));
        }
    }

    [Test]
    public async Task LeaseRejectsSecondLiveWriterAndScopeIsolatesDevFromMain()
    {
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "main", Guid.NewGuid(), _now);
        await using (var db = Db()) Assert.ThrowsAsync<InvalidOperationException>(() => CrashRecoveryOperations.Boot(db, "main", Guid.NewGuid(), _now.AddSeconds(5)));
        await using (var db = Db()) Assert.That((await CrashRecoveryOperations.Boot(db, "dev", Guid.NewGuid(), _now)).Incidents, Is.Empty);
    }

    [Test]
    public async Task StaleSnapshotCannotResurrectDepositedCash()
    {
        var run = Guid.NewGuid(); var cash = Asset();
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", run, _now);
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", run, [cash], [], _now);
        await using (var db = Db())
            Assert.That(await AccountCurrencyOperations.DepositBank(db, _user, 0, 1000, 100,
                recovery: new RecoveryRemoval("test", run, [cash.Identity])), Is.EqualTo(1100));
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", run, [cash with { Revision = 999 }], [], _now);
        await using (var db = Db())
        {
            await CrashRecoveryOperations.Boot(db, "test", Guid.NewGuid(), _now.AddMinutes(1));
            var prior = await CrashRecoveryOperations.ReadIncident(db, "test", run);
            Assert.That(prior.Assets.Single().Location, Is.EqualTo(RecoveryLocation.Destroyed));
        }
    }

    [Test]
    public async Task PaidButUndeliveredPurchaseRefundsExactlyOnce()
    {
        var incident = Guid.NewGuid(); var current = Guid.NewGuid(); var product = Guid.NewGuid();
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", incident, _now);
        await using (var db = Db()) await AccountCurrencyOperations.WithdrawBank(db, _user, 0, 1000, 137,
            recovery: new RecoveryPayment("test", incident, [product], 137, _user, _profile));
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", current, _now.AddMinutes(1));
        for (var i = 0; i < 2; i++)
        {
            await using var db = Db();
            var result = await CrashRecoveryOperations.Claim(db, "test", incident, current, _user, _profile, [], [product]);
            Assert.That(result.Balance, Is.EqualTo(1000));
        }
        await using var read = Db();
        Assert.That(await read.BankFinancialOperations.CountAsync(t => t.Kind == "bank_crash_refund"), Is.EqualTo(1));
    }

    [TestCase(RecoveryLocation.Carried)]
    [TestCase(RecoveryLocation.Persistent)]
    [TestCase(RecoveryLocation.Destroyed)]
    public async Task RestoredPersistedOrLegitimatelyLostPurchaseDoesNotAlsoRefund(RecoveryLocation location)
    {
        var incident = Guid.NewGuid(); var current = Guid.NewGuid(); var product = Asset(location: location);
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", incident, _now);
        await using (var db = Db()) await AccountCurrencyOperations.WithdrawBank(db, _user, 0, 1000, 137,
            recovery: new RecoveryPayment("test", incident, [product.Identity], 137, _user, _profile));
        // Ownership after a gift is irrelevant to whether the buyer can claim the money too.
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", incident, [product with { User = Guid.NewGuid() }], [], _now);
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", current, _now.AddMinutes(1));
        await using var claim = Db();
        Assert.ThrowsAsync<InvalidOperationException>(() => CrashRecoveryOperations.Claim(claim, "test", incident, current, _user, _profile, [], [product.Identity]));
    }

    [Test]
    public async Task BootRetainsIssuedPurchaseTombstoneWhileOtherItemsRemainOutstanding()
    {
        var incident = Guid.NewGuid(); var current = Guid.NewGuid(); var product = Asset();
        var incomplete = Asset() with { Data = "" };
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", incident, _now);
        await using (var db = Db()) await AccountCurrencyOperations.WithdrawBank(db, _user, 0, 1000, 137,
            recovery: new RecoveryPayment("test", incident, [product.Identity], 137, _user, _profile));
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", incident, [product, incomplete], [], _now);
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", current, _now.AddMinutes(1));
        await using (var db = Db()) await CrashRecoveryOperations.Claim(db, "test", incident, current, _user, _profile, [product.Identity], []);
        await using var restart = Db();
        var boot = await CrashRecoveryOperations.Boot(restart, "test", Guid.NewGuid(), _now.AddMinutes(2));
        var original = boot.Incidents.Single(i => i.Run.Identity == incident);
        Assert.That(original.Assets.Single(a => a.Identity == product.Identity).Location, Is.EqualTo(RecoveryLocation.Issued));
        Assert.That(CrashRecoveryOperations.Refundable(original.Purchases.Single(), original.Assets), Is.False);
        Assert.That(await restart.BankFinancialOperations.CountAsync(t => t.Kind == "bank_crash_refund"), Is.Zero);
    }

    [Test]
    public async Task SecondCrashBetweenClaimAndSpawnPreservesItemAndExactResearchNominal()
    {
        var incident = Guid.NewGuid(); var current = Guid.NewGuid(); var item = Asset();
        var research = Asset() with { YamlId = 0, Kind = "research", Amount = 47382, Data = "" };
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", incident, _now);
        await using (var db = Db()) await CrashRecoveryOperations.Write(db, "test", incident, [item, research], [], _now);
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", current, _now.AddMinutes(1));
        for (var retry = 0; retry < 2; retry++)
        {
            await using var db = Db();
            var claim = await CrashRecoveryOperations.Claim(db, "test", incident, current, _user, _profile, [item.Identity, research.Identity], []);
            Assert.That(claim.Assets, Has.Length.EqualTo(2), "An ambiguous successful claim must be retryable.");
        }
        await using var restart = Db();
        var boot = await CrashRecoveryOperations.Boot(restart, "test", Guid.NewGuid(), _now.AddMinutes(2));
        var outstanding = boot.Incidents.SelectMany(i => i.Assets).ToArray();
        Assert.That(outstanding.Select(a => a.Identity), Is.EquivalentTo(new[] { item.Identity, research.Identity }));
        Assert.That(outstanding.Single(a => a.Kind == "research").Amount, Is.EqualTo(47382));
    }

    [Test]
    public void SeventyPlayersContainerSpamRemainsBoundedAndFair()
    {
        var queue = new RecoveryWorkQueue<int>();
        var clock = Stopwatch.StartNew();
        for (var repetition = 0; repetition < 10000; repetition++)
            for (var player = 0; player < 70; player++) Assert.That(queue.Enqueue(player, 70), Is.True);
        Assert.That(queue.Count, Is.EqualTo(70));
        Assert.That(queue.Enqueue(70, 70), Is.False);
        for (var player = 0; player < 70; player++)
        {
            Assert.That(queue.TryDequeue(out var next), Is.True);
            Assert.That(next, Is.EqualTo(player));
        }
        TestContext.WriteLine($"700000 spam events, maximum queue=70, elapsed_ms={clock.Elapsed.TotalMilliseconds:F3}");
    }

    [Test]
    public void BackpackTransferMovesItsContentsWithoutDependingOnChildSnapshotOrder()
    {
        var owner = Guid.NewGuid();
        var bag = Asset() with { User = owner, Profile = 123 };
        var child = Asset() with { Parent = bag.Identity, YamlId = 2 };
        var normalized = CrashRecoveryOperations.Normalize([bag, child]);
        Assert.That(normalized[1].User, Is.EqualTo(owner));
        Assert.That(normalized[1].Profile, Is.EqualTo(123));
        normalized = CrashRecoveryOperations.Normalize([bag with { Location = RecoveryLocation.Loose }, child]);
        Assert.That(normalized[1].Location, Is.EqualTo(RecoveryLocation.Loose));
    }

    [Test]
    public async Task InvalidPurchaseAttributionRollsBackBothDebitAndReceipt()
    {
        var incident = Guid.NewGuid();
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", incident, _now);
        await using (var db = Db())
            Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.WithdrawBank(db, _user, 0, 1000, 100,
                recovery: new RecoveryPayment("test", incident, [Guid.NewGuid()], 100, _user, _profile + 1)));
        await using var read = Db();
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(1000));
        Assert.That(await read.BankFinancialOperations.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ClosedRoundRejectsDelayedWriterBatch()
    {
        var run = Guid.NewGuid();
        await using (var db = Db()) await CrashRecoveryOperations.Boot(db, "test", run, _now);
        await using (var db = Db()) await CrashRecoveryOperations.Close(db, "test", run, _now);
        await using var write = Db();
        Assert.ThrowsAsync<InvalidOperationException>(() => CrashRecoveryOperations.Write(write, "test", run, [Asset()], [], _now));
    }
}
