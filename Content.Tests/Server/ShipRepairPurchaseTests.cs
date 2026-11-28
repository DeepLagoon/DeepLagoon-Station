using System;
using System.IO;
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
public sealed class ShipRepairPurchaseTests
{
    private string _path;
    private Guid _user;
    private int _profile;
    private DbContextOptions<SqliteServerDbContext> _options;
    [SetUp]
    public async Task Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _path = Path.Combine(Path.GetTempPath(), "ship-repair-" + Guid.NewGuid() + ".db");
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
        await using var read = new SqliteServerDbContext(_options);
        _profile = (await read.Profile.SingleAsync()).Id;
    }
    [TearDown] public void Cleanup() { SqliteConnection.ClearAllPools(); File.Delete(_path); }
    private ShipRepairPurchase Order(bool coins = false) => new(Guid.NewGuid(), _user, _profile, 0, Guid.NewGuid(), Guid.NewGuid(),
        coins ? 0 : 12000, coins ? 12 : 0, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
    private async Task<ShipRepairPurchase> Purchase(ShipRepairPurchase order) { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.PurchaseShipRepair(db, order); }
    private async Task<ShipRepairPurchase> Finish(ShipRepairPurchase order, bool refund) { await using var db = new SqliteServerDbContext(_options); return await EconomyOperations.FinishShipRepair(db, order.Id, order.User, refund); }

    [TestCase(false)] [TestCase(true)]
    public async Task ConcurrentPaymentRetryAndRefundAreIdempotent(bool coins)
    {
        var order = Order(coins);
        var purchases = await Task.WhenAll(Task.Run(() => Purchase(order)), Task.Run(() => Purchase(order)));
        Assert.That(purchases.Select(p => p.State), Is.All.EqualTo("paid"));
        await using (var read = new SqliteServerDbContext(_options))
        {
            Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(coins ? 800000 : 788000));
            Assert.That((await read.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(coins ? 88 : 100));
            Assert.That(await read.LagoonEconomyRecords.CountAsync(), Is.EqualTo(1));
        }
        await Task.WhenAll(Task.Run(() => Finish(order, true)), Task.Run(() => Finish(order, true)));
        Assert.That((await Purchase(order)).State, Is.EqualTo("refunded"));
        Assert.That((await Finish(order, false)).State, Is.EqualTo("refunded"));
        await using var final = new SqliteServerDbContext(_options);
        Assert.That((await final.Profile.SingleAsync()).BankBalance, Is.EqualTo(800000));
        Assert.That((await final.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(100));
        Assert.That(await final.LagoonCoinOperations.CountAsync(), Is.EqualTo(coins ? 2 : 0));
        Assert.That(await final.BankFinancialOperations.CountAsync(), Is.EqualTo(coins ? 0 : 2));
    }

    [TestCase(false)] [TestCase(true)]
    public async Task DeliveredRepairCannotBeRefundedOrChargedAgain(bool coins)
    {
        var order = Order(coins); await Purchase(order);
        Assert.That((await Finish(order, false)).State, Is.EqualTo("completed"));
        Assert.That((await Finish(order, true)).State, Is.EqualTo("completed"));
        Assert.That((await Purchase(order)).State, Is.EqualTo("completed"));
        await using var db = new SqliteServerDbContext(_options);
        Assert.That((await db.Profile.SingleAsync()).BankBalance, Is.EqualTo(coins ? 800000 : 788000));
        Assert.That((await db.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(coins ? 88 : 100));
    }

    [TestCase(false)] [TestCase(true)]
    public async Task InsufficientFundsNeverCreateAnOrder(bool coins)
    {
        var order = Order(coins) with { Credits = coins ? 0 : 900000, Coins = coins ? 101 : 0 };
        Assert.CatchAsync<InvalidOperationException>(() => Purchase(order));
        await using var db = new SqliteServerDbContext(_options);
        Assert.That(await db.LagoonEconomyRecords.CountAsync(), Is.Zero);
        Assert.That(await db.LagoonCoinOperations.CountAsync(), Is.Zero);
        Assert.That(await db.BankFinancialOperations.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ChangedPriceShipOrCharacterCannotReusePaymentReceipt()
    {
        var order = Order(); await Purchase(order);
        Assert.ThrowsAsync<InvalidOperationException>(() => Purchase(order with { Credits = 1 }));
        Assert.ThrowsAsync<InvalidOperationException>(() => Purchase(order with { Ship = Guid.NewGuid() }));
        Assert.ThrowsAsync<InvalidOperationException>(() => Purchase(order with { Slot = 1 }));
        await using var db = new SqliteServerDbContext(_options);
        Assert.That((await db.Profile.SingleAsync()).BankBalance, Is.EqualTo(788000));
    }

    [Test]
    public async Task InterruptedExpiredRepairRemainsRefundableAfterDatabaseReopen()
    {
        var order = Order(true) with { Expires = 1000 }; await Purchase(order);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That((await EconomyOperations.ExpiredShipRepairs(db, 1000)).Select(p => p.Id), Is.EqualTo(new[] { order.Id }));
        await Finish(order, true);
        await using var final = new SqliteServerDbContext(_options);
        Assert.That(await EconomyOperations.ExpiredShipRepairs(final, 1000), Is.Empty);
    }
}
