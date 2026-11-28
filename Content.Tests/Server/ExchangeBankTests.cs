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
public sealed partial class ExchangeBankTests
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
        _path = Path.Combine(Path.GetTempPath(), "exchange-bank-" + Guid.NewGuid() + ".db");
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

    [Test]
    public async Task DirectMiningSharesIssuanceAndCreditsSeveralFarmsToTheSameWalletAtomically()
    {
        ExchangeResult initial;
        await using (var db = new SqliteServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var requests = Enumerable.Range(0, 40).Select(_ => new ExchangeMiningRequest(Guid.NewGuid(), _user, initial.ProfileId)).ToArray();
        await using (var db = new SqliteServerDbContext(_options))
        {
            var results = await ExchangeOperations.MintToWallets(db, requests, 3600000);
            Assert.That(results.Sum(r => r.Units), Is.EqualTo(30000));
            Assert.That(results.All(r => r.Error == null), Is.True);
        }
        await using (var db = new SqliteServerDbContext(_options))
        {
            var result = await ExchangeOperations.Read(db, _user, 0, 3600000);
            Assert.That(result.Wallet.Values.Sum(), Is.EqualTo(30000));
            Assert.That(result.Bank, Is.EqualTo(800000));
            Assert.That(result.Market.Pools.Values.Sum(p => p.Cash), Is.Zero);
            Assert.That(result.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(30000));
            Assert.That((await ExchangeOperations.Read(db, _user, 1, 3600000)).Wallet, Is.Empty);
            Assert.That(await db.BankFinancialOperations.CountAsync(r => r.Kind != "crypto_mining"), Is.Zero);
            Assert.That((await db.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(100));
        }
        await using var retry = new SqliteServerDbContext(_options);
        Assert.That((await ExchangeOperations.MintToWallets(retry, requests, 7200000)).All(r => !r.Applied), Is.True);
        // A later retry may accrue new allowance, but it cannot spend that allowance on old work again.
        Assert.That((await ExchangeOperations.Read(retry, _user, 0, 7200000)).Wallet.Values.Sum(), Is.EqualTo(30000));
    }

    [Test]
    public async Task ConcurrentDirectMiningRetriesCommitOneReceiptAndOneCoin()
    {
        ExchangeResult initial;
        await using (var db = new SqliteServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var request = new ExchangeMiningRequest(Guid.NewGuid(), _user, initial.ProfileId);
        async Task<ExchangeMiningResult> Mine()
        {
            await using var db = new SqliteServerDbContext(_options);
            return (await ExchangeOperations.MintToWallets(db, [request], 3600000)).Single();
        }
        var results = await Task.WhenAll(Task.Run(Mine), Task.Run(Mine));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        await using var read = new SqliteServerDbContext(_options);
        var wallet = await ExchangeOperations.Read(read, _user, 0, 3600000);
        Assert.That(wallet.Wallet.Values.Sum(), Is.EqualTo(1000));
        Assert.That(wallet.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(1000));
        Assert.That(await read.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith("exchange:mine:")), Is.EqualTo(1));
    }

    [Test]
    public async Task DeletedCharacterDoesNotRedirectPendingMiningOrInheritAnOldReceipt()
    {
        ExchangeResult original;
        await using (var db = new SqliteServerDbContext(_options)) original = await ExchangeOperations.Read(db, _user, 0, 0);
        var paid = new ExchangeMiningRequest(Guid.NewGuid(), _user, original.ProfileId);
        await using (var db = new SqliteServerDbContext(_options)) await ExchangeOperations.MintToWallets(db, [paid], 3600000);
        await using (var db = new SqliteServerDbContext(_options))
        {
            db.Profile.Remove(await db.Profile.SingleAsync(p => p.Id == original.ProfileId));
            await db.SaveChangesAsync();
        }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(75000), 0);
        await using var read = new SqliteServerDbContext(_options);
        var results = await ExchangeOperations.MintToWallets(read,
            [paid, new(Guid.NewGuid(), _user, original.ProfileId)], 3600000);
        Assert.That(results[0].Applied, Is.False);
        Assert.That(results[1].Error, Is.EqualTo("character_missing"));
        Assert.That(results[1].Units, Is.Zero);
        var replacement = await ExchangeOperations.Read(read, _user, 0, 3600000);
        Assert.That(replacement.Wallet, Is.Empty);
        Assert.That(replacement.Bank, Is.EqualTo(75000));
        Assert.That(replacement.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(1000));
    }

    [Test]
    public async Task InvalidMiningReceiptRollsBackTheEntireBatchIncludingIssuance()
    {
        ExchangeResult original;
        ExchangeResult other;
        await using (var db = new SqliteServerDbContext(_options))
        {
            original = await ExchangeOperations.Read(db, _user, 0, 0);
            other = await ExchangeOperations.Read(db, _user, 1, 0);
        }
        var previous = new ExchangeMiningRequest(Guid.NewGuid(), _user, original.ProfileId);
        await using (var db = new SqliteServerDbContext(_options)) await ExchangeOperations.MintToWallets(db, [previous], 3600000);
        var newWork = new ExchangeMiningRequest(Guid.NewGuid(), _user, original.ProfileId);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.MintToWallets(db,
                [newWork, previous with { ProfileId = other.ProfileId }], 3600000));
        await using var read = new SqliteServerDbContext(_options);
        var result = await ExchangeOperations.Read(read, _user, 0, 3600000);
        Assert.That(result.Wallet.Values.Sum(), Is.EqualTo(1000));
        Assert.That(result.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(1000));
        Assert.That(await read.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith("exchange:mine:")), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentRetriesBuyOnlyOnceAndReturnCurrentBalances()
    {
        ExchangeResult initial;
        await using (var db = new SqliteServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var cost = initial.Market.Quote("contrib", 1000).BuyCost;
        var request = new ExchangeTradeRequest(Guid.NewGuid(), 0, "contrib", 1000, true, cost);
        async Task<ExchangeResult> Buy()
        {
            await using var db = new SqliteServerDbContext(_options);
            return await ExchangeOperations.Trade(db, _user, request, 1);
        }
        var results = await Task.WhenAll(Task.Run(Buy), Task.Run(Buy));
        Assert.That(results.All(r => r.Bank == 800000 - cost && r.Wallet["contrib"] == 1000), Is.True);
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.DepositBank(db, _user, 0, 800000 - cost, 7);
        Assert.That((await Buy()).Bank, Is.EqualTo(800007 - cost));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That(await read.BankFinancialOperations.CountAsync(r => r.Kind == "crypto_buy"), Is.EqualTo(1));
        Assert.That((await read.Preference.SingleAsync()).LagoonCoins, Is.EqualTo(100));
    }

    [Test]
    public async Task BankWalletAndPoolCommitAtomicallyAndOnlyTheTargetCharacterIsChanged()
    {
        ExchangeResult initial;
        await using (var db = new SqliteServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var request = new ExchangeTradeRequest(Guid.NewGuid(), 0, "ideas", 1000, true, long.MaxValue);
        ExchangeResult bought;
        await using (var db = new SqliteServerDbContext(_options)) bought = await ExchangeOperations.Trade(db, _user, request, 1);
        await using (var db = new SqliteServerDbContext(_options))
        {
            var other = await ExchangeOperations.Read(db, _user, 1, 2);
            Assert.That(other.Bank, Is.EqualTo(500000));
            Assert.That(other.Wallet, Is.Empty);
        }
        var pool = bought.Market.Pools["ideas"];
        Assert.That(bought.Bank + pool.Cash + pool.FeesBurned, Is.EqualTo(initial.Bank));
        var sell = new ExchangeTradeRequest(Guid.NewGuid(), 0, "ideas", 1000, false, 0);
        await using var sellDb = new SqliteServerDbContext(_options);
        var sold = await ExchangeOperations.Trade(sellDb, _user, sell, 3);
        Assert.That(sold.Wallet["ideas"], Is.Zero);
        Assert.That(sold.Bank + sold.Market.Pools["ideas"].Cash + sold.Market.Pools["ideas"].FeesBurned, Is.EqualTo(initial.Bank));
    }

    [Test]
    public async Task MinedCoinDepositIsIdempotentAndDoesNotCreditSpessos()
    {
        var operation = Guid.NewGuid();
        for (var i = 0; i < 2; i++)
        {
            await using var db = new SqliteServerDbContext(_options);
            var result = await ExchangeOperations.Deposit(db, _user, 0, "goida", 15000, operation, null, i);
            Assert.That(result.Bank, Is.EqualTo(800000));
            Assert.That(result.Wallet["goida"], Is.EqualTo(15000));
            Assert.That(result.Market.Pools["goida"].Cash, Is.Zero);
        }
        await using var invalid = new SqliteServerDbContext(_options);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ExchangeOperations.Deposit(invalid, _user, 1, "goida", 15000, operation, null, 3));
    }

    [Test]
    public async Task FailedSellCannotUseVirtualLiquidityOrChangeWallet()
    {
        await using (var db = new SqliteServerDbContext(_options))
            await ExchangeOperations.Deposit(db, _user, 0, "contrib", 1000, Guid.NewGuid(), null, 0);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Trade(db, _user,
                new(Guid.NewGuid(), 0, "contrib", 1000, false, 0), 1));
        await using var read = new SqliteServerDbContext(_options);
        var result = await ExchangeOperations.Read(read, _user, 0, 2);
        Assert.That(result.Bank, Is.EqualTo(800000));
        Assert.That(result.Wallet["contrib"], Is.EqualTo(1000));
        Assert.That(await read.BankFinancialOperations.CountAsync(r => r.Kind == "crypto_sell" || r.Kind == "crypto_buy"), Is.Zero);
    }

    [Test]
    public async Task RecreatedCharacterSlotDoesNotInheritDeletedCharactersWallet()
    {
        int originalId;
        var creditedOperation = Guid.NewGuid();
        await using (var db = new SqliteServerDbContext(_options))
        {
            originalId = (await ExchangeOperations.Read(db, _user, 0, 0)).ProfileId;
        }
        await using (var db = new SqliteServerDbContext(_options))
            await ExchangeOperations.Deposit(db, _user, 0, "contrib", 1000, creditedOperation, null, 0, expectedProfileId: originalId);
        await using (var db = new SqliteServerDbContext(_options))
        {
            db.Profile.Remove(await db.Profile.SingleAsync(p => p.Slot == 0));
            await db.SaveChangesAsync();
        }
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(_user), new HumanoidCharacterProfile().WithBankBalance(75000), 0);
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await ExchangeOperations.Read(read, _user, 0, 1)).Wallet, Is.Empty);
        await using (var staleTrade = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Trade(staleTrade, _user,
                new(Guid.NewGuid(), 0, "contrib", 1000, true, long.MaxValue, originalId), 2));
        await using (var staleDeposit = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ExchangeOperations.Deposit(staleDeposit, _user,
                0, "contrib", 1000, Guid.NewGuid(), null, 2, expectedProfileId: originalId));
        await using (var retry = new SqliteServerDbContext(_options))
        {
            var result = await ExchangeOperations.Deposit(retry, _user, 0, "contrib", 1000, creditedOperation, null, 2,
                expectedProfileId: originalId);
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Wallet, Is.Empty);
            Assert.That(result.Bank, Is.EqualTo(75000));
        }
    }
}
