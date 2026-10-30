using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    private async Task EnableLauncherShop()
    {
        _http.Dispose(); await _app.DisposeAsync();
        _settings = _settings with { LauncherShopToken = new string('l', 64) };
        _app = await Program.CreateApplication(_settings); await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }

    [Test]
    public async Task LauncherUpgradePreservesPendingLegacyDiscordReceiptFingerprint()
    {
        await Fund(1000);
        // Reconstruct the exact pre-shop request shape, including its property order.
        var legacy = new { DiscordId = Target, ActorDiscordId = Target, RequestId = Receipt,
            HostAuthorized = false, TaskId = (string?)null, Category = (string?)null, Amount = 0, Reason = (string?)null,
            Tier = 2, Receipt = (string?)null, PaidAt = default(DateTimeOffset), Historical = false,
            Color = (string?)null, ReferralCode = (string?)null, ExternalTier = 0,
            NewcomerDiscordId = (string?)null, ObservedAt = default(DateTimeOffset), OperationId = (string?)null };
        var reason = System.Text.Json.JsonSerializer.Serialize(legacy);
        var current = new EconomyRequest(Target, Target, Receipt, Tier: 2);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(current), Is.EqualTo(reason));
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
            action = "economy_pass", DiscordId = Target, ActorDiscordId = Target, Amount = 0, reason });
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_settings.ReceiptDatabase};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS receipts(request_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, user_id TEXT NOT NULL, actor_id TEXT); INSERT INTO receipts VALUES($id,$hash,$uid,$uid)";
            cmd.Parameters.AddWithValue("$id", Receipt); cmd.Parameters.AddWithValue("$hash", fingerprint);
            cmd.Parameters.AddWithValue("$uid", _user.ToString()); cmd.ExecuteNonQuery();
        }
        var result = await Call("economy_pass", new { discord_id = Target, actor_discord_id = Target, request_id = Receipt, tier = 2 });
        Assert.That(result.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(result.Body.GetProperty("balance").GetInt64(), Is.EqualTo(400));
    }

    [Test]
    public async Task LauncherShopPassSurvivesRestartAndUsesNativeWalletAndCatalog()
    {
        await EnableLauncherShop(); await Fund(1000);
        var body = new { discord_id = Target, actor_discord_id = Target, request_id = "18123456789012345678", tier = 2, expected_uid = _user };
        var first = await Call("launcher_economy_pass", body, _settings.LauncherShopToken);
        Assert.That(first.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(first.Body.GetProperty("balance").GetInt64(), Is.EqualTo(400));
        Assert.That(first.Body.GetProperty("subscriptions")[1].GetProperty("price").GetInt32(), Is.EqualTo(Content.Shared.Database.LagoonEconomyRules.PassPrice(2)));
        await EnableLauncherShop();
        var replay = await Call("launcher_economy_pass", body, _settings.LauncherShopToken);
        Assert.That(replay.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(replay.Body.GetProperty("balance").GetInt64(), Is.EqualTo(400));
        Assert.That((await Economy(_user)).State.PassTier, Is.EqualTo(2));
    }

    [Test]
    public async Task LauncherShopKeyCannotUseStaffRoutesOtherScopesOrChangedMappings()
    {
        await EnableLauncherShop(); await Fund(1000);
        object Body(Guid? uid = null, bool host = false) => new { discord_id = Target, actor_discord_id = Target, request_id = Receipt,
            expected_uid = uid ?? _user, host_authorized = host, tier = 1 };
        foreach (var token in new[] { _settings.CoinToken, _settings.WhitelistToken, _settings.SupporterToken })
            Assert.That((await Call("launcher_economy_pass", Body(), token)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        foreach (var route in new[] { "lc_add", "economy_payment", "economy_supporter", "whitelist" })
            Assert.That((await Call(route, Body(), _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Call("launcher_economy_payment", Body(), _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await Call("launcher_economy_pass", Body(host: true), _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await Call("launcher_economy_pass", Body(_other), _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.Conflict));
        var missing = new { discord_id = Target, actor_discord_id = Target, request_id = Receipt, tier = 1 };
        Assert.That((await Call("launcher_economy_pass", missing, _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.Forbidden));
        Links("UPDATE discord_links SET ss14_uid=$uid WHERE discord_id=$id", ("$id", Target), ("$uid", _other.ToString()));
        Assert.That((await Call("launcher_economy_pass", Body(), _settings.LauncherShopToken)).Status, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(1000));
        Assert.That((await Economy(_other)).Balance, Is.Zero);
    }
}
