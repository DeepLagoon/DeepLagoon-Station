using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
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
public sealed partial class SectorEconomyTests
{
    private string _path;
    private Guid _user;
    private DbContextOptions<SqliteServerDbContext> _options;
    [SetUp]
    public async Task Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _path = Path.Combine(Path.GetTempPath(), "sector-economy-" + Guid.NewGuid() + ".db");
        _options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite($"Data Source={_path};Default Timeout=30;Pooling=False").Options;
        _user = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            await db.Database.MigrateAsync();
            db.Preference.Add(new Preference { UserId = _user, AdminOOCColor = "#FF0000", LagoonCoins = 100 });
            await db.SaveChangesAsync();
        }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(800000), 0);
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(500000), 1);
    }
    [TearDown]
    public void Cleanup() { SqliteConnection.ClearAllPools(); File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); }

    private async Task<int> ProfileId(Guid user, int slot = 0)
    { await using var db = new SqliteServerDbContext(_options); return (await EconomyOperations.Read(db, user, slot)).Profile; }
    private async Task<EconomyPayment> Pay(EconomyProcurement request)
    { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.Procure(db, request, new(), 3600000); }
    private async Task Fund()
    {
        await using var db = new SqliteServerDbContext(_options);
        var epoch = Guid.NewGuid();
        for (var minute = 0; minute <= 60; minute++) await EconomyOperations.Sample(db, epoch, minute * 60000L, 30, new(), []);
    }
    [Test]
    public void IssuanceRequiresMeasuredActiveTimeAndDoesNotCatchUpAcrossRestart()
    {
        var model = new EconomyFinance(); var epoch = Guid.NewGuid(); var policy = new EconomyPolicy();
        model.Sample(epoch, 0, 30, policy);
        for (var minute = 1; minute <= 60; minute++) model.Sample(epoch, minute * 60000L, 30, policy);
        Assert.That(model.Issued, Is.EqualTo(600000));
        Assert.That(model.Pools["Gas"].General, Is.EqualTo(36000));
        Assert.That(model.Pools["Gas"].Protected, Is.EqualTo(24000));
        var amount = model.Issued;
        model.Sample(Guid.NewGuid(), 86400000, 30, policy);
        model.Sample(model.Epoch, 172800000, 30, policy);
        model.Sample(model.Epoch, 172860000, 0, policy);
        model.Sample(model.Epoch, 172920000, 30, policy);
        Assert.That(model.Issued, Is.EqualTo(amount));
    }
    [Test]
    public void WholeBatchRefusalIsAtomicAndProtectedSalesHaveAnAccountWideDailyLimit()
    {
        var model = new EconomyFinance(); var policy = new EconomyPolicy(ProtectedDaily: 5000);
        model.Sample(Guid.NewGuid(), 0, 30, policy);
        model.Pools["Gas"].Protected = 10000; model.Pools["Materials"].Protected = 10000;
        var user = Guid.NewGuid();
        Assert.That(model.TrySpend(user, new Dictionary<string,long> { ["Gas"] = 3000, ["Materials"] = 3000 }, 6000, 1, policy), Is.False);
        Assert.That(model.Pools["Gas"].Protected, Is.EqualTo(10000));
        Assert.That(model.TrySpend(user, new Dictionary<string,long> { ["Gas"] = 5000 }, 5000, 1, policy), Is.True);
        Assert.That(model.TrySpend(user, new Dictionary<string,long> { ["Materials"] = 1 }, 1, 1, policy), Is.False);
        Assert.That(model.TrySpend(user, new Dictionary<string,long> { ["Materials"] = 1 }, 1, 86400002, policy), Is.True);
    }
    [Test]
    public void DemandIsIndependentBoundedAndRecoversWithoutRestocking()
    {
        var model = new EconomyFinance();
        model.Purchase("vendor:PlasmaFuel", 1000000, 0);
        Assert.That(model.DemandFactor("vendor:PlasmaFuel", 0), Is.EqualTo(1.4).Within(.00001));
        Assert.That(model.DemandFactor("vendor:AmeFuel", 0), Is.EqualTo(1));
        Assert.That(model.DemandFactor("vendor:PlasmaFuel", 1800000), Is.EqualTo(1.2).Within(.00001));
        Assert.That(model.DemandFactor("vendor:PlasmaFuel", 7200000), Is.EqualTo(1.025).Within(.00001));
    }
    [Test]
    public async Task ConcurrentProcurementRetriesPayExactlyOnceAndPrincipalDoesNotSpendIssuance()
    {
        await Fund(); var profile = await ProfileId(_user);
        var request = new EconomyProcurement(Guid.NewGuid(), _user, profile, 9000, 1000,
            new Dictionary<string,long> { ["Materials"] = 8000 }, null);
        var results = await Task.WhenAll(Task.Run(() => Pay(request)), Task.Run(() => Pay(request)));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        Assert.That(results.All(r => r.Accepted), Is.True);
        await using var db = new SqliteServerDbContext(_options);
        var actual = await EconomyOperations.Read(db, _user, 0);
        Assert.That(actual.Bank, Is.EqualTo(809000));
        Assert.That(actual.Finance.Spent, Is.EqualTo(8000));
        Assert.That(await db.BankFinancialOperations.CountAsync(), Is.EqualTo(1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await Pay(request with { Payout = 9001, Principal = 1001 }));
    }
    [Test]
    public async Task BuyEscrowOfflineFulfillmentDeliveryAndCancellationConserveMoneyAndItemState()
    {
        var seller = Guid.NewGuid();
        await using (var setup = new SqliteServerDbContext(_options))
        { setup.Preference.Add(new Preference { UserId = seller, AdminOOCColor = "#FF0000" }); await setup.SaveChangesAsync(); }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(seller), new HumanoidCharacterProfile().WithBankBalance(100000), 0);
        var buyerProfile = await ProfileId(_user); var sellerProfile = await ProfileId(seller);
        var create = new TradeCreate(Guid.NewGuid(), _user, buyerProfile, true, "Flatpack", "Packed cannon", "Undamaged", 2,
            5000, new(100, 0, 1), null, null);
        TradeResult created;
        await using (var db = new SqliteServerDbContext(_options)) created = await EconomyOperations.CreateOffer(db, create, 0);
        var item = new TradeItem("Flatpack", "Customized packed cannon", "FULL STATE WITH CONTAINERS", 100, 0, 1, new());
        var deal = new TradeDeal(Guid.NewGuid(), seller, sellerProfile, created.Offer, item, null);
        TradeResult fulfilled;
        await using (var db = new SqliteServerDbContext(_options)) fulfilled = await EconomyOperations.Deal(db, deal, 1);
        await using (var db = new SqliteServerDbContext(_options)) Assert.That((await EconomyOperations.Deal(db, deal, 1)).Applied, Is.False);
        var lease = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        { var delivery = await EconomyOperations.ClaimDelivery(db, _user, buyerProfile, fulfilled.Delivery!.Value, lease, 2); Assert.That(delivery.Snapshot, Is.EqualTo(item.Snapshot)); }
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CompleteDelivery(db, _user, buyerProfile, fulfilled.Delivery!.Value, lease, null);
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CompleteDelivery(db, _user, buyerProfile, fulfilled.Delivery!.Value, lease, null);
        var cancel = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CancelOffer(db, _user, buyerProfile, created.Offer, cancel, 3);
        await using (var db = new SqliteServerDbContext(_options))
        {
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(795000));
            Assert.That((await EconomyOperations.Read(db, seller, 0)).Bank, Is.EqualTo(105000));
            Assert.That((await EconomyOperations.Board(db, _user, buyerProfile)).Deliveries, Is.Empty);
        }
    }
    [Test]
    public async Task ExpiredBuyOrderReturnsItsReserveAndAnOrphanCannotPayAReplacementCharacter()
    {
        var profile = await ProfileId(_user);
        var create = new TradeCreate(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 2, 5000, new(), null, null);
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CreateOffer(db, create, 0);
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.ExpireOffers(db, 604800001);
        await using (var db = new SqliteServerDbContext(_options)) Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(800000));
        var orphan = create with { Operation = Guid.NewGuid() };
        await using (var db = new SqliteServerDbContext(_options)) await EconomyOperations.CreateOffer(db, orphan, 0);
        await using (var db = new SqliteServerDbContext(_options))
        { db.Profile.Remove(await db.Profile.SingleAsync(p => p.Id == profile)); await db.SaveChangesAsync(); }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(75000), 0);
        await using (var db = new SqliteServerDbContext(_options))
        {
            // A lost confirmation still resolves its original receipt after deletion.
            Assert.That((await EconomyOperations.CreateOffer(db, orphan, 1)).Applied, Is.False);
            db.ChangeTracker.Clear();
            await EconomyOperations.ExpireOffers(db, 604800001);
            Assert.That((await EconomyOperations.Read(db, _user, 0)).Bank, Is.EqualTo(75000));
            var closed = await db.LagoonEconomyRecords.SingleAsync(r => r.Id == "economy:closed-offer:" + orphan.Operation.ToString("N"));
            var retained = System.Text.Json.JsonSerializer.Deserialize<TradeOffer>(closed.Data);
            Assert.That(retained.Orphaned, Is.True);
            Assert.That(retained.Escrow, Is.EqualTo(10000));
        }
    }
}
