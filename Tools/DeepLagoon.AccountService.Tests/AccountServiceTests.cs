using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Server.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Npgsql;

namespace DeepLagoon.AccountService.Tests;

[TestFixture]
public sealed partial class AccountServiceTests
{
    private string _directory = null!;
    private ServiceSettings _settings = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private Guid _user;
    private Guid _other;
    private const string Target = "1554164947804487771";
    private const string Actor = "1554164947804487770";
    private const string Receipt = "1554164947804487772";

    [SetUp]
    public async Task Setup()
    {
        SQLitePCL.Batteries_V2.Init();
        _directory = Path.Combine(Path.GetTempPath(), "account-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var postgres = Environment.GetEnvironmentVariable("ACCOUNT_TEST_POSTGRES");
        _settings = new ServiceSettings(postgres == null ? "sqlite" : "postgres",
            postgres ?? $"Data Source={Path.Combine(_directory, "game.db")};Pooling=False;Default Timeout=8",
            Path.Combine(_directory, "links.db"), Path.Combine(_directory, "receipts.db"), new string('w', 64), new string('c', 64), 0)
            { SupporterToken = new string('s', 64) };
        _user = Guid.NewGuid();
        _other = Guid.NewGuid();
        await using (var db = _settings.OpenDatabase())
        {
            await db.Database.MigrateAsync();
            foreach (var user in new[] { _user, _other })
            {
                var preference = new Preference { UserId = user, AdminOOCColor = "#000000", Money = user == _user ? 777 : 0 };
                db.Profile.Add(new Profile
                {
                    Preference = preference, Slot = 0, CharacterName = "Test", FlavorText = "",
                    Sex = "Male", Gender = "Male", Species = "Human", HairName = "", HairColor = "#000000",
                    FacialHairName = "", FacialHairColor = "#000000", EyeColor = "#000000", SkinColor = "#000000",
                });
            }
            await db.SaveChangesAsync();
        }
        Links("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE discord_links(discord_id TEXT PRIMARY KEY, ss14_uid TEXT UNIQUE, username TEXT);
            CREATE TABLE discord_account_merge_pending(official_uid TEXT,canonical_uid TEXT,discord_id TEXT);
            CREATE TABLE discord_account_aliases(official_uid TEXT,canonical_uid TEXT,discord_id TEXT);
            """);
        Links("INSERT INTO discord_links VALUES($id,$uid,'Player')", ("$id", Target), ("$uid", _user.ToString()));
        var reviewer = Guid.NewGuid();
        Links("INSERT INTO discord_links VALUES($id,$uid,'Host')", ("$id", Actor), ("$uid", reviewer.ToString()));
        SetHost(reviewer);
        _app = await Program.CreateApplication(_settings);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        _http = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
    }

    [TearDown]
    public async Task Cleanup()
    {
        _http?.Dispose();
        if (_app != null) await _app.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private void Links(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = new SqliteConnection($"Data Source={_settings.LinkDatabase};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private void SetHost(Guid uid)
    {
        using var db = _settings.OpenDatabase();
        db.Admin.Add(new Admin { UserId = uid, Flags = [new AdminFlag { Flag = "HOST" }] });
        db.SaveChanges();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string action, object body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/deeplagoon/discord/" + action);
        request.Headers.Authorization = new("Bearer", token ?? (action.Contains("whitelist") ? _settings.WhitelistToken :
            action == "economy_supporter" ? _settings.SupporterToken : _settings.CoinToken));
        request.Content = JsonContent.Create(body);
        using var reply = await _http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();
        return (reply.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static object Change(long amount = 10, string receipt = Receipt, string reason = "test", bool authorized = true) =>
        new { discord_id = Target, actor_discord_id = Actor, request_id = receipt, amount, reason, host_authorized = authorized };

    [Test]
    public async Task WhitelistWorksWithoutAnyGameProcessAndProtectsAdmins()
    {
        var added = await Call("whitelist", new { discord_id = Target });
        Assert.That(added.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(added.Body.GetProperty("existing").GetBoolean(), Is.False);
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.True);
        db.Admin.Add(new Admin { UserId = _user });
        await db.SaveChangesAsync();
        var denied = await Call("remove_whitelist", new { discord_id = Target, host_authorized = false });
        Assert.That(denied.Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.True);
        var removed = await Call("remove_whitelist", new { discord_id = Target, actor_discord_id = Actor, host_authorized = true });
        Assert.That(removed.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);
    }

    [Test]
    public async Task MissingWrongAndCrossScopeCredentialsCannotWrite()
    {
        foreach (var token in new[] { "invalid", _settings.WhitelistToken })
            Assert.That((await Call("lc_add", Change(), token)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Call("whitelist", new { discord_id = Target }, _settings.CoinToken)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Call("lc_add", Change(authorized: false))).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await _http.PostAsJsonAsync("/deeplagoon/discord/lc_add", Change())).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(o => o.UserId == _user), Is.Zero);
    }

    [Test]
    public async Task ConcurrentDuplicateCreditsAndDebitsApplyExactlyOnce()
    {
        var credits = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call("lc_add", Change(100))));
        Assert.That(credits.All(r => r.Status == HttpStatusCode.OK), Is.True);
        Assert.That(credits.Count(r => r.Body.GetProperty("applied").GetBoolean()), Is.EqualTo(1));
        var debits = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Call("lc_remove", Change(25, "1554164947804487773"))));
        Assert.That(debits.All(r => r.Status == HttpStatusCode.OK), Is.True);
        Assert.That(debits.Count(r => r.Body.GetProperty("applied").GetBoolean()), Is.EqualTo(1));
        await using var db = _settings.OpenDatabase();
        var row = await db.Preference.SingleAsync(p => p.UserId == _user);
        Assert.That(row.LagoonCoins, Is.EqualTo(75));
        Assert.That(row.Money, Is.EqualTo(777));
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user), Is.EqualTo(2));
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user && !p.WebhookDelivered), Is.EqualTo(2));
    }

    [Test]
    public async Task ConflictingReceiptCannotChangeAmountSignReasonOrTarget()
    {
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.OK));
        foreach (var operation in new[] { ("lc_add", Change(11)), ("lc_remove", Change()), ("lc_add", Change(reason: "different")) })
            Assert.That((await Call(operation.Item1, operation.Item2)).Body.GetProperty("error").GetString(), Is.EqualTo("operation_conflict"));
        Links("UPDATE discord_links SET ss14_uid=$uid WHERE discord_id=$id", ("$uid", _other.ToString()), ("$id", Target));
        Assert.That((await Call("lc_add", Change())).Body.GetProperty("error").GetString(), Is.EqualTo("operation_conflict"));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.Preference.Where(p => p.UserId == _other).Select(p => p.LagoonCoins).SingleAsync(), Is.Zero);
    }

    [Test]
    public async Task LostResponseAndServiceRestartCannotReplayCredit()
    {
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.OK));
        await _app.DisposeAsync();
        _http.Dispose();
        _app = await Program.CreateApplication(_settings);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var retry = await Call("lc_add", Change());
        Assert.That(retry.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(retry.Body.GetProperty("applied").GetBoolean(), Is.False);
        Assert.That(retry.Body.GetProperty("balance").GetInt64(), Is.EqualTo(10));
    }

    [Test]
    public async Task InsufficientFundsAndPendingMergeDoNotMutateWalletOrWhitelist()
    {
        Assert.That((await Call("lc_remove", Change())).Body.GetProperty("error").GetString(), Is.EqualTo("insufficient_lc"));
        Links("INSERT INTO discord_account_merge_pending VALUES($uid,$other,$discord)", ("$uid", _user.ToString()), ("$other", _other.ToString()), ("$discord", Target));
        foreach (var operation in new[] { "lc_add", "lc_remove", "whitelist", "remove_whitelist" })
            Assert.That((await Call(operation, Change())).Status, Is.EqualTo(HttpStatusCode.Conflict));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user), Is.Zero);
        Assert.That(await db.Whitelist.AnyAsync(w => w.UserId == _user), Is.False);
    }

    [Test]
    public async Task IndependentGameWriterAndServiceShareRowLockWithoutLostUpdates()
    {
        async Task GameReward()
        {
            await using var game = _settings.OpenDatabase();
            await AccountCurrencyOperations.AwardLagoonCoins(game, _user, "game:reward", 5, "gameplay");
        }
        await Task.WhenAll(GameReward(), Call("lc_add", Change()));
        var history = await Call("lc_history", Change());
        Assert.That(history.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(history.Body.GetProperty("balance").GetInt64(), Is.EqualTo(15));
        Assert.That(history.Body.GetProperty("rows").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task LinkRevokedBeforeCommitRollsBackBalanceAndJournal()
    {
        await using var db = _settings.OpenDatabase();
        Assert.ThrowsAsync<AccountError>(async () => await AccountCurrencyOperations.AwardLagoonCoins(
            db, _user, "discord:rollback", 10, "test", beforeCommit: () => throw new AccountError("stale_link")));
        await using var fresh = _settings.OpenDatabase();
        Assert.That(await fresh.Preference.Where(p => p.UserId == _user).Select(p => p.LagoonCoins).SingleAsync(), Is.Zero);
        Assert.That(await fresh.LagoonCoinOperations.AnyAsync(p => p.UserId == _user), Is.False);
    }

    [Test]
    public async Task RestrictedPostgresLoginUsesPublishedGrantsWithoutMoneyOrAdminWrites()
    {
        if (_settings.Engine != "postgres") Assert.Ignore("PostgreSQL column permissions require PostgreSQL");
        await using var owner = _settings.OpenDatabase();
        await owner.Database.ExecuteSqlRawAsync("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='lagoon_accounts') THEN
                CREATE ROLE lagoon_accounts LOGIN PASSWORD 'synthetic-account-service-password';
              END IF;
            END $$;
            """);
        await owner.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "grants.sql")));
        var connection = new NpgsqlConnectionStringBuilder(_settings.Connection)
        { Username = "lagoon_accounts", Password = "synthetic-account-service-password" };
        var limited = _settings with { Connection = connection.ToString() };
        using var service = new AccountOperations(limited);
        await service.CheckSchema();
        await service.Execute("whitelist", new AccountRequest(Target));
        await service.Execute("remove_whitelist", new AccountRequest(Target));
        await service.Execute("lc_add", new AccountRequest(Target, Actor, Receipt, 10, "test", true));
        await service.Execute("lc_remove", new AccountRequest(Target, Actor, "1554164947804487773", 5, "test", true));
        await service.Execute("lc_history", new AccountRequest(Target, Actor, Receipt, HostAuthorized: true));
        await using var restricted = limited.OpenDatabase();
        var moneyError = Assert.ThrowsAsync<PostgresException>(async () => await restricted.Preference.Where(p => p.UserId == _user)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Money, 0L)));
        Assert.That(moneyError!.SqlState, Is.EqualTo("42501"));
        var adminError = Assert.ThrowsAsync<PostgresException>(async () => await restricted.Admin.Where(p => p.UserId == _user).ExecuteDeleteAsync());
        Assert.That(adminError!.SqlState, Is.EqualTo("42501"));
    }

    [Test]
    public async Task CompletedMergeAliasDoesNotPermanentlyBlockWhitelistOrWallet()
    {
        Links("INSERT INTO discord_account_merge_pending VALUES($other,$uid,$discord); INSERT INTO discord_account_aliases VALUES($other,$uid,$discord)",
            ("$other", _other.ToString()), ("$uid", _user.ToString()), ("$discord", Target));
        Assert.That((await Call("whitelist", new { discord_id = Target })).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task BankTransferAndExternalLcUpdatePreserveSeparateWallets()
    {
        await using (var setup = _settings.OpenDatabase())
        {
            var source = await setup.Profile.SingleAsync(p => p.Preference.UserId == _user && p.Slot == 0);
            source.BankBalance = 777; await setup.SaveChangesAsync();
        }
        async Task TransferMoney()
        {
            await using var db = _settings.OpenDatabase();
            await AccountCurrencyOperations.TransferBank(db, _user, 0, 777, _other, 0, 0, 25);
        }
        await Task.WhenAll(TransferMoney(), Call("lc_add", Change()));
        await using var fresh = _settings.OpenDatabase();
        var sender = await fresh.Preference.SingleAsync(p => p.UserId == _user);
        var recipient = await fresh.Preference.SingleAsync(p => p.UserId == _other);
        Assert.That(sender.Money, Is.EqualTo(777));
        Assert.That(recipient.Money, Is.Zero);
        Assert.That(await fresh.Profile.Where(p => p.Preference.UserId == _user && p.Slot == 0).Select(p => p.BankBalance).SingleAsync(), Is.EqualTo(752));
        Assert.That(await fresh.Profile.Where(p => p.Preference.UserId == _other && p.Slot == 0).Select(p => p.BankBalance).SingleAsync(), Is.EqualTo(25));
        Assert.That(sender.LagoonCoins, Is.EqualTo(10));
        Assert.That(recipient.LagoonCoins, Is.Zero);
    }

    [Test]
    public async Task InvalidAmountsAndOverflowCannotChangeBalanceOrCreateJournalEntries()
    {
        foreach (var amount in new[] { 0L, -1L, long.MaxValue })
            Assert.That((await Call("lc_add", Change(amount))).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        await using (var db = _settings.OpenDatabase())
            await db.Preference.Where(p => p.UserId == _user).ExecuteUpdateAsync(s => s.SetProperty(p => p.LagoonCoins, long.MaxValue));
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        await using var fresh = _settings.OpenDatabase();
        Assert.That(await fresh.Preference.Where(p => p.UserId == _user).Select(p => p.LagoonCoins).SingleAsync(), Is.EqualTo(long.MaxValue));
        Assert.That(await fresh.LagoonCoinOperations.AnyAsync(p => p.UserId == _user), Is.False);
    }

    [Test]
    public async Task ReadOnlyIdentityLeaseKeepsWalAvailableAfterLastWriterCloses()
    {
        using var ready = await _http.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using (var writer = new SqliteConnection($"Data Source={_settings.LinkDatabase};Pooling=False"))
        {
            writer.Open();
            using var command = writer.CreateCommand();
            command.CommandText = "UPDATE discord_links SET username=username";
            command.ExecuteNonQuery();
        }
        Assert.That(File.Exists(_settings.LinkDatabase + "-wal"), Is.True);
        Assert.That(File.Exists(_settings.LinkDatabase + "-shm"), Is.True);
        Assert.That((await Call("whitelist", new { discord_id = Target })).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("lc_add", Change())).Status, Is.EqualTo(HttpStatusCode.OK));
    }
}
