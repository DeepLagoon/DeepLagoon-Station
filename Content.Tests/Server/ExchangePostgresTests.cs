using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture, NonParallelizable]
public sealed class ExchangePostgresTests
{
    private DbContextOptions<PostgresServerDbContext> _options;
    private Guid _user;

    [OneTimeSetUp]
    public async Task MigrateIsolatedDatabase()
    {
        var connection = Environment.GetEnvironmentVariable("DL_EXCHANGE_TEST_PG");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Ignore("Set DL_EXCHANGE_TEST_PG to an isolated exchange_test* database.");
        var builder = new NpgsqlConnectionStringBuilder(connection!);
        Assert.That(builder.Database, Does.StartWith("exchange_test"), "Never run this fixture against a game database.");
        _options = new DbContextOptionsBuilder<PostgresServerDbContext>().UseNpgsql(connection).Options;
        await using var db = new PostgresServerDbContext(_options);
        await db.Database.MigrateAsync();
    }

    [SetUp]
    public async Task SeedCharacter()
    {
        _user = Guid.NewGuid();
        await using var db = new PostgresServerDbContext(_options);
        await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith("exchange:")).ExecuteDeleteAsync();
        var preference = new Preference { UserId = _user, AdminOOCColor = "#FFFFFF", LagoonCoins = 100 };
        var profile = new Profile { Preference = preference, Slot = 0, BankBalance = 800000 };
        // Minimal database fixture; gameplay profile validation is exercised by the normal SQLite/profile tests.
        foreach (var property in typeof(Profile).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(string)))
            if (property.GetValue(profile) == null) property.SetValue(profile, "");
        db.Profile.Add(profile);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task ConcurrentDirectMiningBatchesCreditWalletsOnlyOnceWithinTheSharedIssueBudget()
    {
        ExchangeResult initial;
        await using (var db = new PostgresServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var requests = Enumerable.Range(0, 40).Select(_ => new ExchangeMiningRequest(Guid.NewGuid(), _user, initial.ProfileId)).ToArray();
        async Task<ExchangeMiningResult[]> Mine()
        {
            await using var db = new PostgresServerDbContext(_options);
            return await ExchangeOperations.MintToWallets(db, requests, 3600000);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Mine)));
        Assert.That(results.SelectMany(r => r).Where(r => r.Applied).Sum(r => r.Units), Is.EqualTo(30000));
        await using var read = new PostgresServerDbContext(_options);
        var state = await ExchangeOperations.Read(read, _user, 0, 3600000);
        Assert.That(state.Wallet.Values.Sum(), Is.EqualTo(30000));
        Assert.That(state.Bank, Is.EqualTo(800000));
        Assert.That(state.Market.Pools.Values.Sum(p => p.MinedUnits), Is.EqualTo(30000));
        Assert.That(state.Market.Pools.Values.Sum(p => p.Cash), Is.Zero);
        Assert.That(await read.LagoonEconomyRecords.CountAsync(r => r.Id.StartsWith("exchange:mine:")), Is.EqualTo(40));
    }

    [Test]
    public async Task ConcurrentTradeRetriesAreSerializedAndDebitOnlyOnce()
    {
        ExchangeResult initial;
        await using (var db = new PostgresServerDbContext(_options)) initial = await ExchangeOperations.Read(db, _user, 0, 0);
        var request = new ExchangeTradeRequest(Guid.NewGuid(), 0, "contrib", 1000, true,
            initial.Market.Quote("contrib", 1000).BuyCost, initial.ProfileId);
        async Task<ExchangeResult> Buy()
        {
            await using var db = new PostgresServerDbContext(_options);
            return await ExchangeOperations.Trade(db, _user, request, 1);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Buy)));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        Assert.That(results.All(r => r.Wallet["contrib"] == 1000 && r.Bank == 800000 - request.Limit), Is.True);
        await using var read = new PostgresServerDbContext(_options);
        Assert.That(await read.BankFinancialOperations.CountAsync(r => r.UserId == _user && r.Kind == "crypto_buy"), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentMinedCoinDepositsCreditAssetsOnceAndNeverIssueSpessos()
    {
        var operation = Guid.NewGuid();
        async Task<ExchangeResult> Deposit()
        {
            await using var db = new PostgresServerDbContext(_options);
            return await ExchangeOperations.Deposit(db, _user, 0, "ideas", 15000, operation, null, 1);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Deposit)));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        Assert.That(results.All(r => r.Bank == 800000 && r.Wallet["ideas"] == 15000 && r.Market.Pools["ideas"].Cash == 0), Is.True);
    }

    [Test]
    public async Task GlobalIssuanceIsSharedAcrossConcurrentFarmBatchesAndPersists()
    {
        await using (var db = new PostgresServerDbContext(_options))
            Assert.That(await ExchangeOperations.Mint(db, 64, 0), Is.Empty);
        async Task<string[]> Mint()
        {
            await using var db = new PostgresServerDbContext(_options);
            return await ExchangeOperations.Mint(db, 64, 3600000);
        }
        var batches = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Mint)));
        Assert.That(batches.Sum(b => b.Length), Is.EqualTo(30));
        Assert.That(await Mint(), Is.Empty);
    }
}
