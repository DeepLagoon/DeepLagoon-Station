using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Content.Server.Database;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DeepLagoon.AccountService.Tests;

public sealed partial class AccountServiceTests
{
    private void LinkReviewer() => Links("INSERT INTO discord_links VALUES($id,$uid,'Reviewer')", ("$id", Actor), ("$uid", _other.ToString()));

    [Test]
    public async Task EconomyHttpTaskRequiresStaffAndIndependentReviewerAndPaysOnce()
    {
        LinkReviewer();
        var declared = await Call("economy_task_create", new { discord_id = Target, actor_discord_id = Target,
            request_id = Receipt, host_authorized = true, task_id = "HTTP-42", category = "code-small", amount = 250, reason = "Accepted fix" });
        Assert.That(declared.Status, Is.EqualTo(HttpStatusCode.OK));
        var forbidden = await Call("economy_task_approve", new { discord_id = Target, actor_discord_id = Actor,
            request_id = "1554164947804487790", host_authorized = false, task_id = "HTTP-42" });
        Assert.That(forbidden.Status, Is.EqualTo(HttpStatusCode.Forbidden));
        var self = await Call("economy_task_approve", new { discord_id = Target, actor_discord_id = Target,
            request_id = "1554164947804487791", host_authorized = true, task_id = "HTTP-42" });
        Assert.That(self.Status, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Economy(_user)).Balance, Is.Zero);
        foreach (var request in new[] { "1554164947804487792", "1554164947804487793" })
        {
            var accepted = await Call("economy_task_approve", new { discord_id = Target, actor_discord_id = Actor,
                request_id = request, host_authorized = true, task_id = "HTTP-42" });
            Assert.That(accepted.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(accepted.Body.GetProperty("balance").GetInt64(), Is.EqualTo(250));
        }
        var status = await Call("economy_status", new { discord_id = Target, actor_discord_id = Target,
            request_id = "1554164947804487794", task_id = "HTTP-42" });
        Assert.That(status.Body.GetProperty("status").GetString(), Is.EqualTo("committed"));
        Assert.That(status.Body.GetProperty("record").GetProperty("Approver").GetGuid(), Is.EqualTo(_other));
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.UserId == _user && p.OperationId == "task:HTTP-42"), Is.EqualTo(1));
    }

    [Test]
    public async Task EconomyHttpConcurrentPaymentIsGlobalAndCannotMoveToAnotherAccount()
    {
        LinkReviewer();
        var paidAt = DateTime.UtcNow;
        object Payment(string interaction, string recipient = Target, int tier = 2) => new { discord_id = recipient,
            actor_discord_id = Actor, request_id = interaction, host_authorized = true,
            receipt = "boosty:http-period", tier, paid_at = paidAt, historical = false };
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Call("economy_payment", Payment((1554164947804487800L + i).ToString()))));
        Assert.That(responses.Select(p => p.Status), Is.All.EqualTo(HttpStatusCode.OK));
        Assert.That(responses.Count(p => p.Body.GetProperty("applied").GetBoolean()), Is.EqualTo(1));
        Assert.That((await Economy(_user)).Balance, Is.EqualTo(100));
        Assert.That((await Call("economy_payment", Payment("1554164947804487810", Actor))).Status, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Call("economy_payment", Payment("1554164947804487811", tier: 3))).Status, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Economy(_other)).Balance, Is.Zero);
        await using var db = _settings.OpenDatabase();
        Assert.That(await db.LagoonCoinOperations.CountAsync(p => p.OperationId == "payment:boosty:http-period"), Is.EqualTo(1));
    }

    [Test]
    public async Task EconomyHttpPassReplayAfterRestartCannotChargeOrExtendTwice()
    {
        await Fund(1000);
        var request = new { discord_id = Target, actor_discord_id = Target, request_id = Receipt, tier = 2 };
        Assert.That((await Call("economy_pass", request)).Status, Is.EqualTo(HttpStatusCode.OK));
        var first = await Economy(_user);
        _http.Dispose();
        await _app.DisposeAsync();
        _app = await Program.CreateApplication(_settings);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var replay = await Call("economy_pass", request);
        Assert.That(replay.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(replay.Body.GetProperty("balance").GetInt64(), Is.EqualTo(400));
        var current = await Economy(_user);
        Assert.That(current.State.PassTicks, Is.InRange(first.State.PassTicks - TimeSpan.FromMinutes(1).Ticks, first.State.PassTicks));
        var status = await Call("economy_status", new { discord_id = Target, actor_discord_id = Target,
            request_id = "1554164947804487820", operation_id = "spend:discord:" + Receipt });
        Assert.That(status.Body.GetProperty("status").GetString(), Is.EqualTo("committed"));
        Assert.That(status.Body.GetProperty("amount").GetInt64(), Is.EqualTo(-600));
    }

    [Test]
    public async Task EconomyHttpColorReplayCannotOverwriteNewerColorAndFinancialBodyCannotRenewRights()
    {
        async Task Sync(int tier, DateTime observedAt) => Assert.That((await Call("economy_supporter", new {
            discord_id = Target, actor_discord_id = Target, request_id = Receipt, external_tier = tier, observed_at = observedAt })).Status, Is.EqualTo(HttpStatusCode.OK));
        var observation = DateTime.UtcNow;
        await Sync(1, observation);
        object Color(string key, string color) => new { discord_id = Target, actor_discord_id = Target, request_id = key, color, external_tier = 4 };
        var original = Color(Receipt, "#80C080");
        Assert.That((await Call("economy_color", original)).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("economy_color", Color("1554164947804487830", "#8080C0"))).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Call("economy_color", original)).Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await Economy(_user)).State.OocColor, Is.EqualTo("#8080C0"));
        await Sync(0, observation.AddMilliseconds(1));
        var forged = await Call("economy_color", Color("1554164947804487831", "#C08080"));
        Assert.That(forged.Status, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await Economy(_user)).State.ExternalTier, Is.Zero);
        var stale = await Call("economy_supporter", new { discord_id = Target, actor_discord_id = Target,
            request_id = "1554164947804487832", external_tier = 4, observed_at = observation.AddMinutes(-3) });
        Assert.That(stale.Status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task EconomyHttpInvalidPaymentAndAmbiguousStatusDoNotWrite()
    {
        LinkReviewer();
        var invalid = await Call("economy_payment", new { discord_id = Target, actor_discord_id = Actor,
            request_id = Receipt, host_authorized = true, receipt = "missing-payment-date", tier = 2 });
        Assert.That(invalid.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await Economy(_user)).Balance, Is.Zero);
        var ambiguous = await Call("economy_status", new { discord_id = Target, actor_discord_id = Target,
            request_id = "1554164947804487840", task_id = "anything", receipt = "anything" });
        Assert.That(ambiguous.Status, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task AccountStartupAndLivenessDoNotDependOnDatabaseReadiness()
    {
        var unavailable = _settings with { Engine = "sqlite", Connection = "Data Source=" + Path.Combine(_directory, "absent", "game.db") + ";Mode=ReadWrite;Pooling=False;Default Timeout=1" };
        var watch = Stopwatch.StartNew();
        await using var app = await Program.CreateApplication(unavailable);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(5) };
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        using var live = await client.GetAsync("/health/live");
        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var ready = await client.GetAsync("/health/ready");
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        using var payload = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.That(payload.RootElement.GetProperty("ready").GetBoolean(), Is.False);
        using var liveAgain = await client.GetAsync("/health/live");
        Assert.That(liveAgain.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
    [Test]
    public async Task ExpiredExternalLeaseDoesNotKeepLcPassPausedBetweenRequests()
    {
        await Fund(1000);
        var now = DateTime.UtcNow;
        await Buy("expiry-test-pass", "pass", "2", at: now);
        var observed = now.AddHours(1);
        await using (var db = _settings.OpenDatabase())
            await AccountEconomyOperations.SyncExternal(db, _user, 4, observed, observed);
        var expired = await Economy(_user, at: observed.AddMinutes(20));
        Assert.That(expired.State.ExternalTier, Is.Zero);
        var remaining = TimeSpan.FromDays(30).Ticks - TimeSpan.FromHours(1).Ticks - TimeSpan.FromMinutes(5).Ticks;
        Assert.That(expired.State.PassTicks, Is.EqualTo(remaining));
        var later = await Economy(_user, at: observed.AddMinutes(30));
        Assert.That(later.State.PassTicks, Is.EqualTo(remaining - TimeSpan.FromMinutes(10).Ticks));
    }

    [TestCase(0, false, false, 0)]
    [TestCase(0, true, false, 1)]
    [TestCase(0, false, true, 2)]
    [TestCase(0, true, true, 2)]
    [TestCase(1, false, true, 2)]
    [TestCase(2, false, true, 3)]
    [TestCase(3, false, true, 4)]
    public void NativeBoosterHasImpulseFloorWithoutStacking(int boosty, bool spark, bool booster, int expected)
        => Assert.That(Content.Shared.Database.LagoonEconomyRules.ProductTier(boosty, spark, booster), Is.EqualTo(expected));

    [Test]
    public async Task BoosterLeasePausesImpulsePassWithoutPaymentAndRevocationResumesIt()
    {
        await Fund(2000);
        var now = DateTime.UtcNow;
        await Buy("booster-pass", "pass", "2", at: now);
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.SyncExternal(db, _user, 2, now, now);
        var active = await Economy(_user, at: now.AddMinutes(5));
        Assert.That(active.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(30).Ticks));
        Assert.That(active.Balance, Is.EqualTo(1400));
        await using (var db = _settings.OpenDatabase()) Assert.That(await AccountEconomyOperations.PublicSupporters(db), Is.Empty);
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.SyncExternal(db, _user, 0, now.AddMinutes(6), now.AddMinutes(6));
        var ended = await Economy(_user, at: now.AddMinutes(10));
        Assert.That(ended.State.PassTicks, Is.EqualTo(TimeSpan.FromDays(30).Ticks - TimeSpan.FromMinutes(4).Ticks));
        var shine = await Buy("booster-upgrade", "pass", "3", at: now.AddMinutes(10));
        await using (var db = _settings.OpenDatabase()) await AccountEconomyOperations.SyncExternal(db, _user, 2, now.AddMinutes(11), now.AddMinutes(11));
        var higher = await Economy(_user, at: now.AddMinutes(12));
        Assert.That(higher.State.PassTicks, Is.EqualTo(shine.State.PassTicks - TimeSpan.FromMinutes(2).Ticks));
    }

}
