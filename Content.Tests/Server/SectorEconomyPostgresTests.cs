using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;

namespace Content.Tests.Server;

[TestFixture, NonParallelizable]
public sealed class SectorEconomyPostgresTests
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
        await db.LagoonEconomyRecords.Where(r => r.Id.StartsWith("economy:") || r.Id.StartsWith("exchange:")).ExecuteDeleteAsync();
        var preference = new Preference { UserId = _user, AdminOOCColor = "#FFFFFF", LagoonCoins = 100 };
        var profile = new Profile { Preference = preference, Slot = 0, BankBalance = 800000 };
        // Minimal database fixture; gameplay profile validation is exercised by the normal SQLite/profile tests.
        foreach (var property in typeof(Profile).GetProperties().Where(p => p.CanWrite && p.PropertyType == typeof(string)))
            if (property.GetValue(profile) == null) property.SetValue(profile, "");
        db.Profile.Add(profile);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task ConcurrentPalletReceiptsCannotOverspendOrIssueTwice()
    {
        var epoch = Guid.NewGuid(); int profile;
        await using (var db = new PostgresServerDbContext(_options))
        {
            for (var minute = 0; minute <= 10; minute++) await EconomyOperations.Sample(db, epoch, minute * 60000L, 30, new(), []);
            profile = (await EconomyOperations.Read(db, _user, 0)).Profile;
        }
        var request = new EconomyProcurement(Guid.NewGuid(), _user, profile, 8000, 0, new() { ["Materials"] = 8000 }, null);
        async Task<EconomyPayment> Pay()
        { await using var db = new PostgresServerDbContext(_options); return await EconomyOperations.Procure(db, request, new(), 600000); }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Pay)));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        Assert.That(results.All(r => r.Accepted), Is.True);
        await using var read = new PostgresServerDbContext(_options);
        var actual = await EconomyOperations.Read(read, _user, 0);
        Assert.That(actual.Bank, Is.EqualTo(808000));
        Assert.That(actual.Finance.Spent, Is.EqualTo(8000));
    }
    [Test]
    public async Task ConcurrentBuyOrderReservationAndCancellationConserveTheBank()
    {
        int profile;
        await using (var db = new PostgresServerDbContext(_options)) profile = (await EconomyOperations.Read(db, _user, 0)).Profile;
        var create = new TradeCreate(Guid.NewGuid(), _user, profile, true, "Steel", "Steel", "", 3, 5000, new(), null, null);
        async Task<TradeResult> Create()
        { await using var db = new PostgresServerDbContext(_options); return await EconomyOperations.CreateOffer(db, create, 0); }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Create)));
        Assert.That(results.Count(r => r.Applied), Is.EqualTo(1));
        await using (var read = new PostgresServerDbContext(_options)) Assert.That((await EconomyOperations.Read(read, _user, 0)).Bank, Is.EqualTo(785000));
        var cancel = Guid.NewGuid();
        async Task<TradeResult> Cancel()
        { await using var db = new PostgresServerDbContext(_options); return await EconomyOperations.CancelOffer(db, _user, profile, create.Operation, cancel, 1); }
        var refunds = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Cancel)));
        Assert.That(refunds.Count(r => r.Applied), Is.EqualTo(1));
        await using var final = new PostgresServerDbContext(_options);
        Assert.That((await EconomyOperations.Read(final, _user, 0)).Bank, Is.EqualTo(800000));
    }
}
