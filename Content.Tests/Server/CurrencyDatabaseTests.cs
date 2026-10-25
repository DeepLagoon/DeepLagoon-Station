using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._DeepLagoon.Currency;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Content.Shared.Preferences;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Moq;
using Content.Server._DeepLagoon.Currency;

namespace Content.Tests.Server;

[TestFixture]
public sealed class CurrencyDatabaseTests
{
    private string _path;
    private DbContextOptions<SqliteServerDbContext> _options;
    private Guid _user;
    private Guid _recipient;

    [SetUp]
    public async Task Setup()
    {
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#else
        SQLitePCL.Batteries_V2.Init();
#endif
        _path = Path.Combine(Path.GetTempPath(), $"lagoon-currency-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite($"Data Source={_path};Default Timeout=30;Pooling=False").Options;
        _user = Guid.NewGuid();
        _recipient = Guid.NewGuid();
        await using var db = new SqliteServerDbContext(_options);
        await db.Database.MigrateAsync();
        db.Preference.Add(new Preference { UserId = _user, AdminOOCColor = "#FF0000", Money = 10 });
        db.Preference.Add(new Preference { UserId = _recipient, AdminOOCColor = "#FF0000" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public void Cleanup() => File.Delete(_path);

    [Test]
    public async Task WebhookOutboxIsTransactionalAndSurvivesRestart()
    {
        await using (var db = new SqliteServerDbContext(_options))
        {
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "admin:1", 10, "admin");
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "admin:1", 10, "admin");
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "checkpoint", 0, "playtime", playedTicks: 1);
            Assert.ThrowsAsync<InsufficientLagoonCoinsException>(async () =>
                await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "failed", -11, "deduct"));
        }
        var owner = Guid.NewGuid();
        await using var fresh = new SqliteServerDbContext(_options);
        var pending = await AccountCurrencyOperations.ClaimLagoonCoinWebhook(fresh, owner);
        Assert.That(pending, Is.Not.Null);
        Assert.That(pending!.OperationId, Is.EqualTo("admin:1"));
        Assert.That(pending.BalanceAfter, Is.EqualTo(10));
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(fresh, Guid.NewGuid()), Is.Null);
        await AccountCurrencyOperations.CompleteLagoonCoinWebhook(fresh, _user, pending.OperationId, Guid.NewGuid());
        Assert.That(await fresh.LagoonCoinOperations.AsNoTracking().Where(p => !p.WebhookDelivered).CountAsync(), Is.EqualTo(1));
        await AccountCurrencyOperations.CompleteLagoonCoinWebhook(fresh, _user, pending.OperationId, owner);
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(fresh, owner), Is.Null);
    }

    [Test]
    public async Task EveryGameplayAwardUsesTheSameWebhookJournal()
    {
        await using var db = new SqliteServerDbContext(_options);
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "round:1:ready", 5, "ready");
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "round:1:objective:1", 2, "antagonist-objective");
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "time:1", 0, "playtime", playedTicks: TimeSpan.TicksPerHour);
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "spend:1", -3, "Shop item");
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "purchase:1", 100, "Verified order");
        var pending = await db.LagoonCoinOperations.AsNoTracking().Where(p => !p.WebhookDelivered).OrderBy(p => p.CreatedAt).ToListAsync();
        Assert.That(pending.Select(p => p.Amount), Is.EqualTo(new long[] { 5, 2, 2, -3, 100 }));
        Assert.That(pending.Select(p => p.BalanceAfter), Is.EqualTo(new long[] { 5, 7, 9, 6, 106 }));
    }

    [Test]
    public async Task ExpiredWebhookLeaseCanBeReclaimedByAnotherServer()
    {
        await using var db = new SqliteServerDbContext(_options);
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "purchase:order", 100, "purchase");
        var first = Guid.NewGuid();
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, first), Is.Not.Null);
        await db.LagoonCoinOperations.ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookLeaseUntil, (DateTime?)DateTime.UtcNow.AddMinutes(-1)));
        var second = Guid.NewGuid();
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, second), Is.Not.Null);
        await AccountCurrencyOperations.CompleteLagoonCoinWebhook(db, _user, "purchase:order", first);
        Assert.That(await db.LagoonCoinOperations.AsNoTracking().Where(p => !p.WebhookDelivered).CountAsync(), Is.EqualTo(1));
        await AccountCurrencyOperations.CompleteLagoonCoinWebhook(db, _user, "purchase:order", second);
        Assert.That(await db.LagoonCoinOperations.AsNoTracking().Where(p => !p.WebhookDelivered).CountAsync(), Is.Zero);
    }

    [Test]
    public async Task HandledFailureReschedulesWithoutWaitingForCrashLease()
    {
        await using var db = new SqliteServerDbContext(_options);
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "retry:1", 1, "admin");
        var owner = Guid.NewGuid();
        await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, owner);
        var retryAt = DateTime.UtcNow.AddSeconds(30);
        await AccountCurrencyOperations.RetryLagoonCoinWebhook(db, _user, "retry:1", Guid.NewGuid(), retryAt);
        var untouched = await db.LagoonCoinOperations.AsNoTracking().SingleAsync();
        Assert.That(untouched.WebhookLeaseOwner, Is.EqualTo(owner));
        await AccountCurrencyOperations.RetryLagoonCoinWebhook(db, _user, "retry:1", owner, retryAt);
        var pending = await db.LagoonCoinOperations.AsNoTracking().SingleAsync();
        Assert.That(pending.WebhookLeaseOwner, Is.Null);
        Assert.That(pending.WebhookLeaseUntil, Is.EqualTo(retryAt));
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, Guid.NewGuid()), Is.Null);
        await AccountCurrencyOperations.RetryLagoonCoinWebhook(db, _user, "retry:1", owner, DateTime.UtcNow.AddMinutes(-1));
        var stillPending = await db.LagoonCoinOperations.AsNoTracking().SingleAsync();
        Assert.That(stillPending.WebhookLeaseUntil, Is.EqualTo(retryAt));
        await db.LagoonCoinOperations.ExecuteUpdateAsync(s => s.SetProperty(p => p.WebhookLeaseUntil, (DateTime?)DateTime.UtcNow.AddSeconds(-1)));
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, Guid.NewGuid()), Is.Not.Null);
    }

    [Test]
    public async Task UpgradeDoesNotReplayPreWebhookHistory()
    {
        await using var db = new SqliteServerDbContext(_options);
        await db.GetService<IMigrator>().MigrateAsync("20261009210957_LagoonCoinWallet");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO lagoon_coin_operations(user_id,operation_id,amount,played_ticks,subscriber_ticks,reason,created_at) VALUES ({_user}, 'old', 5, 0, 0, 'admin', {DateTime.UtcNow})");
        await db.Database.MigrateAsync();
        Assert.That(await AccountCurrencyOperations.ClaimLagoonCoinWebhook(db, Guid.NewGuid()), Is.Null);
    }

    [Test]
    public async Task LagoonCoinDeductionIsAtomicIdempotentAndJournaled()
    {
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "seed", 10, "admin");
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That((await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "discord:remove", -7, "deduct")).Balance, Is.EqualTo(3));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That((await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "discord:remove", -7, "deduct")).Applied, Is.False);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InsufficientLagoonCoinsException>(async () =>
                await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "too-much", -4, "deduct"));
        await using var check = new SqliteServerDbContext(_options);
        var row = await check.Preference.SingleAsync(p => p.UserId == _user);
        Assert.That(row.LagoonCoins, Is.EqualTo(3));
        Assert.That(row.Money, Is.EqualTo(10));
        Assert.That(await check.LagoonCoinOperations.CountAsync(), Is.EqualTo(2));
        var history = await AccountCurrencyOperations.GetLagoonCoinHistory(check, _user, 1);
        Assert.That(history.First().Amount, Is.EqualTo(-7));
        Assert.That(history.Count, Is.EqualTo(2));
        Assert.That(await AccountCurrencyOperations.GetLagoonCoinHistory(check, _recipient, 1), Is.Empty);
    }

    [Test]
    public async Task ConcurrentLagoonCoinDeductionsCannotOverdraw()
    {
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "seed", 10, "admin");
        async Task<bool> Deduct(string key)
        {
            await using var db = new SqliteServerDbContext(_options);
            try { await AccountCurrencyOperations.AwardLagoonCoins(db, _user, key, -7, "deduct"); return true; }
            catch (InsufficientLagoonCoinsException) { return false; }
        }
        Assert.That((await Task.WhenAll(Deduct("one"), Deduct("two"))).Count(v => v), Is.EqualTo(1));
        await using var check = new SqliteServerDbContext(_options);
        Assert.That((await check.Preference.SingleAsync(p => p.UserId == _user)).LagoonCoins, Is.EqualTo(3));
    }

    [Test]
    public async Task LagoonCoinHistoryPaginatesAndExcludesTimeCheckpoints()
    {
        await using var db = new SqliteServerDbContext(_options);
        for (var i = 0; i < 25; i++)
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, $"award:{i:D2}", 1, "admin");
        await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "checkpoint", 0, "playtime", playedTicks: 1);
        var page1 = await AccountCurrencyOperations.GetLagoonCoinHistory(db, _user, 1);
        var page2 = await AccountCurrencyOperations.GetLagoonCoinHistory(db, _user, 2);
        Assert.That(page1.Count, Is.EqualTo(21)); // One extra row indicates a next page.
        Assert.That(page2.Count, Is.EqualTo(5));
        Assert.That(page1.Take(20).Select(r => r.OperationId).Intersect(page2.Select(r => r.OperationId)), Is.Empty);
        Assert.That(page1.All(r => r.Amount != 0), Is.True);
    }

    private async Task SaveBank(Guid user, int balance, int slot = 0)
    {
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        await backend.SaveCharacterSlotAsync(new NetUserId(user), new HumanoidCharacterProfile().WithBankBalance(balance), slot);
    }

    [Test]
    public async Task ConcurrentDepositsRejectStaleBalanceWithoutChangingArchivedMoney()
    {
        await SaveBank(_user, 10);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = new SqliteServerDbContext(_options);
            try { await AccountCurrencyOperations.DepositBank(db, _user, 0, 10, 1); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That(outcomes.Count(p => p), Is.EqualTo(1));
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(11));
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
    }

    [Test]
    public async Task ConcurrentTransfersCannotSpendTheSameCharacterBalanceTwice()
    {
        await SaveBank(_user, 10);
        await SaveBank(_recipient, 0);
        async Task<bool> Transfer()
        {
            await using var db = new SqliteServerDbContext(_options);
            try { await AccountCurrencyOperations.TransferBank(db, _user, 0, 10, _recipient, 0, 0, 8); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Transfer(), Transfer());
        await using var read = new SqliteServerDbContext(_options);
        var balances = await read.Profile.Select(p => p.BankBalance).ToArrayAsync();
        Assert.Multiple(() =>
        {
            Assert.That(outcomes.Count(p => p), Is.EqualTo(1));
            Assert.That(balances.Sum(), Is.EqualTo(10));
            Assert.That(balances, Does.Contain(2));
            Assert.That(balances, Does.Contain(8));
        });
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
    }

    [Test]
    public async Task FailedTransferAndOverflowLeaveBothCharacterAccountsUnchanged()
    {
        await SaveBank(_user, 10);
        await SaveBank(_recipient, int.MaxValue);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<OverflowException>(() => AccountCurrencyOperations.TransferBank(db, _user, 0, 10, _recipient, 0, int.MaxValue, 1));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.TransferBank(db, _user, 0, 10, _recipient, 0, int.MaxValue, 11));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.TransferBank(db, _user, 0, 10, Guid.NewGuid(), 0, 0, 1));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _user)).BankBalance, Is.EqualTo(10));
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _recipient)).BankBalance, Is.EqualTo(int.MaxValue));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1)]
    public async Task SelfAndNonPositiveTransfersAreRejected(int amount)
    {
        await SaveBank(_user, 10);
        await using var db = new SqliteServerDbContext(_options);
        Assert.ThrowsAsync<ArgumentException>(() => AccountCurrencyOperations.TransferBank(db, _user, 0, 10,
            amount > 0 ? _user : _recipient, 0, 0, amount));
        Assert.That((await db.Profile.SingleAsync()).BankBalance, Is.EqualTo(10));
    }

    [Test]
    public async Task PaymentUsesOnlyCharacterBalanceEvenWhenArchivedMoneyExists()
    {
        await SaveBank(_user, 2);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.WithdrawBank(db, _user, 0, 2, 12));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(await AccountCurrencyOperations.WithdrawBank(db, _user, 0, 2, 2), Is.Zero);
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.Zero);
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
    }

    [Test]
    public async Task DelayedProfileSaveCannotUndoCharacterPayment()
    {
        var user = new NetUserId(_user);
        var backend = new ServerDbSqlite(() => _options, true, Mock.Of<IConfigurationManager>(), true, Mock.Of<ISawmill>());
        var profile = new HumanoidCharacterProfile().WithBankBalance(7);
        await backend.SaveCharacterSlotAsync(user, profile, 0);
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.WithdrawBank(db, _user, 0, 7, 5);
        await backend.SaveCharacterSlotAsync(user, profile, 0);
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(2));
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
        await using var rejected = new SqliteServerDbContext(_options);
        Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.WithdrawBank(rejected, _user, 0, 7, 1));
    }

    [Test]
    public async Task FullDepositReachesCharacterAndOverflowRollsBack()
    {
        await SaveBank(_user, 7);
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(await AccountCurrencyOperations.DepositBank(db, _user, 0, 7, 9), Is.EqualTo(16));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<OverflowException>(() => AccountCurrencyOperations.DepositBank(db, _user, 0, 16, int.MaxValue));
        await using (var db = new SqliteServerDbContext(_options))
            Assert.ThrowsAsync<InvalidOperationException>(() => AccountCurrencyOperations.DepositBank(db, _user, 0, 7, 9));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
        Assert.That((await read.Profile.SingleAsync()).BankBalance, Is.EqualTo(16));
    }

    [Test]
    public async Task TransferChangesOnlySpecifiedCharactersAndPreservesLagoonCoins()
    {
        await SaveBank(_user, 50);
        await SaveBank(_recipient, 60);
        await SaveBank(_user, 10, 1);
        await SaveBank(_recipient, 20, 1);
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "transfer-test", 5, "admin");
        await using (var db = new SqliteServerDbContext(_options))
            Assert.That(await AccountCurrencyOperations.TransferBank(db, _user, 1, 10, _recipient, 1, 20, 3), Is.EqualTo(new BankTransfer(7, 23)));
        await using var read = new SqliteServerDbContext(_options);
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 0)).BankBalance, Is.EqualTo(50));
        Assert.That((await read.Profile.SingleAsync(p => p.Preference.UserId == _recipient && p.Slot == 0)).BankBalance, Is.EqualTo(60));
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).LagoonCoins, Is.EqualTo(5));
        Assert.That((await read.Preference.SingleAsync(p => p.UserId == _user)).Money, Is.EqualTo(10));
    }

    [Test]
    public void PendingRewardsSurviveRestartAndCompletionRemovesOnlyTheirReceipt()
    {
        var path = _path + ".outbox";
        var reward = new PendingLagoonCoinReward(_user, "round:3:ready", 5, "ready");
        try
        {
            using (var outbox = new LagoonCoinOutbox(path))
            {
                Assert.That(outbox.Store(reward), Is.True);
                Assert.That(outbox.Store(reward), Is.False);
            }
            using (var reopened = new LagoonCoinOutbox(path))
            {
                Assert.That(reopened.Load().Single(), Is.EqualTo(reward));
                reopened.Complete(reward);
                Assert.That(reopened.Load(), Is.Empty);
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Test]
    public async Task AdditiveMigrationPreservesOldMoneyAndStartsLagoonWalletAtZero()
    {
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite("Data Source=:memory:").Options;
        await using var db = new SqliteServerDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261009160000_UnequippedLoadoutSlots");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO preference (user_id, selected_character_slot, admin_ooc_color, mono_coins) VALUES ({_user}, 0, '#FF0000', 12345)");
        await db.Database.MigrateAsync();
        var row = await db.Preference.SingleAsync();
        Assert.That(row.Money, Is.EqualTo(12345));
        Assert.That(row.LagoonCoins, Is.Zero);
        Assert.That(row.LagoonCoinPlayedTicks, Is.Zero);
    }

    [Test]
    public async Task RewardsAreIdempotentAndSeparateFromMoney()
    {
        async Task<LagoonCoinResult> Award()
        {
            await using var db = new SqliteServerDbContext(_options);
            return await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "round:1:ready", 5, "ready");
        }
        var results = await Task.WhenAll(Award(), Award());
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "round:1:goal:1", 2, "objective");
        await using var read = new SqliteServerDbContext(_options);
        var row = await read.Preference.SingleAsync(p => p.UserId == _user);
        Assert.Multiple(() =>
        {
            Assert.That(results.Count(p => p.Applied), Is.EqualTo(1));
            Assert.That(row.LagoonCoins, Is.EqualTo(7));
            Assert.That(row.Money, Is.EqualTo(10));
        });
        Assert.That(await read.LagoonCoinOperations.CountAsync(), Is.EqualTo(2));
    }

    [TestCase(false, 2)]
    [TestCase(true, 4)]
    public async Task IncompleteHourSurvivesSessionsAndDoesNotRewardTwice(bool subscribed, long expected)
    {
        var halfHour = TimeSpan.TicksPerHour / 2;
        await using (var db = new SqliteServerDbContext(_options))
        {
            var result = await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "session:1", 0, "playtime",
                playedTicks: halfHour, subscriberTicks: subscribed ? halfHour : 0);
            Assert.That(result.Balance, Is.Zero);
        }
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "session:2", 0, "playtime",
                playedTicks: halfHour, subscriberTicks: subscribed ? halfHour : 0);
        await using (var db = new SqliteServerDbContext(_options))
        {
            var replay = await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "session:2", 0, "playtime",
                playedTicks: halfHour, subscriberTicks: subscribed ? halfHour : 0);
            Assert.That(replay.Applied, Is.False);
            Assert.That(replay.Balance, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task SubscriptionChangeWithinHourAwardsThreeCoins()
    {
        await using (var db = new SqliteServerDbContext(_options))
            await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "time:1", 0, "playtime", playedTicks: TimeSpan.TicksPerHour / 2);
        await using var second = new SqliteServerDbContext(_options);
        var result = await AccountCurrencyOperations.AwardLagoonCoins(second, _user, "time:2", 0, "playtime",
            playedTicks: TimeSpan.TicksPerHour / 2, subscriberTicks: TimeSpan.TicksPerHour / 2);
        Assert.That(result.Balance, Is.EqualTo(3));
    }

    [TestCase(false, 2)]
    [TestCase(true, 4)]
    public async Task PlaytimePaysOnlyAtEachFullAccumulatedHour(bool subscribed, long hourlyReward)
    {
        async Task<LagoonCoinResult> Save(string key, long ticks)
        {
            // A new context also exercises the persisted remainder after reconnecting.
            await using var db = new SqliteServerDbContext(_options);
            return await AccountCurrencyOperations.AwardLagoonCoins(db, _user, key, 0, "playtime",
                playedTicks: ticks, subscriberTicks: subscribed ? ticks : 0);
        }

        Assert.That((await Save("before-disconnect", TimeSpan.FromMinutes(59).Ticks)).Awarded, Is.Zero);
        Assert.That((await Save("reconnected", 0)).Awarded, Is.Zero, "Connecting alone must not pay LC.");
        Assert.That((await Save("after-reconnect", TimeSpan.FromSeconds(59).Ticks)).Awarded, Is.Zero);

        await using (var db = new SqliteServerDbContext(_options))
        {
            var ready = await AccountCurrencyOperations.AwardLagoonCoins(db, _user, "round:1:ready", 5, "ready");
            Assert.That(ready.Awarded, Is.EqualTo(5), "The Ready reward must remain separate from playtime.");
        }

        var firstHour = await Save("first-hour", TimeSpan.TicksPerSecond);
        Assert.That(firstHour, Is.EqualTo(new LagoonCoinResult(5 + hourlyReward, hourlyReward, true)));
        Assert.That((await Save("first-hour", TimeSpan.TicksPerSecond)).Applied, Is.False);
        Assert.That((await Save("almost-second-hour", TimeSpan.TicksPerHour - 1)).Awarded, Is.Zero);
        var secondHour = await Save("second-hour", 1);
        Assert.That(secondHour, Is.EqualTo(new LagoonCoinResult(5 + 2 * hourlyReward, hourlyReward, true)));

        await using var read = new SqliteServerDbContext(_options);
        var row = await read.Preference.SingleAsync(p => p.UserId == _user);
        Assert.Multiple(() =>
        {
            Assert.That(row.LagoonCoinPlayedTicks, Is.Zero);
            Assert.That(row.LagoonCoinBonusTicks, Is.Zero);
            Assert.That(row.Money, Is.EqualTo(10));
        });
    }

    [Test]
    public void GhostLobbyAfkAndDisconnectedPlayersNeverCountAsPlayedTime()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LagoonCoinRules.CountsTime(true, true, true, false, true, false), Is.True);
            Assert.That(LagoonCoinRules.CountsTime(true, true, true, true, true, false), Is.False);
            Assert.That(LagoonCoinRules.CountsTime(false, true, true, false, true, false), Is.False);
            Assert.That(LagoonCoinRules.CountsTime(true, false, true, false, true, false), Is.False);
            Assert.That(LagoonCoinRules.CountsTime(true, true, false, false, true, false), Is.False);
            Assert.That(LagoonCoinRules.CountsTime(true, true, true, false, false, false), Is.False);
            Assert.That(LagoonCoinRules.CountsTime(true, true, true, false, true, true), Is.False);
        });
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    public void EverySubscriptionTierQualifies(int tier, bool expected)
        => Assert.That(LagoonCoinRules.HasSubscription(tier), Is.EqualTo(expected));

    [TestCase(true)]
    [TestCase(false)]
    public void MigrationIsAdditiveAndModelsAgree(bool postgres)
    {
        using DbContext db = postgres
            ? new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
                .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options)
            : new SqliteServerDbContext(_options);
        Assert.That(db.Database.HasPendingModelChanges(), Is.False);
        var sql = db.GetService<IMigrator>().GenerateScript("20261009160000_UnequippedLoadoutSlots");
        Assert.That(sql, Does.Contain("lagoon_coins"));
        Assert.That(sql, Does.Not.Contain("DROP COLUMN mono_coins"));
        Assert.That(sql, Does.Not.Contain("RENAME COLUMN mono_coins"));
    }
}
