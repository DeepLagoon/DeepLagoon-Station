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
public sealed class ParkingBankTests
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
        _path = Path.Combine(Path.GetTempPath(), "parking-bank-" + Guid.NewGuid() + ".db");
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
    }
    [TearDown]
    public void Cleanup() { SqliteConnection.ClearAllPools(); File.Delete(_path); }
    [Test]
    public async Task ConcurrentRetriesChargeOnceAndReceiptSurvivesContextRestart()
    {
        var key = Guid.NewGuid();
        async Task<int> Debit()
        {
            await using var db = new SqliteServerDbContext(_options);
            return await ParkingBankOperations.Debit(db, _user, 0, 204333, key);
        }
        var balances = await Task.WhenAll(Task.Run(Debit), Task.Run(Debit));
        Assert.That(balances, Is.All.EqualTo(595667));
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.DepositBank(db, _user, 0, 595667, 7);
        Assert.That(await Debit(), Is.EqualTo(595674), "Retry must return the current balance, not an obsolete balance from its receipt");
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(100));
        Assert.That(await read.LagoonEconomyRecords.CountAsync(p => p.Id.StartsWith("parking-credit:")), Is.EqualTo(1));
    }
    [Test]
    public async Task ReceiptRejectsChangedPaymentAndInsufficientBalanceLeavesNoReceipt()
    {
        var key = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options)) await ParkingBankOperations.Debit(db, _user, 0, 700000, key);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => ParkingBankOperations.Debit(db, _user, 0, 1, key));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => ParkingBankOperations.Debit(db, _user, 0, 200000, Guid.NewGuid()));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(100000));
        Assert.That(await read.LagoonEconomyRecords.CountAsync(p => p.Id.StartsWith("parking-credit:")), Is.EqualTo(1));
    }
}
